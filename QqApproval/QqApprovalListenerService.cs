using Microsoft.Extensions.Hosting;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Platform;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval;

/// <summary>
/// 订阅 Core 的平台待审批请求分发。用 hosted service 而非新的插件组件类型：
/// 宿主已经负责 hosted service 的启停，插件卸载时退订随之发生。
/// </summary>
public sealed class QqApprovalListenerService(
    IPluginHostServices hostServices,
    IServiceScopeFactory scopeFactory,
    ILogger<QqApprovalListenerService> logger) : IHostedService, IPlatformRequestListener
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dispatcher = hostServices.Services.GetRequiredService<PlatformRequestDispatcher>();
        _subscription = dispatcher.Subscribe(this);
        logger.LogInformation("QqApproval 已订阅平台待审批请求。");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public async Task OnPlatformRequestAsync(PlatformRequestNotice notice, CancellationToken cancellationToken = default)
    {
        // 本插件只管 QQ；其它平台的请求留给各自的插件。
        if (notice.Platform != BotPlatform.Qq)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<QqApprovalService>();
        await service.HandleAsync(notice, cancellationToken);
    }
}
