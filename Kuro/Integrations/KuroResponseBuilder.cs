using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Commanding.Notifications;

namespace OhMyBot.Core.Integrations.Kuro;

public sealed class KuroResponseBuilder(
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
        (KuroBbsTaskFlags.SignIn, "签到"),
        (KuroBbsTaskFlags.ViewPosts, "浏览"),
        (KuroBbsTaskFlags.LikePosts, "点赞"),
        (KuroBbsTaskFlags.SharePosts, "分享")
    ];

    /// <summary>单页时不显示页码，好让四个插件的面板在常见情形下逐字一致。</summary>
    private static string PageSuffix(int page, int totalPages)
    {
        return totalPages <= 1 ? string.Empty : $"（第 {page + 1}/{totalPages} 页）";
    }

    public async Task<CommandResponse> BuildAccountListAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var markdown = RenderAccountListMarkdown(accounts);
        var response = CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
        return await Task.FromResult(response);
    }

    public CommandResponse BuildBindResult(CommandContext context, KuroBindResult result)
    {
        var markdown = RenderBindResultMarkdown(result);
        return CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
    }

    public CommandResponse BuildBbsSignResult(
        CommandContext context,
        KuroBbsSignResult result,
        bool autoSign = false)
    {
        var title = autoSign ? "[库街区-自动社区签到]" : "[库街区-手动社区签到]";
        var markdown = RenderSignResultMarkdown(title, result.Account, result.Lines);
        return CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
    }

    public CommandResponse BuildGameSignResult(
        CommandContext context,
        KuroGameSignResult result,
        bool autoSign = false)
    {
        var title = autoSign ? "[库街区-自动游戏签到]" : "[库街区-手动游戏签到]";
        var markdown = RenderSignResultMarkdown(title, result.Account, result.Lines);
        return CommandResponses.TelegramMarkdown(
            context.Identity, markdown, replyToMessageId: context.Request.MessageId);
    }

    public async Task<CommandResponse> BuildBbsSignSelectionAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        IReadOnlyList<string> actions,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要执行社区签到的库街区账号：", context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, accounts, columns: 1, "kuro-bbs-sign-select",
            account => $"{account.DisplayName} #{account.Id}",
            account => new KuroBbsSignCallbackData(account.Id, actions.ToArray()),
            cancellationToken);

        if (accounts.Count > 1)
        {
            await panel.AddRowAsync(
                response, "kuro-bbs-sign-all", "全部签到", new KuroBbsSignAllCallbackData(), cancellationToken);
        }

        return response;
    }

    public async Task<CommandResponse> BuildGameSignSelectionAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要执行游戏签到的库街区账号：", context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response, accounts, columns: 1, "kuro-game-sign-panel",
            account => $"{account.DisplayName} #{account.Id}",
            account => new KuroGameSignPanelCallbackData(account.Id),
            cancellationToken);

        if (accounts.Count > 1)
        {
            await panel.AddRowAsync(
                response, "kuro-game-sign-all", "全部签到", new KuroGameSignAllCallbackData(), cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// 游戏签到勾选面板：每个游戏一个开关按钮（√ 签到 / × 跳过），底部为「签到」「返回」。
    /// </summary>
    public async Task<CommandResponse> BuildGameSignPanelAsync(
        CommandContext context,
        KuroAccount account,
        IReadOnlyCollection<long> selected,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var available = AvailableGameIds(account);
        if (available.Count == 0)
        {
            return CommandResponses.Text(
                    $"账号 #{account.Id} {account.DisplayName} 暂无游戏角色，请先使用 /kuro game init {account.Id} 同步", context)
                .AsTelegramEditIfSpecified(editMessageId);
        }

        var selectedSet = new HashSet<long>(selected);
        var lines = new List<string>
        {
            "[库街区-手动游戏签到]",
            $"账号：#{account.Id} {account.DisplayName}",
            "勾选要签到的游戏（√=签到 ×=跳过），然后点击「签到」："
        };
        lines.AddRange(available.Select(gameId => FormatGameRolesLine(account, gameId)));
        var response = CommandResponses.Text(string.Join('\n', lines), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response, available, columns: 1, "kuro-game-sign-panel",
            gameId => $"{(selectedSet.Contains(gameId) ? "[√]" : "[×]")} " +
                KuroGameNames.Format(gameId, account.Roles.FirstOrDefault(role => role.GameId == gameId)?.GameName ?? string.Empty),
            gameId => new KuroGameSignPanelCallbackData(account.Id, Toggle: gameId),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync("kuro-game-sign-run", "签到",
                new KuroGameSignPanelCallbackData(account.Id), cancellationToken),
            await panel.ButtonAsync("kuro-game-sign-back", "返回",
                new KuroGameSignBackCallbackData(), cancellationToken)));
        return response;
    }

    /// <summary>全部账号签到的合并结果（纯文本）。</summary>
    public CommandResponse BuildCombinedResult(
        CommandContext context,
        string title,
        IReadOnlyList<(KuroAccount Account, IReadOnlyList<string> Lines)> results,
        string? editMessageId = null)
    {
        var blocks = new List<string> { title };
        foreach (var (account, lines) in results)
        {
            blocks.Add(string.Empty);
            blocks.Add($"#{account.Id} {account.DisplayName}");
            blocks.AddRange(lines.Count == 0 ? ["无结果"] : lines);
        }

        blocks.Add(string.Empty);
        blocks.Add("时间：" + timeProvider.GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        return CommandResponses.Text(string.Join('\n', blocks), context)
            .AsTelegramEditIfSpecified(editMessageId);
    }

    /// <summary>账号已同步角色去重后的游戏 Id，按 Id 排序。</summary>
    public static IReadOnlyList<long> AvailableGameIds(KuroAccount account)
    {
        return account.Roles.Select(role => role.GameId).Distinct().OrderBy(id => id).ToArray();
    }

    /// <summary>“显式清空（无勾选）”的存储标记，用于与“未设置（默认全选）”区分。</summary>
    public const string NoneSelectionSentinel = "-";

    /// <summary>面板初始勾选：账号上次持久化的选择（与当前可用游戏取交集）；未设置时默认全选，显式清空则为空。</summary>
    public static IReadOnlyList<long> ResolveGameSignSelection(KuroAccount account)
    {
        var available = AvailableGameIds(account);
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
            .Select(part => long.TryParse(part, out var id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();
        return available.Where(id => stored.Contains(id)).ToArray();
    }

    /// <summary>把勾选集合序列化为存储字符串（空集合存为清空标记）。</summary>
    public static string SerializeGameSignSelection(IReadOnlyCollection<long> selected)
    {
        return selected.Count == 0 ? NoneSelectionSentinel : string.Join(',', selected);
    }

    /// <summary>面板中单个游戏一行：游戏名 + 各角色名(RoleId) Lv.等级。</summary>
    private static string FormatGameRolesLine(KuroAccount account, long gameId)
    {
        var gameName = KuroGameNames.Format(gameId, account.Roles.FirstOrDefault(role => role.GameId == gameId)?.GameName ?? string.Empty);
        var details = account.Roles
            .Where(role => role.GameId == gameId)
            .Select(role => $"{role.RoleName}({role.RoleId}){(string.IsNullOrWhiteSpace(role.GameLevel) ? string.Empty : " Lv." + role.GameLevel)}")
            .ToArray();
        return details.Length == 0 ? $"- {gameName}" : $"- {gameName}：{string.Join("、", details)}";
    }

    // ---- Telegram MarkdownV2 富文本（原 TelegramGateway KuroTelegramRenderer 逻辑迁入） ----

    private static string RenderAccountListMarkdown(IReadOnlyList<KuroAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return "尚未绑定库街区账号";
        }

        var lines = new List<string> { MarkdownV2.Escape("[库街区]"), "已绑定账号：" };
        foreach (var account in accounts)
        {
            lines.Add($"\\- `#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}` \\({account.BbsUserId}\\)：自动签到{MarkdownV2.Escape(account.AutoSignEnabled ? "开启" : "关闭")}");
            foreach (var role in account.Roles)
            {
                lines.Add($"  \\- {MarkdownV2.Escape(role.GameName)} / `{MarkdownV2.Code(role.RoleName)}`：{MarkdownV2.Escape(role.AutoSignEnabled ? "自动签到开启" : "自动签到关闭")}");
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderBindResultMarkdown(KuroBindResult result)
    {
        var account = result.Account;
        var lines = new List<string>
        {
            MarkdownV2.Escape(result.UpdatedExisting ? "库街区账号已更新" : "库街区账号绑定成功"),
            $"账号：`#{account.Id}` `{MarkdownV2.Code(account.DisplayName)}`",
            $"UID：`{account.BbsUserId}`"
        };
        if (account.Roles.Count > 0)
        {
            lines.Add("角色：");
            lines.AddRange(account.Roles.Select(role => $"\\- {MarkdownV2.Escape(role.GameName)} / `{MarkdownV2.Code(role.RoleName)}` \\(Lv\\.{MarkdownV2.Escape(role.GameLevel)}\\)"));
        }

        return string.Join('\n', lines);
    }

    private string RenderSignResultMarkdown(string title, KuroAccount account, IReadOnlyList<string> resultLines)
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




    public async Task<CommandResponse> BuildDeletePanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var response = CommandResponses.Text("请选择要删除的库街区账号：", context);
        foreach (var account in accounts)
        {
            response.AddButtonRow(new ResponseButtonRow
            {
                Buttons =
                {
                    new ResponseButton
                    {
                        Text = $"{account.DisplayName} #{account.Id}",
                        Payload = await callbackStore.PutAsync(
                            "kuro-delete-select",
                            context.Identity.CoreUserId,
                            context.Request.ChatId,
                            context.Request.UserId,
                            new KuroAccountCallbackData(account.Id),
                            cancellationToken: cancellationToken)
                    }
                }
            });
        }

        return response;
    }

    public async Task<CommandResponse> BuildAutoSignPanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
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
            "kuro-autosign-account-menu",
            account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} {account.DisplayName} #{account.Id}",
            account => new KuroAutoSignMenuCallbackData(account.Id, "account", page),
            cancellationToken);
        var toggleAll = await panel.ButtonAsync(
            "kuro-auto-sign-toggle",
            ToggleAllText,
            new KuroAutoSignCallbackData(0, ToggleAll: true, Page: page),
            cancellationToken);
        await panel.AddPagerAsync(
            response,
            "kuro-autosign-root-menu",
            page,
            totalPages,
            target => new KuroAutoSignMenuCallbackData(0, "root", target),
            PreviousPageText,
            NextPageText,
            cancellationToken,
            middleButton: toggleAll);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("KuroAccountMissing", "未找到指定库街区账号", context);
        }

        var response = CommandResponses.Text(BuildAutoSignAccountDetailText(account), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddRowAsync(
            response,
            "kuro-auto-sign-toggle",
            account.AutoSignEnabled ? "[开] 总开关" : "[关] 总开关",
            new KuroAutoSignCallbackData(account.Id, Page: page),
            cancellationToken);
        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync(
                "kuro-autosign-bbs-menu", "社区任务",
                new KuroAutoSignMenuCallbackData(account.Id, "bbs", page), cancellationToken),
            await panel.ButtonAsync(
                "kuro-autosign-game-menu", "游戏角色",
                new KuroAutoSignMenuCallbackData(account.Id, "game"), cancellationToken)));
        await panel.AddRowAsync(
            response,
            "kuro-autosign-root-menu",
            "返回账号列表",
            new KuroAutoSignMenuCallbackData(0, "root", page),
            cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignBbsPanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("KuroAccountMissing", "未找到指定库街区账号", context);
        }

        var response = CommandResponses.Text(BuildAutoSignBbsText(account), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response,
            BbsTaskLabels,
            columns: 2,
            "kuro-bbs-task-toggle",
            item => $"{(((account.BbsTaskFlags & item.Flag) != 0) ? "[开]" : "[关]")} {item.Label}",
            item => new KuroBbsTaskCallbackData(account.Id, item.Flag),
            cancellationToken);
        await panel.AddRowAsync(
            response,
            "kuro-bbs-task-toggle-all",
            ToggleAllText,
            new KuroBbsTaskToggleAllCallbackData(account.Id),
            cancellationToken);
        await panel.AddRowAsync(
            response,
            "kuro-autosign-account-menu",
            "返回",
            new KuroAutoSignMenuCallbackData(account.Id, "account", page),
            cancellationToken);
        return response;
    }

    public async Task<CommandResponse> BuildAutoSignGamePanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        long accountId,
        string? editMessageId = null,
        CancellationToken cancellationToken = default,
        int page = 0)
    {
        var account = accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is null)
        {
            return CommandResponses.Error("KuroAccountMissing", "未找到指定库街区账号", context);
        }

        var orderedRoles = account.Roles.OrderBy(role => role.GameId).ThenBy(role => role.RoleId).ToArray();
        var totalPages = Pagination.TotalPages(orderedRoles.Length, RolesPerPage);
        page = Pagination.NormalizePage(page, orderedRoles.Length, RolesPerPage);
        var response = CommandResponses.Text(BuildAutoSignGameText(account, orderedRoles, page, totalPages), context)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context);

        await panel.AddGridAsync(
            response,
            Pagination.Slice(orderedRoles, page, RolesPerPage),
            columns: 1,
            "kuro-game-auto-sign-toggle",
            role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {role.GameName}/{role.RoleName}",
            role => new KuroGameAutoSignCallbackData(role.Id, account.Id, page),
            cancellationToken);
        await panel.AddPagerAsync(
            response,
            "kuro-autosign-game-menu",
            page,
            totalPages,
            target => new KuroAutoSignMenuCallbackData(account.Id, "game", target),
            PreviousPageText,
            NextPageText,
            cancellationToken);
        await panel.AddRowAsync(
            response,
            "kuro-game-auto-sign-toggle-all",
            ToggleAllText,
            new KuroGameAutoSignToggleAllCallbackData(account.Id, page),
            cancellationToken);
        await panel.AddRowAsync(
            response,
            "kuro-autosign-account-menu",
            "返回",
            new KuroAutoSignMenuCallbackData(account.Id, "account"),
            cancellationToken);
        return response;
    }





    public async Task<CommandResponse> BuildNotifyTypePanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var enabled = await subscriptionService.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            NotificationTypes.KuroAutoSign,
            accounts.Select(account => account.Id).ToArray(),
            cancellationToken);
        var enabledMarks = enabled.Count > 0
            ? new[] { $"`{MarkdownV2.Code(NotificationTypes.KuroAutoSignDisplayName)}`" }
            : [];
        var markdown = string.Join('\n',
            MarkdownV2.Escape("[消息订阅管理]"),
            MarkdownV2.Escape("当前已启用：") + (enabledMarks.Length == 0
                ? MarkdownV2.Escape("无")
                : string.Join(MarkdownV2.Escape("、"), enabledMarks)));
        var response = CommandResponses.TelegramMarkdown(
            context.Identity,
            markdown,
            replyToMessageId: editMessageId is null ? context.Request.MessageId : null,
            editMessageId: editMessageId);

        response.AddButtonRow(new ResponseButtonRow
        {
            Buttons =
            {
                new ResponseButton
                {
                    Text = NotificationTypes.KuroAutoSignDisplayName,
                    Payload = await callbackStore.PutAsync(
                        "notify-type-select",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new NotifyTypeCallbackData(NotificationTypes.KuroAutoSign),
                        cancellationToken: cancellationToken)
                }
            }
        });
        return response;
    }

    public async Task<CommandResponse> BuildNotifyAccountPanelAsync(
        CommandContext context,
        IReadOnlyList<KuroAccount> accounts,
        string? editMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var enabled = await subscriptionService.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            NotificationTypes.KuroAutoSign,
            accounts.Select(account => account.Id).ToArray(),
            cancellationToken);
        var enabledMarks = accounts
            .Where(account => enabled.Contains(account.Id))
            .Select(account => $"`{MarkdownV2.Code(account.DisplayName)}`")
            .ToArray();
        var markdown = string.Join('\n',
            MarkdownV2.Escape($"[消息订阅 · {NotificationTypes.KuroAutoSignDisplayName}]"),
            MarkdownV2.Escape("当前已启用：") + (enabledMarks.Length == 0
                ? MarkdownV2.Escape("无")
                : string.Join(MarkdownV2.Escape("、"), enabledMarks)),
            MarkdownV2.Escape("此处为消息订阅管理，仅控制签到结果是否推送；开关自动签到请使用 ") + MarkdownV2.CodeSpan("/kuro autosign"));
        var response = CommandResponses.TelegramMarkdown(
            context.Identity,
            markdown,
            replyToMessageId: editMessageId is null ? context.Request.MessageId : null,
            editMessageId: editMessageId);

        var panel = new PanelBuilder(callbackStore, context);
        await panel.AddGridAsync(
            response,
            accounts,
            columns: 2,
            "notify-account-toggle",
            account => $"{(enabled.Contains(account.Id) ? "[开]" : "[关]")} {account.DisplayName}",
            account => new NotifyAccountCallbackData(NotificationTypes.KuroAutoSign, account.Id, ToggleAll: false),
            cancellationToken);

        response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync(
                "notify-account-toggle", ToggleAllText,
                new NotifyAccountCallbackData(NotificationTypes.KuroAutoSign, 0, ToggleAll: true), cancellationToken),
            await panel.ButtonAsync(
                "notify-back", "返回", new NotifyBackCallbackData(), cancellationToken)));
        return response;
    }


    private static string BuildAutoSignText(IReadOnlyList<KuroAccount> accounts, int page, int totalPages)
    {
        if (accounts.Count == 0)
        {
            return "尚未绑定库街区账号";
        }

        var lines = new List<string>
        {
            "[库街区-自动签到]",
            "点击账号进入设置" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(accounts, page, AccountsPerPage)
            .Select(account => $"{(account.AutoSignEnabled ? "[开]" : "[关]")} #{account.Id} {account.DisplayName}"));
        lines.Add(NotifyHintLine);
        return string.Join('\n', lines);
    }

    private static string BuildAutoSignAccountDetailText(KuroAccount account)
    {
        return string.Join('\n',
            "[库街区-自动签到]",
            $"账号：#{account.Id} {account.DisplayName}",
            $"总开关：{(account.AutoSignEnabled ? "开启" : "关闭")}",
            $"社区任务：{FormatBbsTasks(account.BbsTaskFlags)}",
            "游戏角色：" + FormatGameRoles(account));
    }

    private static string BuildAutoSignBbsText(KuroAccount account)
    {
        return string.Join('\n',
            "[库街区-自动签到 - 社区任务]",
            $"账号：#{account.Id} {account.DisplayName}",
            "点击任务开关自动签到：",
            $"当前已启用：{FormatBbsTasks(account.BbsTaskFlags)}");
    }

    private static string BuildAutoSignGameText(
        KuroAccount account,
        IReadOnlyList<KuroGameRole> roles,
        int page,
        int totalPages)
    {
        var lines = new List<string>
        {
            "[库街区-自动签到 - 游戏角色]",
            $"账号：#{account.Id} {account.DisplayName}",
            "点击角色开关自动签到" + PageSuffix(page, totalPages) + "："
        };
        lines.AddRange(Pagination.Slice(roles, page, RolesPerPage)
            .Select(role => $"{(role.AutoSignEnabled ? "[开]" : "[关]")} {role.GameName}/{role.RoleName}"));
        return string.Join('\n', lines);
    }

    private static string FormatGameRoles(KuroAccount account, bool onlyEnabled = false)
    {
        return TextLayout.JoinOrEmpty(
            account.Roles
                .Where(role => !onlyEnabled || role.AutoSignEnabled)
                .Select(role => $"{role.GameName}/{role.RoleName}"),
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


}
