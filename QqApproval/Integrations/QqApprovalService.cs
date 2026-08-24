using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Platform;
using OhMyBot.Core.Commanding.Qq;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Plugins.QqApproval.Data.Entities;

namespace OhMyBot.Plugins.QqApproval.Integrations;

/// <summary>
/// 待审批请求的落库、自动规则判定、订阅通知与最终裁决。
/// 平台协议细节（OneBot 动作名、flag 含义）全部不在这里：本类只表达「同意 / 拒绝哪一条」。
/// </summary>
public sealed class QqApprovalService(
    QqApprovalDbContext dbContext,
    QqApprovalSettingsService settings,
    CallbackActionStore callbackStore,
    QqMenuStore menuStore,
    INotificationSubscriptionService subscriptionService,
    INotificationPublisher notificationPublisher,
    IPlatformRequestDecisionPublisher decisionPublisher,
    IOptions<QqApprovalOptions> options,
    TimeProvider timeProvider,
    ILogger<QqApprovalService> logger)
{
    private readonly QqApprovalOptions _options = options.Value;

    public async Task HandleAsync(PlatformRequestNotice notice, CancellationToken cancellationToken = default)
    {
        var typeOptions = _options.GetRequestType(notice.Kind);
        if (!typeOptions.Enabled)
        {
            logger.LogDebug("QQ 请求类型 {Kind} 未开启接入，已忽略。requester={Requester}", notice.Kind, notice.RequesterId);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var existing = await dbContext.QqApprovalRequests
            .FirstOrDefaultAsync(request => request.Flag == notice.Flag, cancellationToken);
        if (existing is not null)
        {
            // 网关重连后 NapCat 会重推同一条请求；已经在库里就不再重复通知。
            logger.LogDebug("QQ 待审批请求已存在，跳过重复上报。flag={Flag}", notice.Flag);
            return;
        }

        var record = new QqApprovalRequest
        {
            Kind = notice.Kind,
            Flag = notice.Flag,
            BotInstanceId = notice.BotInstanceId,
            RequesterId = notice.RequesterId,
            RequesterName = notice.RequesterName,
            GroupId = notice.GroupId,
            Comment = notice.Comment,
            RequesterProfileJson = notice.RequesterProfile is { Count: > 0 } profile
                ? JsonSerializer.Serialize(profile)
                : string.Empty,
            Status = QqApprovalStatus.Pending,
            OccurredAt = notice.OccurredAt,
            CreatedAt = now
        };
        dbContext.QqApprovalRequests.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);

        var setting = await settings.GetAsync(notice.Kind, cancellationToken);
        if (setting.RulesEnabled)
        {
            var rule = await MatchRuleAsync(record, cancellationToken);
            if (rule is not null)
            {
                var approve = rule.Action == QqApprovalRuleAction.Approve;
                await ApplyDecisionAsync(
                    record,
                    approve,
                    approve ? QqApprovalStatus.AutoApproved : QqApprovalStatus.AutoRejected,
                    decidedByCoreUserId: null,
                    reason: $"自动规则 #{rule.Id}",
                    cancellationToken);
                logger.LogInformation(
                    "QQ 请求命中自动规则。kind={Kind} requester={Requester} action={Action} rule={RuleId}",
                    record.Kind,
                    record.RequesterId,
                    rule.Action,
                    rule.Id);
                return;
            }
        }

        await NotifySubscribersAsync(record, cancellationToken);
    }

    private async Task<QqApprovalRule?> MatchRuleAsync(QqApprovalRequest request, CancellationToken cancellationToken)
    {
        var rules = await dbContext.QqApprovalRules
            .Where(rule => rule.Kind == request.Kind)
            .ToListAsync(cancellationToken);

        bool Matches(QqApprovalRule rule) => rule.Scope switch
        {
            QqApprovalRuleScope.Requester => rule.Value == request.RequesterId,
            QqApprovalRuleScope.Group => !string.IsNullOrEmpty(request.GroupId) && rule.Value == request.GroupId,
            _ => false
        };

        // 黑名单优先：同时命中黑白名单时按拒绝处理。
        return rules.FirstOrDefault(rule => rule.Action == QqApprovalRuleAction.Reject && Matches(rule))
            ?? rules.FirstOrDefault(rule => rule.Action == QqApprovalRuleAction.Approve && Matches(rule));
    }

    /// <summary>给每个已订阅且仍满足权限的用户推一条带审批编号菜单的 QQ 私聊消息。</summary>
    public async Task NotifySubscribersAsync(QqApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var typeOptions = _options.GetRequestType(request.Kind);
        var required = typeOptions.ResolveRequiredPrivilege();
        var deliveries = await subscriptionService.ListEnabledDeliveriesByTargetAsync(
            QqApprovalNotificationTypes.For(request.Kind),
            (long)request.Kind,
            cancellationToken);
        var text = Describe(request);

        foreach (var delivery in deliveries
                     .Where(item => item.Platform == BotPlatform.Qq
                         && item.Privilege >= required
                         && string.Equals(item.BotInstanceId, request.BotInstanceId, StringComparison.Ordinal))
                     .DistinctBy(item => (item.CoreUserId, item.BotInstanceId, item.ChatId)))
        {
            try
            {
                var menuToken = await BuildDecisionMenuAsync(
                    request.Id,
                    delivery.ChatId,
                    delivery.CoreUserId,
                    cancellationToken);
                await notificationPublisher.PublishAsync(
                    BotPlatform.Qq,
                    delivery.BotInstanceId,
                    delivery.ChatId,
                    [QqMenuConverter.RenderMenu(text, ["同意", "拒绝"], BotChatType.Private)],
                    [menuToken],
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // 单个订阅者推送失败不影响其它人，也不影响请求已落库的事实。
                logger.LogError(
                    exception,
                    "向订阅者 {CoreUserId} 推送 QQ 待审批请求失败。id={RequestId}",
                    delivery.CoreUserId,
                    request.Id);
            }
        }
    }

    private async Task<string> BuildDecisionMenuAsync(
        long requestId,
        string subscriberQq,
        long coreUserId,
        CancellationToken cancellationToken)
    {
        // 私聊里 chatId 就是对方 QQ 号；回调侧会校验会话与发起人。
        var approve = await callbackStore.PutAsync(
            QqApprovalCallbackHandler.DecideActionType,
            coreUserId,
            subscriberQq,
            subscriberQq,
            new QqApprovalDecideData(requestId, true),
            ttl: _options.PendingTtl,
            ownerPluginId: QqApprovalPlugin.PluginId,
            cancellationToken: cancellationToken);
        var reject = await callbackStore.PutAsync(
            QqApprovalCallbackHandler.DecideActionType,
            coreUserId,
            subscriberQq,
            subscriberQq,
            new QqApprovalDecideData(requestId, false),
            ttl: _options.PendingTtl,
            ownerPluginId: QqApprovalPlugin.PluginId,
            cancellationToken: cancellationToken);
        return await menuStore.PutTokenAsync([approve, reject], _options.PendingTtl, cancellationToken);
    }

    /// <summary>人工裁决。返回给用户看的结果文案。</summary>
    public async Task<string> DecideAsync(
        long requestId,
        bool approve,
        long decidedByCoreUserId,
        CancellationToken cancellationToken = default)
    {
        // 用 AsNoTracking 读状态：裁决走的是 ExecuteUpdate，不会刷新已追踪实体，
        // 沿用追踪副本会让同一 scope 内的第二次点击误报成「被别人抢先处理」。
        var request = await dbContext.QqApprovalRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null)
        {
            return "未找到这条请求。";
        }

        if (request.Status != QqApprovalStatus.Pending)
        {
            return $"这条请求已经处理过了（{FormatStatus(request.Status)}）。";
        }

        // 多个审批人可能同时点同一条：条件更新保证只有一个人真正落地决定。
        var affected = await dbContext.QqApprovalRequests
            .Where(item => item.Id == requestId && item.Status == QqApprovalStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, approve ? QqApprovalStatus.Approved : QqApprovalStatus.Rejected)
                    .SetProperty(item => item.DecidedByCoreUserId, decidedByCoreUserId)
                    .SetProperty(item => item.DecidedAt, timeProvider.GetUtcNow())
                    .SetProperty(item => item.DecidedReason, approve ? string.Empty : _options.RejectReason),
                cancellationToken);
        if (affected == 0)
        {
            return "这条请求刚刚已被其他管理员处理。";
        }

        await PublishDecisionAsync(request, approve, cancellationToken);
        return $"已{(approve ? "同意" : "拒绝")}：{Summarize(request)}";
    }

    private async Task ApplyDecisionAsync(
        QqApprovalRequest request,
        bool approve,
        QqApprovalStatus status,
        long? decidedByCoreUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        request.Status = status;
        request.DecidedByCoreUserId = decidedByCoreUserId;
        request.DecidedAt = timeProvider.GetUtcNow();
        request.DecidedReason = approve ? string.Empty : _options.RejectReason;
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishDecisionAsync(request, approve, cancellationToken);
        logger.LogInformation("QQ 请求已裁决。id={RequestId} approve={Approve} reason={Reason}", request.Id, approve, reason);
    }

    private Task PublishDecisionAsync(QqApprovalRequest request, bool approve, CancellationToken cancellationToken)
    {
        return decisionPublisher.PublishAsync(
            BotPlatform.Qq,
            request.BotInstanceId,
            request.Kind,
            request.Flag,
            approve,
            approve ? string.Empty : _options.RejectReason,
            cancellationToken);
    }

    public Task<List<QqApprovalRequest>> ListPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        // 按自增主键倒序：Id 就是插入顺序，等价于按创建时间倒序，
        // 且同一时刻落库的多条也有确定次序（CreatedAt 相同时排序不稳定）。
        return dbContext.QqApprovalRequests
            .Where(request => request.Status == QqApprovalStatus.Pending)
            .OrderByDescending(request => request.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<List<QqApprovalRequest>> ListRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        return dbContext.QqApprovalRequests
            .Where(request => request.Status != QqApprovalStatus.Pending)
            .OrderByDescending(request => request.DecidedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<QqApprovalRequest?> FindAsync(long requestId, CancellationToken cancellationToken = default)
    {
        return dbContext.QqApprovalRequests.FirstOrDefaultAsync(request => request.Id == requestId, cancellationToken);
    }

    // ---- 文案 ----

    public static string FormatKind(PlatformRequestKind kind) => kind switch
    {
        PlatformRequestKind.FriendAdd => "加好友",
        PlatformRequestKind.GroupInvite => "邀请进群",
        PlatformRequestKind.GroupAdd => "入群申请",
        _ => kind.ToString()
    };

    public static string FormatStatus(QqApprovalStatus status) => status switch
    {
        QqApprovalStatus.Pending => "待审批",
        QqApprovalStatus.Approved => "已同意",
        QqApprovalStatus.Rejected => "已拒绝",
        QqApprovalStatus.AutoApproved => "自动同意",
        QqApprovalStatus.AutoRejected => "自动拒绝",
        _ => status.ToString()
    };

    /// <summary>单行摘要，用于列表。</summary>
    public static string Summarize(QqApprovalRequest request)
    {
        var group = string.IsNullOrEmpty(request.GroupId)
            ? string.Empty
            : $" {FormatGroup(request, ReadProfile(request))}";
        return $"#{request.Id} [{FormatKind(request.Kind)}]{group} {FormatRequester(request)}";
    }

    /// <summary>群名查得到就「群名(群号)」，查不到退回「群 群号」。</summary>
    private static string FormatGroup(QqApprovalRequest request, IReadOnlyDictionary<string, string> profile)
    {
        return profile.TryGetValue(PlatformRequestProfileKeys.GroupName, out var name)
            && !string.IsNullOrWhiteSpace(name)
            ? $"{name}({request.GroupId})"
            : $"群 {request.GroupId}";
    }

    /// <summary>推送给订阅者的详情文案。</summary>
    public static string Describe(QqApprovalRequest request)
    {
        var profile = ReadProfile(request);
        var lines = new List<string>
        {
            $"收到待审批请求 #{request.Id}",
            $"类型：{FormatKind(request.Kind)}",
            $"申请人：{QqText.Escape(FormatRequester(request))}"
        };

        // 档案是网关尽力查来的，查不到就整行不出现，不写「未知」占位。
        var detail = new List<string>();
        if (profile.TryGetValue(PlatformRequestProfileKeys.Gender, out var gender))
        {
            detail.Add($"性别 {FormatGender(gender)}");
        }

        if (profile.TryGetValue(PlatformRequestProfileKeys.Age, out var age))
        {
            detail.Add($"{age} 岁");
        }

        if (profile.TryGetValue(PlatformRequestProfileKeys.Level, out var level))
        {
            detail.Add($"QQ 等级 {level}");
        }

        if (detail.Count > 0)
        {
            lines.Add($"资料：{QqText.Escape(string.Join(" · ", detail))}");
        }

        if (!string.IsNullOrEmpty(request.GroupId))
        {
            lines.Add($"群：{QqText.Escape(FormatGroup(request, profile))}");
        }

        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            lines.Add($"附言：{QqText.Escape(request.Comment)}");
        }

        // 头像用 CQ 图片段发出：通知走的是字符串消息，NapCat 会解析 CQ 码。
        // 正因如此，上面所有用户可控字段都必须先转义，否则昵称里的方括号会被当成 CQ 码。
        if (profile.TryGetValue(PlatformRequestProfileKeys.AvatarUrl, out var avatar)
            && Uri.TryCreate(avatar, UriKind.Absolute, out var avatarUri)
            && avatarUri.Scheme is "http" or "https")
        {
            lines.Add($"[CQ:image,file={QqText.EscapeParameter(avatar)}]");
        }

        return string.Join('\n', lines);
    }

    public static string FormatRequester(QqApprovalRequest request)
    {
        return string.IsNullOrWhiteSpace(request.RequesterName)
            ? request.RequesterId
            : $"{request.RequesterName}({request.RequesterId})";
    }

    private static string FormatGender(string gender) => gender.ToLowerInvariant() switch
    {
        "male" or "男" => "男",
        "female" or "女" => "女",
        _ => gender
    };

    private static Dictionary<string, string> ReadProfile(QqApprovalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequesterProfileJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(request.RequesterProfileJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record QqApprovalDecideData(long RequestId, bool Approve);
