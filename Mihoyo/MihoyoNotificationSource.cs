using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Integrations.Mihoyo;

namespace OhMyBot.Plugins.Mihoyo;

public sealed class MihoyoNotificationSource(IServiceScopeFactory scopeFactory) : IPluginNotificationSource
{
    public string Type => NotificationTypes.MihoyoAutoSign;

    public string DisplayName => NotificationTypes.MihoyoAutoSignDisplayName;

    public int Order => 300;

    public async Task<bool> HasEnabledTargetsAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<INotificationSubscriptionService>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        var enabled = await subscriptionService.GetEnabledTargetIdsAsync(
            context.Identity.CoreUserId,
            context.Request.Platform,
            Type,
            accounts.Select(account => account.Id).ToArray(),
            cancellationToken);
        return enabled.Count > 0;
    }

    public async Task<CommandResponse> BuildAccountPanelAsync(
        CommandContext context,
        string? editMessageId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        return await builder.BuildNotifyAccountPanelAsync(
            context,
            accounts,
            editMessageId,
            cancellationToken);
    }

    public async Task<CommandResponse> ToggleAsync(
        CommandContext context,
        long accountId,
        bool toggleAll,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<INotificationSubscriptionService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            cancellationToken: cancellationToken);

        if (toggleAll)
        {
            await subscriptionService.ToggleAllAsync(
                context.Identity.CoreUserId,
                context.Request.Platform,
                context.Request.BotInstanceId,
                context.Request.ChatId,
                Type,
                accounts.Select(account => account.Id).ToArray(),
                cancellationToken);
        }
        else if (accounts.Any(account => account.Id == accountId))
        {
            await subscriptionService.ToggleAsync(
                context.Identity.CoreUserId,
                context.Request.Platform,
                context.Request.BotInstanceId,
                context.Request.ChatId,
                Type,
                accountId,
                cancellationToken);
        }
        else
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var updatedAccounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        return await builder.BuildNotifyAccountPanelAsync(
            context,
            updatedAccounts,
            editMessageId,
            cancellationToken);
    }

    private static CommandResponse CallbackError(CommandContext context, string editMessageId, string message)
    {
        return new CommandResponse
        {
            Code = 1,
            ErrorCode = "CallbackRejected",
            CallbackAnswerText = message,
            CallbackAnswerAlert = false,
            Context = new CommandResponseContext
            {
                CallerCoreUserId = context.Identity.CoreUserId,
                CallerPrivilege = context.Identity.Privilege,
                Platform = context.Identity.Platform
            },
            Telegram = new TelegramResponse
            {
                Messages =
                {
                    new TelegramMessage
                    {
                        Text = $"错误：{message}（CallbackRejected）",
                        ParseMode = TelegramParseMode.None,
                        EditMessageId = editMessageId
                    }
                }
            }
        };
    }
}
