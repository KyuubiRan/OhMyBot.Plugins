using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Plugin.Commanding;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval;

public static class QqApprovalNotificationTypes
{
    public static readonly NotificationCategory BotMessagesCategory = new(
        "bot-messages",
        "Bot消息通知",
        int.MaxValue);

    public const string FriendAdd = "qq-approval-friend-add";
    public const string GroupInvite = "qq-approval-group-invite";
    public const string GroupAdd = "qq-approval-group-add";

    public static string For(PlatformRequestKind kind) => kind switch
    {
        PlatformRequestKind.FriendAdd => FriendAdd,
        PlatformRequestKind.GroupInvite => GroupInvite,
        PlatformRequestKind.GroupAdd => GroupAdd,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported QQ request kind.")
    };
}

public abstract class QqApprovalNotificationSource(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options) : IPluginNotificationSource
{
    protected abstract PlatformRequestKind Kind { get; }

    private QqApprovalRequestTypeOptions TypeOptions => options.Value.GetRequestType(Kind);

    private long TargetId => (long)Kind;

    public string Type => QqApprovalNotificationTypes.For(Kind);

    public string DisplayName => QqApprovalService.FormatKind(Kind) + "通知";

    public int Order => Kind switch
    {
        PlatformRequestKind.FriendAdd => 100,
        PlatformRequestKind.GroupInvite => 200,
        PlatformRequestKind.GroupAdd => 300,
        _ => int.MaxValue
    };

    public NotificationCategory Category => QqApprovalNotificationTypes.BotMessagesCategory;

    public UserPrivilege RequiredPrivilege => TypeOptions.ResolveRequiredPrivilege();

    public SupportedPlatforms SupportPlatforms => SupportedPlatforms.QQ;

    public bool Enabled => TypeOptions.Enabled;

    public async Task<bool> HasEnabledTargetsAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<INotificationSubscriptionService>();
        var enabled = await subscriptions.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            Type,
            [TargetId],
            cancellationToken);
        return enabled.Contains(TargetId);
    }

    public async Task<CommandResponse> BuildAccountPanelAsync(
        CommandContext context,
        string? editMessageId,
        CancellationToken cancellationToken = default)
    {
        var enabled = await HasEnabledTargetsAsync(context, cancellationToken);
        return await BuildPanelAsync(context, editMessageId, enabled, cancellationToken);
    }

    public async Task<CommandResponse> ToggleAsync(
        CommandContext context,
        long accountId,
        bool toggleAll,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        if (toggleAll || accountId != TargetId)
        {
            return PluginCallbackResponses.Error(context.Identity, editMessageId, "无效的 QQ 请求通知类型。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<INotificationSubscriptionService>();
        await subscriptions.ToggleAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            context.Request.BotInstanceId,
            context.Request.ChatId,
            Type,
            TargetId,
            cancellationToken);
        var enabled = await subscriptions.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            Type,
            [TargetId],
            cancellationToken);
        return await BuildPanelAsync(context, editMessageId, enabled.Contains(TargetId), cancellationToken);
    }

    private async Task<CommandResponse> BuildPanelAsync(
        CommandContext context,
        string? editMessageId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var response = CommandResponses.TelegramPlain(
                context.Identity,
                $"[消息订阅 · {DisplayName}]\n当前状态：{(enabled ? "已开启" : "已关闭")}",
                context.Request.MessageId)
            .AsTelegramEditIfSpecified(editMessageId);
        var panel = new PanelBuilder(callbackStore, context, QqApprovalPlugin.PluginId);
        return response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync(
                "notify-account-toggle",
                enabled ? "关闭" : "开启",
                new NotificationAccountCallbackData(Type, TargetId, ToggleAll: false),
                cancellationToken),
            await panel.ButtonAsync(
                "notify-category-select",
                "返回",
                new NotificationCategoryCallbackData(Category.Id),
                cancellationToken)));
    }
}

public sealed class QqFriendAddNotificationSource(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options)
    : QqApprovalNotificationSource(scopeFactory, callbackStore, options)
{
    protected override PlatformRequestKind Kind => PlatformRequestKind.FriendAdd;
}

public sealed class QqGroupInviteNotificationSource(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options)
    : QqApprovalNotificationSource(scopeFactory, callbackStore, options)
{
    protected override PlatformRequestKind Kind => PlatformRequestKind.GroupInvite;
}

public sealed class QqGroupAddNotificationSource(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options)
    : QqApprovalNotificationSource(scopeFactory, callbackStore, options)
{
    protected override PlatformRequestKind Kind => PlatformRequestKind.GroupAdd;
}
