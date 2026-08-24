using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.Security;

namespace OhMyBot.Plugins.Mihoyo;

internal sealed class HostSecretProtector(IServiceScopeFactory hostScopeFactory) : ISecretProtector
{
    public string Protect(string plaintext)
    {
        using var scope = hostScopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(plaintext);
    }

    public string Unprotect(string ciphertext)
    {
        using var scope = hostScopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(ciphertext);
    }
}

internal sealed class HostNotificationPublisher(IServiceScopeFactory hostScopeFactory) : INotificationPublisher
{
    public async Task PublishAsync(
        BotPlatform platform,
        string botInstanceId,
        string chatId,
        IReadOnlyList<string> messages,
        IReadOnlyList<string>? menuTokens = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = hostScopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotificationPublisher>()
            .PublishAsync(platform, botInstanceId, chatId, messages, menuTokens, cancellationToken);
    }

    public async Task PublishTelegramAsync(
        string botInstanceId,
        string chatId,
        IReadOnlyList<string> messages,
        CancellationToken cancellationToken = default)
    {
        await using var scope = hostScopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotificationPublisher>()
            .PublishTelegramAsync(botInstanceId, chatId, messages, cancellationToken);
    }
}
