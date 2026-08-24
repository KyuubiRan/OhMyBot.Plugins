using OhMyBot.Contracts.Grpc;

namespace OhMyBot.Plugins.QqApproval.Data.Entities;

/// <summary>
/// 每种请求类型的自动规则开关，由 <c>/qqreq rules on|off</c> 维护。
/// 表里没有对应行时使用代码内置默认值（见 <see cref="Defaults"/>），
/// 所以插件首次启动无需写库也能按预期工作。
/// </summary>
public sealed class QqApprovalListenerSetting
{
    public PlatformRequestKind Kind { get; set; }

    /// <summary>是否启用自动黑/白名单规则；关闭后所有请求一律转人工审批。</summary>
    public bool RulesEnabled { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public const bool DefaultRulesEnabled = false;
}
