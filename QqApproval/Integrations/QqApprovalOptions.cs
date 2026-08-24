using OhMyBot.Contracts;
using OhMyBot.Contracts.Grpc;

namespace OhMyBot.Plugins.QqApproval.Integrations;

public sealed class QqApprovalOptions
{
    /// <summary>查看待审记录、维护规则和执行审批所需的最低权限。</summary>
    public string ApprovalRequiredPrivilege { get; set; } = "owner";

    /// <summary>三类 QQ 请求的全局接入开关和个人订阅权限。</summary>
    public QqApprovalRequestTypesOptions RequestTypes { get; set; } = new();

    /// <summary>推送菜单与其回调 payload 的存活时长；超时后需用 /qqreq list 重新出菜单。</summary>
    public TimeSpan PendingTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>拒绝群请求时回给申请人的理由（QQ 只在群请求上展示理由）。</summary>
    public string RejectReason { get; set; } = "管理员拒绝了这个请求。";

    public UserPrivilege ResolveApprovalRequiredPrivilege()
    {
        return UserPrivilegeNames.TryParse(ApprovalRequiredPrivilege, out var privilege)
            ? privilege
            : UserPrivilege.Owner;
    }

    public QqApprovalRequestTypeOptions GetRequestType(PlatformRequestKind kind) => kind switch
    {
        PlatformRequestKind.FriendAdd => RequestTypes.FriendAdd,
        PlatformRequestKind.GroupInvite => RequestTypes.GroupInvite,
        PlatformRequestKind.GroupAdd => RequestTypes.GroupAdd,
        _ => new QqApprovalRequestTypeOptions { Enabled = false }
    };
}

public sealed class QqApprovalRequestTypesOptions
{
    public QqApprovalRequestTypeOptions FriendAdd { get; set; } = new();

    public QqApprovalRequestTypeOptions GroupInvite { get; set; } = new();

    public QqApprovalRequestTypeOptions GroupAdd { get; set; } = new() { Enabled = false };
}

public sealed class QqApprovalRequestTypeOptions
{
    /// <summary>是否接入、落库并处理该类型。个人是否收消息由 /notify 订阅决定。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>在 /notify 中查看并订阅该类型所需的最低权限。</summary>
    public string RequiredPrivilege { get; set; } = "owner";

    public UserPrivilege ResolveRequiredPrivilege()
    {
        return UserPrivilegeNames.TryParse(RequiredPrivilege, out var privilege)
            ? privilege
            : UserPrivilege.Owner;
    }
}
