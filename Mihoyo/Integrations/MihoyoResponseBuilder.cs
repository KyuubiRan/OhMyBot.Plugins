using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Commanding.Notifications;

namespace OhMyBot.Core.Integrations.Mihoyo;

public sealed class MihoyoResponseBuilder(
    CallbackActionStore callbackStore,
    INotificationSubscriptionService subscriptionService,
    TimeProvider timeProvider)
{
    private const int AccountsPerPage = 8;
    private const int RolesPerPage = 6;

    private const string PreviousPageText = "<上一页";
    private const string NextPageText = "下一页>";
    private const string ToggleAllText = "开启/关闭全部";
    private const string NotifyHintLine = "如需管理签到结果的消息推送，请使用 `/notify`";

    private static readonly (long Flag, string Label)[] BbsTaskLabels =
    [
        (MihoyoBbsTaskFlags.SignIn, "签到"),
        (MihoyoBbsTaskFlags.ViewPosts, "浏览"),
        (MihoyoBbsTaskFlags.LikePosts, "点赞"),
        (MihoyoBbsTaskFlags.SharePosts, "分享")
    ];

    /// <summary>单页时不显示页码，好让四个插件的面板在常见情形下逐字一致。</summary>
    private static string PageSuffix(int page, int totalPages)
    {
        return totalPages <= 1 ? string.Empty : $"（第 {page + 1}/{totalPages} 页）";
    }

    public Task<CommandResponse> BuildAccountListAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.TelegramMarkdown(
            context.Identity, RenderAccountList(accounts), replyToMessageId: context.Request.MessageId);
        return Task.FromResult(response);
    }

    public CommandResponse BuildBindResult(CommandContext context, MihoyoBindResult result)
    {
        return CommandResponses.TelegramMarkdown(
            context.Identity, RenderBindResult(result), replyToMessageId: context.Request.MessageId);
    }

    public CommandResponse BuildBbsSignResult(CommandContext context, MihoyoBbsSignResult result, bool autoSign = false)
    {
        return CommandResponses.TelegramMarkdown(
            context.Identity,
            RenderSignResult("社区", result.Account, result.Lines, autoSign),
            replyToMessageId: context.Request.MessageId);
    }

    public CommandResponse BuildGameSignResult(CommandContext context, MihoyoGameSignResult result, bool autoSign = false)
    {
        return CommandResponses.TelegramMarkdown(
            context.Identity,
            RenderSignResult("游戏", result.Account, result.Lines, autoSign),
            replyToMessageId: context.Request.MessageId);
    }

    public async Task<CommandResponse> BuildBbsSignSelectionAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        IReadOnlyList<string> actions,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要执行社区任务的米游社账号（仅国服）：", context)
            .AsTelegramEditIfSpecified(editMessageId);
        var cnAccounts = accounts.Where(account => account.Region == MihoyoRegion.Cn).ToArray();
        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, cnAccounts, columns: 1, "mihoyo-bbs-sign-select",
            account => $"{account.DisplayName} #{account.Id}",
            account => new MihoyoBbsSignCallbackData(account.Id, actions.ToArray()),
            cancellationToken);

        if (cnAccounts.Length > 1)
        {
            await panel.AddRowAsync(
                response, "mihoyo-bbs-sign-all", "全部签到",
                new MihoyoBbsSignAllCallbackData(), cancellationToken);
        }

        return response;
    }

    public async Task<CommandResponse> BuildGameSignSelectionAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要执行游戏签到的米游社账号：", context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, accounts, columns: 1, "mihoyo-game-sign-panel",
            account => $"{account.DisplayName} #{account.Id} [{RegionLabel(account.Region)}]",
            account => new MihoyoGameSignPanelCallbackData(account.Id),
            cancellationToken);

        if (accounts.Count > 1)
        {
            await panel.AddRowAsync(
                response, "mihoyo-game-sign-all", "全部签到",
                new MihoyoGameSignAllCallbackData(), cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// 游戏签到勾选面板：每个游戏一个开关按钮（√ 签到 / × 跳过），底部为「签到」「返回」。
    /// </summary>
    public async Task<CommandResponse> BuildGameSignPanelAsync(
        CommandContext context,
        MihoyoAccount account,
        IReadOnlyCollection<string> selected,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var available = AvailableGameKeys(account);
        if (available.Count == 0)
        {
            return CommandResponses.Text(
                    $"账号 #{account.Id} {account.DisplayName} 暂无游戏角色，请先使用 /mihoyo game init {account.Id} 同步", context)
                .AsTelegramEditIfSpecified(editMessageId);
        }

        var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            "[米游社-手动游戏签到]",
            $"账号：#{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]",
            "勾选要签到的游戏（√=签到 ×=跳过），然后点击「签到」："
        };
        lines.AddRange(available.Select(key => FormatGameRolesLine(account, MihoyoGameCatalog.FindByKey(key)!)));
        var response = CommandResponses.Text(string.Join('\n', lines), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response, available, columns: 1, "mihoyo-game-sign-panel",
            key => $"{(selectedSet.Contains(key) ? "[√]" : "[×]")} {MihoyoGameCatalog.FindByKey(key)!.Name}",
            key => new MihoyoGameSignPanelCallbackData(account.Id, Toggle: key),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync("mihoyo-game-sign-run", "签到",
                new MihoyoGameSignPanelCallbackData(account.Id), cancellationToken),
            await panel.ButtonAsync("mihoyo-game-sign-back", "返回",
                new MihoyoGameSignBackCallbackData(), cancellationToken)));
        return response;
    }

    /// <summary>
    /// 全部账号游戏签到的合并结果（纯文本）。
    /// </summary>
    public CommandResponse BuildCombinedResult(
        CommandContext context,
        string title,
        IReadOnlyList<(MihoyoAccount Account, IReadOnlyList<string> Lines)> results,
        string? editMessageId = null)
    {
        var blocks = new List<string> { title };
        foreach (var (account, lines) in results)
        {
            blocks.Add(string.Empty);
            blocks.Add($"#{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]");
            blocks.AddRange(lines.Count == 0 ? ["无结果"] : lines);
        }

        blocks.Add(string.Empty);
        blocks.Add("时间：" + timeProvider.GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        var response = CommandResponses.Text(string.Join('\n', blocks), context)
            .AsTelegramEditIfSpecified(editMessageId);
        return response;
    }

    /// <summary>账号已同步角色去重后的游戏 Key，按目录顺序排列。</summary>
    public static IReadOnlyList<string> AvailableGameKeys(MihoyoAccount account)
    {
        var owned = account.Roles
            .Select(role => MihoyoGameCatalog.FindByGameBiz(role.GameBiz)?.Key)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MihoyoGameCatalog.Games
            .Where(game => owned.Contains(game.Key))
            .Select(game => game.Key)
            .ToArray();
    }

    /// <summary>“显式清空（无勾选）”的存储标记，用于与“未设置（默认全选）”区分。</summary>
    public const string NoneSelectionSentinel = "-";

    /// <summary>面板初始勾选：账号上次持久化的选择（与当前可用游戏取交集）；未设置时默认全选，显式清空则为空。</summary>
    public static IReadOnlyList<string> ResolveGameSignSelection(MihoyoAccount account)
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
        return available.Where(key => stored.Contains(key)).ToArray();
    }

    /// <summary>把勾选集合序列化为存储字符串（空集合存为清空标记）。</summary>
    public static string SerializeGameSignSelection(IReadOnlyCollection<string> selected)
    {
        return selected.Count == 0 ? NoneSelectionSentinel : string.Join(',', selected);
    }

    public async Task<CommandResponse> BuildDeletePanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要删除的米游社账号：", context);
        await new PanelBuilder(callbackStore, context).AddGridAsync(
            response, accounts, columns: 1, "mihoyo-delete-select",
            account => $"{account.DisplayName} #{account.Id} [{RegionLabel(account.Region)}]",
            account => new MihoyoAccountCallbackData(account.Id),
            cancellationToken);

        return response;
    }

    public async Task<CommandResponse> BuildAutoSignPanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var totalPages = Pagination.TotalPages(accounts.Count, AccountsPerPage);
        page = Pagination.NormalizePage(page, accounts.Count, AccountsPerPage);
        var response = CommandResponses.Text(BuildAutoSignText(accounts, page, totalPages), context)
            .AsTelegramEditIfSpecified(editMessageId);
        if (accounts.Count == 0)
        {
            return response;
        }

        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response,
            Pagination.Slice(accounts, page, AccountsPerPage),
            columns: 2,
            "mihoyo-autosign-account-menu",
            // 区服标记只放正文，按钮里塞进去会让 Telegram 双列按钮过宽。
            account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} {account.DisplayName} #{account.Id}",
            account => new MihoyoAutoSignMenuCallbackData(account.Id, "account", page),
            cancellationToken);
        var toggleAll = await panel.ButtonAsync(
            "mihoyo-auto-sign-toggle", ToggleAllText,
            new MihoyoAutoSignCallbackData(0, ToggleAll: true, Page: page), cancellationToken);
        await panel.AddPagerAsync(
            response, "mihoyo-autosign-root-menu", page, totalPages,
            target => new MihoyoAutoSignMenuCallbackData(0, "root", target),
            PreviousPageText, NextPageText, cancellationToken, middleButton: toggleAll);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("MihoyoAccountMissing", "未找到指定米游社账号", context);
        }

        var response = CommandResponses.Text(BuildAutoSignAccountDetailText(account), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddRowAsync(
            response, "mihoyo-auto-sign-toggle",
            account.AutoSignEnabled ? "[开] 总开关" : "[关] 总开关",
            new MihoyoAutoSignCallbackData(account.Id, Page: page), cancellationToken);

        var menuRow = new ResponseButtonRow();
        if (account.Region == MihoyoRegion.Cn)
        {
            menuRow.Buttons.Add(await panel.ButtonAsync(
                "mihoyo-autosign-bbs-menu", "社区任务",
                new MihoyoAutoSignMenuCallbackData(account.Id, "bbs", page), cancellationToken));
        }

        menuRow.Buttons.Add(await panel.ButtonAsync(
            "mihoyo-autosign-game-menu", "游戏角色",
            new MihoyoAutoSignMenuCallbackData(account.Id, "game"), cancellationToken));
        response.AddButtonRow(menuRow);

        await panel.AddRowAsync(
            response, "mihoyo-autosign-root-menu", "返回账号列表",
            new MihoyoAutoSignMenuCallbackData(0, "root", page), cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignBbsPanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("MihoyoAccountMissing", "未找到指定米游社账号", context);
        }

        var response = CommandResponses.Text(BuildAutoSignBbsText(account), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response, BbsTaskLabels, columns: 2, "mihoyo-bbs-task-toggle",
            item => $"{(((account.BbsTaskFlags & item.Flag) != 0) ? "[开]" : "[关]")} {item.Label}",
            item => new MihoyoBbsTaskCallbackData(account.Id, item.Flag),
            cancellationToken);
        await panel.AddRowAsync(
            response, "mihoyo-bbs-task-toggle-all", ToggleAllText,
            new MihoyoBbsTaskToggleAllCallbackData(account.Id), cancellationToken);
        await panel.AddRowAsync(
            response, "mihoyo-autosign-account-menu", "返回",
            new MihoyoAutoSignMenuCallbackData(account.Id, "account", page), cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignGamePanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("MihoyoAccountMissing", "未找到指定米游社账号", context);
        }

        var orderedRoles = account.Roles.OrderBy(role => role.GameBiz).ThenBy(role => role.GameUid).ToArray();
        var totalPages = Pagination.TotalPages(orderedRoles.Length, RolesPerPage);
        page = Pagination.NormalizePage(page, orderedRoles.Length, RolesPerPage);
        var response = CommandResponses.Text(BuildAutoSignGameText(account, orderedRoles, page, totalPages), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response,
            Pagination.Slice(orderedRoles, page, RolesPerPage),
            columns: 1,
            "mihoyo-game-auto-sign-toggle",
            role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {FormatRole(role)}",
            role => new MihoyoGameAutoSignCallbackData(role.Id, account.Id, page),
            cancellationToken);
        await panel.AddPagerAsync(
            response, "mihoyo-autosign-game-menu", page, totalPages,
            target => new MihoyoAutoSignMenuCallbackData(account.Id, "game", target),
            PreviousPageText, NextPageText, cancellationToken);
        await panel.AddRowAsync(
            response, "mihoyo-game-auto-sign-toggle-all", ToggleAllText,
            new MihoyoGameAutoSignToggleAllCallbackData(account.Id, page), cancellationToken);
        await panel.AddRowAsync(
            response, "mihoyo-autosign-account-menu", "返回",
            new MihoyoAutoSignMenuCallbackData(account.Id, "account"), cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildNotifyAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<MihoyoAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var enabled = await subscriptionService.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            NotificationTypes.MihoyoAutoSign,
            accounts.Select(account => account.Id).ToArray(),
            cancellationToken);
        var response = CommandResponses.TelegramMarkdown(
            context.Identity,
            RenderNotifyAccountPanel(accounts, enabled),
            replyToMessageId: editMessageId is null ? context.Request.MessageId : null,
            editMessageId: editMessageId);

        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response,
            accounts,
            columns: 2,
            "notify-account-toggle",
            account => $"{(enabled.Contains(account.Id) ? "[开]" : "[关]")} {account.DisplayName}",
            account => new NotifyAccountCallbackData(NotificationTypes.MihoyoAutoSign, account.Id, ToggleAll: false),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync("notify-account-toggle", ToggleAllText,
                new NotifyAccountCallbackData(NotificationTypes.MihoyoAutoSign, 0, ToggleAll: true), cancellationToken),
            await panel.ButtonAsync("notify-back", "返回", new NotifyBackCallbackData(), cancellationToken)));
        return response;
    }

    // ---- Telegram MarkdownV2 渲染（原先在 TelegramGateway 的 renderer 内） ----

    private static string RenderAccountList(IReadOnlyList<MihoyoAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return "尚未绑定米游社账号";
        }

        var lines = new List<string> { MarkdownV2.Escape("[米游社]"), "已绑定账号：" };
        foreach (var account in accounts)
        {
            lines.Add($"\\- `#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}` \\[{MarkdownV2.Escape(RegionLabel(account.Region))}\\]：自动签到{MarkdownV2.Escape(account.AutoSignEnabled ? "开启" : "关闭")}");
            foreach (var role in OrderRoles(account))
            {
                lines.Add($"  \\- {MarkdownV2.Escape(role.GameName)}{MarkdownV2.Escape(FormatRoleSuffix(role))}：{MarkdownV2.Escape(role.AutoSignEnabled ? "自动签到开启" : "自动签到关闭")}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderBindResult(MihoyoBindResult result)
    {
        var account = result.Account;
        var lines = new List<string>
        {
            MarkdownV2.Escape(result.UpdatedExisting ? "米游社账号已更新" : "米游社账号绑定成功"),
            $"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}` \\[{MarkdownV2.Escape(RegionLabel(account.Region))}\\]",
            $"UID：`{account.Stuid}`"
        };
        if (account.Roles.Count > 0)
        {
            lines.Add("角色：");
            lines.AddRange(OrderRoles(account).Select(role => $"\\- {MarkdownV2.Escape(role.GameName)}{MarkdownV2.Escape(FormatRoleSuffix(role))}"));
        }

        return string.Join('\n', lines);
    }

    private string RenderSignResult(string kind, MihoyoAccount account, IReadOnlyList<string> resultLines, bool autoSign)
    {
        var title = $"[米游社-{(autoSign ? "自动" : "手动")}{kind}签到]";
        var lines = new List<string>
        {
            MarkdownV2.Escape(title),
            $"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}` \\[{MarkdownV2.Escape(RegionLabel(account.Region))}\\]"
        };
        lines.AddRange(resultLines.Select(MarkdownV2.Escape));
        var occurredAt = timeProvider.GetUtcNow().ToLocalTime();
        lines.Add($"时间：{MarkdownV2.Escape(occurredAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        return string.Join('\n', lines);
    }

    private static string RenderNotifyAccountPanel(IReadOnlyList<MihoyoAccount> accounts, IReadOnlySet<long> enabled)
    {
        var enabledNames = accounts
            .Where(account => enabled.Contains(account.Id))
            .Select(account => $"`{MarkdownV2.Code(account.DisplayName)}`")
            .ToArray();
        return string.Join('\n',
            MarkdownV2.Escape($"[消息订阅 · {NotificationTypes.MihoyoAutoSignDisplayName}]"),
            MarkdownV2.Escape("当前已启用：") + (enabledNames.Length == 0 ? MarkdownV2.Escape("无") : string.Join(MarkdownV2.Escape("、"), enabledNames)),
            MarkdownV2.Escape("此处为消息订阅管理，仅控制签到结果是否推送；开关自动签到请使用 ") + MarkdownV2.CodeSpan("/mihoyo autosign"));
    }

    private static IEnumerable<MihoyoGameRole> OrderRoles(MihoyoAccount account)
    {
        return account.Roles.OrderBy(role => role.GameBiz).ThenBy(role => role.GameUid);
    }

    private static string FormatRoleSuffix(MihoyoGameRole role)
    {
        if (role.GameUid <= 0)
        {
            return string.Empty;
        }

        var level = string.IsNullOrWhiteSpace(role.Level) ? string.Empty : $" Lv.{role.Level}";
        return $" / {role.Nickname} ({role.GameUid}){level}";
    }






    private static string BuildAutoSignText(IReadOnlyList<MihoyoAccount> accounts, int page, int totalPages)
    {
        if (accounts.Count == 0)
        {
            return "尚未绑定米游社账号";
        }

        var lines = new List<string>
        {
            "[米游社-自动签到]",
            "点击账号进入设置" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(accounts, page, AccountsPerPage)
            .Select(account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} #{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]"));
        lines.Add(NotifyHintLine);
        return string.Join('\n', lines);
    }

    private static string BuildAutoSignAccountDetailText(MihoyoAccount account)
    {
        // 社区任务只有国服账号有，用 null 让这一行整体消失。
        return TextLayout.JoinLines(
            "[米游社-自动签到]",
            $"账号：#{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]",
            $"总开关：{(account.AutoSignEnabled ? "开启" : "关闭")}",
            account.Region == MihoyoRegion.Cn ? $"社区任务：{FormatBbsTasks(account.BbsTaskFlags)}" : null,
            "游戏角色：" + FormatGameRoles(account));
    }

    private static string BuildAutoSignBbsText(MihoyoAccount account)
    {
        return string.Join('\n',
            "[米游社-自动签到 - 社区任务]",
            $"账号：#{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]",
            "点击任务开关自动签到：",
            $"当前已启用：{FormatBbsTasks(account.BbsTaskFlags)}");
    }

    private static string BuildAutoSignGameText(
        MihoyoAccount account,
        IReadOnlyList<MihoyoGameRole> roles,
        int page,
        int totalPages)
    {
        var lines = new List<string>
        {
            "[米游社-自动签到 - 游戏角色]",
            $"账号：#{account.Id} {account.DisplayName} [{RegionLabel(account.Region)}]",
            "点击角色开关自动签到" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(roles, page, RolesPerPage)
            .Select(role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {FormatRole(role)}"));
        return string.Join('\n', lines);
    }

    private static string FormatRole(MihoyoGameRole role)
    {
        return role.GameUid > 0 ? $"{role.GameName}/{role.Nickname}" : role.GameName;
    }

    /// <summary>面板中单个游戏一行：游戏名 + 各角色昵称(UID) Lv.等级。</summary>
    private static string FormatGameRolesLine(MihoyoAccount account, MihoyoGameDef game)
    {
        var details = account.Roles
            .Where(role => string.Equals(role.GameBiz, game.CnGameBiz, StringComparison.OrdinalIgnoreCase) && role.GameUid > 0)
            .Select(role => $"{role.Nickname}({role.GameUid}){(string.IsNullOrWhiteSpace(role.Level) ? string.Empty : " Lv." + role.Level)}")
            .ToArray();
        return details.Length == 0 ? $"- {game.Name}" : $"- {game.Name}：{string.Join("、", details)}";
    }

    private static string FormatGameRoles(MihoyoAccount account, bool onlyEnabled = false)
    {
        return TextLayout.JoinOrEmpty(
            account.Roles.Where(role => !onlyEnabled || role.AutoSignEnabled).Select(FormatRole),
            "、",
            "无");
    }

    private static string FormatBbsTasks(long flags)
    {
        return TextLayout.JoinOrEmpty(
            BbsTaskLabels.Where(item => (flags & item.Flag) != 0).Select(item => item.Label),
            "、",
            "无");
    }

    private static string RegionLabel(MihoyoRegion region)
    {
        return region == MihoyoRegion.Cn ? "国服" : "国际服";
    }


}
