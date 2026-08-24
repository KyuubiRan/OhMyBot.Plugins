using Microsoft.Extensions.Options;
using OhMyBot.Contracts;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Plugins.QqApproval.Data.Entities;

namespace OhMyBot.Plugins.QqApproval.Integrations;

/// <summary>
/// <c>/qqreq</c> 命令树：查看待审列表并审批、维护自动黑/白名单。
/// 所有节点的所需权限来自配置（默认 owner），与回调侧是同一条线。
/// </summary>
public sealed class QqApprovalCommandDslProvider(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options) : IPlatformCommandDslProvider
{
    private const int PendingPageSize = 10;
    private readonly QqApprovalOptions _options = options.Value;

    public IEnumerable<CommandDslNode> GetNodes()
    {
        var privilege = _options.ResolveApprovalRequiredPrivilege();
        return
        [
            new CommandDslNode
            {
                Name = "qqreq",
                Description = "QQ 请求审批（加好友 / 邀请进群 / 入群申请）",
                Usage = "/qqreq <list|recent|rules|allow|deny|ruledel>",
                RequiredPrivilege = privilege,
                SupportPlatforms = SupportedPlatforms.QQ,
                SupportChatTypes = SupportedChatTypes.Private,
                Handler = StatusAsync,
                Children =
                [
                    Node(privilege, "list", "查看待审批请求并审批", "/qqreq list", ListAsync),
                    Node(privilege, "recent", "查看最近已处理的请求", "/qqreq recent", RecentAsync),
                    Node(privilege, "rules", "查看/开关自动黑白名单", "/qqreq rules [on|off <类型>]", RulesAsync),
                    Node(privilege, "allow", "添加自动同意规则（白名单）", "/qqreq allow <类型> <user|group> <号码> [备注]",
                        context => UpsertRuleAsync(context, QqApprovalRuleAction.Approve)),
                    Node(privilege, "deny", "添加自动拒绝规则（黑名单）", "/qqreq deny <类型> <user|group> <号码> [备注]",
                        context => UpsertRuleAsync(context, QqApprovalRuleAction.Reject)),
                    Node(privilege, "ruledel", "删除一条规则", "/qqreq ruledel <规则id>", DeleteRuleAsync)
                ]
            }
        ];
    }

    private static CommandDslNode Node(
        UserPrivilege privilege,
        string name,
        string description,
        string usage,
        CommandDslHandler handler)
    {
        return new CommandDslNode
        {
            Name = name,
            Description = description,
            Usage = usage,
            RequiredPrivilege = privilege,
            SupportPlatforms = SupportedPlatforms.QQ,
            SupportChatTypes = SupportedChatTypes.Private,
            Handler = handler
        };
    }

    // ---- Handlers ----

    private async Task<CommandResponse> StatusAsync(CommandContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<QqApprovalSettingsService>();
        var service = scope.ServiceProvider.GetRequiredService<QqApprovalService>();
        var all = await settings.GetAllAsync(context.CancellationToken);
        var pending = await service.ListPendingAsync(PendingPageSize + 1, context.CancellationToken);

        var lines = new List<string> { "QQ 请求审批状态：" };
        lines.AddRange(all.Select(setting =>
            $"· {QqApprovalService.FormatKind(setting.Kind)}（{QqApprovalSettingsService.FormatKindKey(setting.Kind)}）"
            + $" 接入：{(_options.GetRequestType(setting.Kind).Enabled ? "开" : "关")}"
            + $" 订阅权限：{UserPrivilegeNames.Format(_options.GetRequestType(setting.Kind).ResolveRequiredPrivilege())}"
            + $" 自动名单：{(setting.RulesEnabled ? "开" : "关")}"));
        lines.Add(string.Empty);
        lines.Add($"待审批：{(pending.Count > PendingPageSize ? $"{PendingPageSize}+" : pending.Count.ToString())} 条");
        lines.Add($"审批权限：{UserPrivilegeNames.Format(_options.ResolveApprovalRequiredPrivilege())}");
        lines.Add(string.Empty);
        lines.Add("用 /notify 管理自己的通知，用 /qqreq list 审批，/qqreq rules 管理自动名单。");
        return CommandResponses.Text(string.Join('\n', lines), context);
    }

    private async Task<CommandResponse> ListAsync(CommandContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<QqApprovalService>();
        var pending = await service.ListPendingAsync(PendingPageSize, context.CancellationToken);
        if (pending.Count == 0)
        {
            return CommandResponses.Text("当前没有待审批的请求。", context);
        }

        // 列表项本身是按钮：QQ 侧会被渲染成编号菜单，选中后再出「同意 / 拒绝」二级菜单。
        // 这里重新生成回调 payload，所以推送菜单过期后也能靠 /qqreq list 继续审批。
        var text = string.Join('\n', new[] { $"待审批请求（{pending.Count} 条）：" }
            .Concat(pending.Select(QqApprovalService.Summarize)));
        var response = CommandResponses.TelegramPlain(context.Identity, text);
        var panel = new PanelBuilder(callbackStore, context, QqApprovalPlugin.PluginId);
        foreach (var request in pending)
        {
            response = await panel.AddRowAsync(
                response,
                QqApprovalCallbackHandler.OpenActionType,
                QqApprovalService.Summarize(request),
                new QqApprovalOpenData(request.Id),
                context.CancellationToken);
        }

        return response;
    }

