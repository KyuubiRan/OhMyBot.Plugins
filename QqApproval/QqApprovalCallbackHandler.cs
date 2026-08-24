using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval;

/// <summary>
/// 处理审批菜单的选择。两种动作：打开某条请求的同意/拒绝面板、以及最终裁决。
/// 权限在这里再校验一次——推送时校验的是「当时」的权限，点击可能发生在很久以后。
/// </summary>
public sealed class QqApprovalCallbackHandler(
    IServiceScopeFactory scopeFactory,
    CallbackActionStore callbackStore,
    IOptions<QqApprovalOptions> options) : IPluginCallbackHandler
{
    public const string DecideActionType = "qqapproval-decide";
    public const string OpenActionType = "qqapproval-open";

    private readonly QqApprovalOptions _options = options.Value;

    public IReadOnlyCollection<string> ActionTypes { get; } = [DecideActionType, OpenActionType];

    public async Task<CommandResponse> ExecuteAsync(
        string actionType,
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        var required = _options.ResolveApprovalRequiredPrivilege();
        if (context.Identity.Privilege < required)
        {
            return PluginCallbackResponses.Error(
                context.Identity,
                editMessageId,
                $"需要 {UserPrivilegeText(required)} 权限才能审批。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<QqApprovalService>();

        return actionType switch
        {
            DecideActionType => await DecideAsync(service, context, action, cancellationToken),
            OpenActionType => await OpenAsync(service, context, action, editMessageId, cancellationToken),
            _ => PluginCallbackResponses.Error(context.Identity, editMessageId, "未知的审批操作。")
        };
    }

    private static async Task<CommandResponse> DecideAsync(
        QqApprovalService service,
        CommandContext context,
        CallbackAction action,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<QqApprovalDecideData>(action);
        if (data is null)
        {
            return CommandResponses.Error("QqApprovalBadPayload", "审批数据已失效，请用 /qqreq list 重新操作。", context);
        }

        var message = await service.DecideAsync(
            data.RequestId,
            data.Approve,
            context.Identity.CoreUserId,
            cancellationToken);
        return CommandResponses.Text(message, context);
    }

    private async Task<CommandResponse> OpenAsync(
        QqApprovalService service,
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<QqApprovalOpenData>(action);
        if (data is null)
        {
            return CommandResponses.Error("QqApprovalBadPayload", "审批数据已失效，请用 /qqreq list 重新操作。", context);
        }

        var request = await service.FindAsync(data.RequestId, cancellationToken);
        if (request is null)
        {
            return CommandResponses.Error("QqApprovalNotFound", "未找到这条请求。", context);
        }

        if (request.Status != Data.Entities.QqApprovalStatus.Pending)
        {
            return CommandResponses.Text(
                $"这条请求已经处理过了（{QqApprovalService.FormatStatus(request.Status)}）。",
                context);
        }

        var panel = new PanelBuilder(callbackStore, context, QqApprovalPlugin.PluginId);
        var response = CommandResponses.TelegramPlain(context.Identity, QqApprovalService.Describe(request))
            .AsTelegramEditIfSpecified(editMessageId);
        return response.AddButtonRow(PanelBuilder.Row(
            await panel.ButtonAsync(DecideActionType, "同意", new QqApprovalDecideData(request.Id, true), cancellationToken),
            await panel.ButtonAsync(DecideActionType, "拒绝", new QqApprovalDecideData(request.Id, false), cancellationToken)));
    }

    private static string UserPrivilegeText(UserPrivilege privilege) => Contracts.UserPrivilegeNames.Format(privilege);
}

public sealed record QqApprovalOpenData(long RequestId);
