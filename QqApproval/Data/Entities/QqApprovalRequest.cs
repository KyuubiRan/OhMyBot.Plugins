using OhMyBot.Contracts.Grpc;

namespace OhMyBot.Plugins.QqApproval.Data.Entities;

public enum QqApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    AutoApproved = 3,
    AutoRejected = 4
}

/// <summary>
/// 一条从 QQ 网关上报的待审批请求。<see cref="Flag"/> 是 OneBot 的审批句柄，
/// 审批时原样回传给网关；Core 侧重启后仍能凭它继续审批，所以必须落库而非只放内存。
/// </summary>
public sealed class QqApprovalRequest
{
    public long Id { get; set; }

    public PlatformRequestKind Kind { get; set; }

    public string Flag { get; set; } = string.Empty;

    public string BotInstanceId { get; set; } = string.Empty;

    public string RequesterId { get; set; } = string.Empty;

    public string RequesterName { get; set; } = string.Empty;

    /// <summary>群相关请求的群号；加好友请求为空串。</summary>
    public string GroupId { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// 申请人档案（昵称/性别/年龄/等级/头像）的 JSON 快照，键见 <c>PlatformRequestProfileKeys</c>。
    /// 存 JSON 而不是逐字段建列：这些值只用于展示、从不参与查询，加字段也就不必再迁移一次表。
    /// </summary>
    public string RequesterProfileJson { get; set; } = string.Empty;

    public QqApprovalStatus Status { get; set; }

    /// <summary>人工审批人的 Core 用户 id；自动规则处理的为 null。</summary>
    public long? DecidedByCoreUserId { get; set; }

    public string DecidedReason { get; set; } = string.Empty;

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
