using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Data.Entities;

namespace OhMyBot.Core.Integrations.Skland;

public sealed class SklandResponseBuilder(
    CallbackActionStore callbackStore,
    INotificationSubscriptionService subscriptionService,
    TimeProvider timeProvider)
{
    private const int AccountsPerPage = 8;
    private const int RolesPerPage = 6;

    private const string PreviousPageText = "上一页";
    private const string NextPageText = "下一页";
    private const string ToggleAllText = "开启/关闭全部";
    private const string NotifyHintLine = "如需管理签到结果的消息推送，请使用 `/notify`";

    /// <summary>单页时不显示页码，好让四个插件的面板在常见情形下逐字一致。</summary>
    private static string PageSuffix(int page, int totalPages)
    {
        return totalPages <= 1 ? string.Empty : $"（第 {page + 1}/{totalPages} 页）";
    }

    // ---- Account list / bind ----

    public async Task<CommandResponse> BuildAccountListAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var markdown = RenderAccountListMarkdown(accounts);
        return await Task.FromResult(CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId));
    }

    public CommandResponse BuildBindResult(CommandContext context, SklandBindResult result)
    {
        var markdown = RenderBindResultMarkdown(result);
        return CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
    }

    // ---- Sign results ----

    public CommandResponse BuildGameSignResult(
        CommandContext context,
        SklandGameSignResult result,
        bool autoSign = false)
    {
        var title = autoSign ? "[森空岛-自动签到]" : "[森空岛-手动签到]";
        var markdown = RenderSignResultMarkdown(title, result.Account, result.Lines);
        return CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
    }

    public CommandResponse BuildCombinedGameSignResult(
        CommandContext context,
        IReadOnlyList<(SklandAccount Account, IReadOnlyList<string> Lines)> results,
        string? editMessageId = null)
    {
        var blocks = new List<string> { MarkdownV2.Escape("[森空岛-手动签到 - 全部账号]") };
        foreach (var (account, lines) in results)
        {
            blocks.Add($"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}`");
            blocks.AddRange(lines.Select(MarkdownV2.Escape));
        }

        var occurredAt = timeProvider.GetUtcNow().ToLocalTime();
        blocks.Add($"时间：{MarkdownV2.Escape(occurredAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        var response = CommandResponses.TelegramMarkdown(
            context.Identity, string.Join('\n', blocks), replyToMessageId: context.Request.MessageId);
        return response.AsTelegramEditIfSpecified(editMessageId);
    }

    // ---- Game sign selection (multi-account) ----

    public async Task<CommandResponse> BuildGameSignSelectionAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要执行游戏签到的森空岛账号：", context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, accounts, columns: 1, "skland-game-sign-panel",
            account => $"{account.DisplayName} #{account.Id}",
            account => new SklandGameSignPanelCallbackData(account.Id),
            cancellationToken);

        if (accounts.Count > 1)
        {
            await panel.AddRowAsync(
                response, "skland-game-sign-all", "全部签到", new SklandGameSignAllCallbackData(), cancellationToken);
        }

        return response;
    }

    // ---- Game sign panel (single account, role toggle) ----

    public async Task<CommandResponse> BuildGameSignPanelAsync(
        CommandContext context,
        SklandAccount account,
        IReadOnlyCollection<string> selected,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var available = AvailableGameKeys(account);
        if (available.Count == 0)
        {
            return CommandResponses.Text(
                    $"账号 #{account.Id} {account.DisplayName} 暂无角色，请先使用 /skland game init {account.Id} 同步", context)
                .AsTelegramEditIfSpecified(editMessageId);
        }

        var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            "[森空岛-手动签到]",
            $"账号：#{account.Id} {account.DisplayName}",
            "勾选要签到的游戏（√=签到 ×=跳过），然后点击「签到」："
        };
        lines.AddRange(available.Select(key => FormatGameLine(account, key)));
        var response = CommandResponses.Text(string.Join('\n', lines), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response, available, columns: 1, "skland-game-sign-panel",
            key => $"{(selectedSet.Contains(key) ? "[√]" : "[×]")} {SklandGameNames.Format(SklandGameNames.FromAppCode(key))}",
            key => new SklandGameSignPanelCallbackData(account.Id, Toggle: key),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync("skland-game-sign-run", "签到",
                new SklandGameSignPanelCallbackData(account.Id), cancellationToken),
            await panel.ButtonAsync("skland-game-sign-back", "返回",
                new SklandGameSignBackCallbackData(), cancellationToken)));
        return response;
    }

    /// <summary>账号已同步角色去重后的游戏 appCode，按目录顺序排列。</summary>
    public static IReadOnlyList<string> AvailableGameKeys(SklandAccount account)
    {
        var owned = account.Roles.Select(role => role.GameId).ToHashSet();
        return SklandGameNames.Order
            .Where(owned.Contains)
            .Select(SklandGameNames.ToAppCode)
            .ToArray();
    }

    /// <summary>“显式清空（无勾选）”的存储标记，用于与“未设置（默认全选）”区分。</summary>
    public const string NoneSelectionSentinel = "-";

    /// <summary>面板初始勾选：账号上次持久化的选择（与当前可用游戏取交集）；未设置时默认全选，显式清空则为空。</summary>
    public static IReadOnlyList<string> ResolveGameSignSelection(SklandAccount account)
    {
        var available = AvailableGameKeys(account);
        if (string.IsNullOrEmpty(account.GameSignSelection))
        {
            return available;
        }

        if (account.GameSignSelection == NoneSelectionSentinel)
        {
            return [];
        }

        var stored = account.GameSignSelection
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return available.Where(stored.Contains).ToArray();
    }

    /// <summary>把勾选集合序列化为存储字符串（空集合存为清空标记）。</summary>
    public static string SerializeGameSignSelection(IReadOnlyCollection<string> selected)
    {
        return selected.Count == 0 ? NoneSelectionSentinel : string.Join(',', selected);
    }

    // ---- Auto-sign management（二级菜单：账号列表 → 账号内角色开关） ----

    public async Task<CommandResponse> BuildAutoSignPanelAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var totalPages = Pagination.TotalPages(accounts.Count, AccountsPerPage);
        page = Pagination.NormalizePage(page, accounts.Count, AccountsPerPage);
        if (accounts.Count == 0)
        {
            // 与 Kuro / Mihoyo 一致：无账号时给未绑定提示，而不是渲染一个只有标题的空列表。
            return CommandResponses.Text("尚未绑定森空岛账号", context)
                .AsTelegramEditIfSpecified(editMessageId);
        }

        var lines = new List<string>
        {
            "[森空岛-自动签到]",
            "点击账号进入设置" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(accounts, page, AccountsPerPage)
            .Select(account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} #{account.Id} {account.DisplayName}"));
        lines.Add(NotifyHintLine);

        var response = CommandResponses.Text(string.Join('\n', lines), context)
            .AsTelegramEditIfSpecified(editMessageId);

        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response,
            Pagination.Slice(accounts, page, AccountsPerPage),
            columns: 2,
            "skland-autosign-account-menu",
            account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} {account.DisplayName} #{account.Id}",
            account => new SklandAutoSignMenuCallbackData(account.Id, "account", page),
            cancellationToken);
        await panel.AddPagerAsync(
            response, "skland-autosign-root-menu", page, totalPages,
            target => new SklandAutoSignMenuCallbackData(0, "root", target),
            PreviousPageText, NextPageText, cancellationToken);
        await panel.AddRowAsync(
            response, "skland-auto-sign-toggle", ToggleAllText,
            new SklandAutoSignCallbackData(0, ToggleAll: true, Page: page), cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("SklandAccountMissing", "未找到指定森空岛账号", context)
                .AsTelegramEditIfSpecified(editMessageId);
        }

        var roles = account.Roles.OrderBy(role => role.GameId).ThenBy(role => role.Id).ToArray();
        var totalPages = Pagination.TotalPages(roles.Length, RolesPerPage);
        page = Pagination.NormalizePage(page, roles.Length, RolesPerPage);
        var lines = new List<string>
        {
            "[森空岛-自动签到]",
            $"账号：#{account.Id} {account.DisplayName}",
            $"总开关：{(account.AutoSignEnabled ? "开启" : "关闭")}",
            "点击角色开关自动签到" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(roles, page, RolesPerPage)
            .Select(role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {role.GameName}/{role.NickName}"));

        var response = CommandResponses.Text(string.Join('\n', lines), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddRowAsync(
            response, "skland-auto-sign-toggle",
            account.AutoSignEnabled ? "[开] 总开关" : "[关] 总开关",
            new SklandAutoSignCallbackData(account.Id, Page: page), cancellationToken);
        await panel.AddGridAsync(
            response,
            Pagination.Slice(roles, page, RolesPerPage),
            columns: 1,
            "skland-game-auto-sign-toggle",
            role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {role.GameName}/{role.NickName}",
            role => new SklandGameAutoSignCallbackData(role.Id, account.Id),
            cancellationToken);
        await panel.AddPagerAsync(
            response, "skland-autosign-account-menu", page, totalPages,
            target => new SklandAutoSignMenuCallbackData(account.Id, "account", target),
            PreviousPageText, NextPageText, cancellationToken);

        if (roles.Length > 0)
        {
            await panel.AddRowAsync(
                response, "skland-game-auto-sign-toggle-all", ToggleAllText,
                new SklandGameAutoSignToggleAllCallbackData(account.Id), cancellationToken);
        }

        await panel.AddRowAsync(
            response, "skland-autosign-root-menu", "返回账号列表",
            new SklandAutoSignMenuCallbackData(0, "root", page), cancellationToken);
        return response;
    }

    // ---- Notify subscription panel (unified /notify 菜单) ----

    public async Task<CommandResponse> BuildNotifyAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var enabled = await subscriptionService.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            NotificationTypes.SklandAutoSign,
            accounts.Select(account => account.Id).ToArray(),
            cancellationToken);
        var response = CommandResponses.TelegramMarkdown(
            context.Identity,
            RenderNotifyAccountPanel(accounts, enabled),
            replyToMessageId: editMessageId is null ? context.Request.MessageId : null,
            editMessageId: editMessageId);

        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, accounts, columns: 2, "notify-account-toggle",
            account => $"{(enabled.Contains(account.Id) ? "[开]" : "[关]")} {account.DisplayName}",
            account => new NotifyAccountCallbackData(NotificationTypes.SklandAutoSign, account.Id, ToggleAll: false),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync("notify-account-toggle", ToggleAllText,
                new NotifyAccountCallbackData(NotificationTypes.SklandAutoSign, 0, ToggleAll: true), cancellationToken),
            await panel.ButtonAsync("notify-back", "返回", new NotifyBackCallbackData(), cancellationToken)));
        return response;
    }

    private static string RenderNotifyAccountPanel(IReadOnlyList<SklandAccount> accounts, IReadOnlySet<long> enabled)
    {
        var enabledNames = accounts
            .Where(account => enabled.Contains(account.Id))
            .Select(account => $"`{MarkdownV2.Code(account.DisplayName)}`")
            .ToArray();
        return string.Join('\n',
            MarkdownV2.Escape($"[消息订阅 · {NotificationTypes.SklandAutoSignDisplayName}]"),
            MarkdownV2.Escape("当前已启用：") + (enabledNames.Length == 0 ? MarkdownV2.Escape("无") : string.Join(MarkdownV2.Escape("、"), enabledNames)),
            MarkdownV2.Escape("此处为消息订阅管理，仅控制签到结果是否推送；开关自动签到请使用 ") + MarkdownV2.CodeSpan("/skland autosign"));
    }

    // ---- Delete panel ----

    public async Task<CommandResponse> BuildDeletePanelAsync(
        CommandContext context,
        IReadOnlyList<SklandAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要删除的森空岛账号：", context);
        await new PanelBuilder(callbackStore, context).AddGridAsync(
            response, accounts, columns: 1, "skland-delete-select",
            account => $"{account.DisplayName} #{account.Id}",
            account => new SklandAccountCallbackData(account.Id),
            cancellationToken);

        return response;
    }

    // ---- Markdown renderers ----

    private static string RenderAccountListMarkdown(IReadOnlyList<SklandAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return "尚未绑定森空岛账号";
        }

        var lines = new List<string> { MarkdownV2.Escape("[森空岛]"), "已绑定账号：" };
        foreach (var account in accounts)
        {
            lines.Add($"\\- `#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}` \\({MarkdownV2.Escape(account.SklandUserId)}\\)：自动签到{MarkdownV2.Escape(account.AutoSignEnabled ? "开启" : "关闭")}");
            foreach (var role in account.Roles)
            {
                var level = string.IsNullOrWhiteSpace(role.Level) ? string.Empty : $" Lv\\.{MarkdownV2.Escape(role.Level)}";
                lines.Add($"  \\- {MarkdownV2.Escape(role.GameName)} \\| `{MarkdownV2.Code(role.NickName)}`{level}：{MarkdownV2.Escape(role.AutoSignEnabled ? "自动签到开启" : "自动签到关闭")}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderBindResultMarkdown(SklandBindResult result)
    {
        var account = result.Account;
        var lines = new List<string>
        {
            MarkdownV2.Escape(result.UpdatedExisting ? "森空岛账号已更新" : "森空岛账号绑定成功"),
            $"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}`",
            $"SklandUID：`{MarkdownV2.Escape(account.SklandUserId)}`"
        };
        if (account.Roles.Count > 0)
        {
            lines.Add("角色：");
            foreach (var role in account.Roles)
            {
                var level = string.IsNullOrWhiteSpace(role.Level) ? string.Empty : $" Lv\\.{MarkdownV2.Escape(role.Level)}";
                lines.Add($"\\- {MarkdownV2.Escape(role.GameName)} \\| `{MarkdownV2.Code(role.NickName)}`{level} \\({MarkdownV2.Escape(role.ChannelName)}\\)");
            }
        }

        return string.Join('\n', lines);
    }

    private string RenderSignResultMarkdown(string title, SklandAccount account, IReadOnlyList<string> resultLines)
    {
        var lines = new List<string>
        {
            MarkdownV2.Escape(title),
            $"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}`"
        };
        lines.AddRange(resultLines.Select(MarkdownV2.Escape));
        var occurredAt = timeProvider.GetUtcNow().ToLocalTime();
        lines.Add($"时间：{MarkdownV2.Escape(occurredAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        return string.Join('\n', lines);
    }

    private static string FormatGameLine(SklandAccount account, string appCode)
    {
        var gameId = SklandGameNames.FromAppCode(appCode);
        var names = string.Join("、", account.Roles.Where(role => role.GameId == gameId).Select(role => role.NickName));
        return $"- {SklandGameNames.Format(gameId)}：{names}";
    }

    // ---- Button helpers ----



}
