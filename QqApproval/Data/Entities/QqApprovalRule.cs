using OhMyBot.Contracts.Grpc;

namespace OhMyBot.Plugins.QqApproval.Data.Entities;

public enum QqApprovalRuleAction
{
    /// <summary>白名单：命中即自动同意。</summary>
    Approve = 1,

    /// <summary>黑名单：命中即自动拒绝。</summary>
    Reject = 2
}

public enum QqApprovalRuleScope
{
    /// <summary>按申请人 QQ 号匹配。</summary>
    Requester = 1,

    /// <summary>按群号匹配（仅群相关请求有意义）。</summary>
    Group = 2
}

/// <summary>
/// 一条自动处理规则。黑名单优先于白名单；两者都不命中的请求转人工审批。
/// 规则整体受对应类型的 <see cref="QqApprovalListenerSetting.RulesEnabled"/> 控制。
/// </summary>
public sealed class QqApprovalRule
{
    public long Id { get; set; }

    public PlatformRequestKind Kind { get; set; }

    public QqApprovalRuleScope Scope { get; set; }

    public string Value { get; set; } = string.Empty;

    public QqApprovalRuleAction Action { get; set; }

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
