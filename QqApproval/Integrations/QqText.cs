namespace OhMyBot.Plugins.QqApproval.Integrations;

/// <summary>
/// QQ 文本的 CQ 码转义。主动推送的通知以字符串形式发给 NapCat，其中的 CQ 码会被解析，
/// 所以想发头像图就得用 CQ 段——代价是同一条消息里所有用户可控内容（昵称、附言）必须先转义，
/// 否则对方在昵称里写个 <c>[CQ:...]</c> 就能让机器人替他发任意消息段。
/// </summary>
internal static class QqText
{
    public static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("[", "&#91;", StringComparison.Ordinal)
            .Replace("]", "&#93;", StringComparison.Ordinal);
    }

    /// <summary>CQ 参数值还要额外转义逗号，否则会被当成参数分隔符。</summary>
    public static string EscapeParameter(string value)
        => Escape(value).Replace(",", "&#44;", StringComparison.Ordinal);
}
