using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Integrations.Kuro;

namespace OhMyBot.Plugins.Kuro;

public sealed class KuroNotificationSource(IServiceScopeFactory scopeFactory) : IPluginNotificationSource
{
    public string Type => NotificationTypes.KuroAutoSign;

    public string DisplayName => NotificationTypes.KuroAutoSignDisplayName;

    public int Order => 200;

    public async Task<bool> HasEnabledTargetsAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
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
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        return await builder.BuildNotifyAccountPanelAsync(context, accounts, editMessageId, cancellationToken);
    }

    public async Task<CommandResponse> ToggleAsync(
        CommandContext context,
        long accountId,
        bool toggleAll,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<INotificationSubscriptionService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
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
            return KuroCallbackHandler.CallbackError(
                context.Identity,
                editMessageId,
                "未找到指定库街区账号。");
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
}