    private async Task<CommandResponse> RecentAsync(CommandContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<QqApprovalService>();
        var recent = await service.ListRecentAsync(PendingPageSize, context.CancellationToken);
        if (recent.Count == 0)
        {
            return CommandResponses.Text("还没有已处理的请求。", context);
        }

        var lines = recent.Select(request =>
            $"{QqApprovalService.Summarize(request)} → {QqApprovalService.FormatStatus(request.Status)}");
        return CommandResponses.Text(
            string.Join('\n', new[] { "最近已处理：" }.Concat(lines)),
            context);
    }

    private async Task<CommandResponse> RulesAsync(CommandContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<QqApprovalSettingsService>();
        var args = context.Request.Args;

        if (args.Count >= 2)
        {
            var enable = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            if (!enable && !args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResponses.Error("Usage", "用法：/qqreq rules [on|off <friend|invite|groupadd>]", context);
            }

            if (!QqApprovalSettingsService.TryParseKind(args[1], out var kind))
            {
                return CommandResponses.Error("Usage", "类型可选：friend、invite、groupadd", context);
            }

            await settings.SetRulesEnabledAsync(kind, enable, context.CancellationToken);
            return CommandResponses.Text(
                $"已{(enable ? "启用" : "停用")}「{QqApprovalService.FormatKind(kind)}」的自动黑白名单。"
                + (enable ? string.Empty : "该类型的请求将一律转人工审批。"),
                context);
        }

        var all = await settings.GetAllAsync(context.CancellationToken);
        var rules = await settings.ListRulesAsync(context.CancellationToken);
        var lines = new List<string> { "自动名单开关：" };
        lines.AddRange(all.Select(setting =>
            $"· {QqApprovalService.FormatKind(setting.Kind)}：{(setting.RulesEnabled ? "启用" : "停用")}"));
        lines.Add(string.Empty);
        lines.Add(rules.Count == 0 ? "暂无规则。" : "规则（黑名单优先）：");
        lines.AddRange(rules.Select(rule =>
            $"#{rule.Id} [{QqApprovalService.FormatKind(rule.Kind)}]"
            + $" {(rule.Action == QqApprovalRuleAction.Approve ? "自动同意" : "自动拒绝")}"
            + $" {(rule.Scope == QqApprovalRuleScope.Requester ? "用户" : "群")} {rule.Value}"
            + (string.IsNullOrWhiteSpace(rule.Note) ? string.Empty : $"（{rule.Note}）")));
        return CommandResponses.Text(string.Join('\n', lines), context);
    }

    private async Task<CommandResponse> UpsertRuleAsync(CommandContext context, QqApprovalRuleAction action)
    {
        var verb = action == QqApprovalRuleAction.Approve ? "allow" : "deny";
        var args = context.Request.Args;
        if (args.Count < 3
            || !QqApprovalSettingsService.TryParseKind(args[0], out var kind)
            || !QqApprovalSettingsService.TryParseScope(args[1], out var scopeKind))
        {
            return CommandResponses.Error(
                "Usage",
                $"用法：/qqreq {verb} <friend|invite|groupadd> <user|group> <号码> [备注]",
                context);
        }

        var value = args[2].Trim();
        if (!value.All(char.IsDigit))
        {
            return CommandResponses.Error("QqApprovalBadValue", "号码只能是数字（QQ 号或群号）。", context);
        }

        if (scopeKind == QqApprovalRuleScope.Group && kind == PlatformRequestKind.FriendAdd)
        {
            return CommandResponses.Error("QqApprovalBadScope", "加好友请求没有群号，不能按群设规则。", context);
        }

        var note = args.Count > 3 ? string.Join(' ', args.Skip(3)) : string.Empty;
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<QqApprovalSettingsService>();
        var setting = await settings.GetAsync(kind, context.CancellationToken);
        var rule = await settings.UpsertRuleAsync(kind, scopeKind, value, action, note, context.CancellationToken);

        var hint = setting.RulesEnabled
            ? string.Empty
            : $"\n注意：「{QqApprovalService.FormatKind(kind)}」的自动名单当前是停用状态，规则不会生效。"
              + $"用 /qqreq rules on {QqApprovalSettingsService.FormatKindKey(kind)} 启用。";
        return CommandResponses.Text(
            $"已保存规则 #{rule.Id}：{QqApprovalService.FormatKind(kind)} "
            + $"{(scopeKind == QqApprovalRuleScope.Requester ? "用户" : "群")} {value} → "
            + $"{(action == QqApprovalRuleAction.Approve ? "自动同意" : "自动拒绝")}{hint}",
            context);
    }

    private async Task<CommandResponse> DeleteRuleAsync(CommandContext context)
    {
        if (context.Request.Args.Count < 1 || !long.TryParse(context.Request.Args[0], out var ruleId))
        {
            return CommandResponses.Error("Usage", "用法：/qqreq ruledel <规则id>", context);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<QqApprovalSettingsService>();
        var deleted = await settings.DeleteRuleAsync(ruleId, context.CancellationToken);
        return CommandResponses.Text(deleted ? $"已删除规则 #{ruleId}。" : $"未找到规则 #{ruleId}。", context);
    }
}
