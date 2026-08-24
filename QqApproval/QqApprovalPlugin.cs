using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Qq;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.Plugins;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval;

/// <summary>
/// QQ 待审批请求（加好友 / 邀请进群 / 入群申请）转交有权限的订阅者审批。
/// 事件由 QQGateway 上报到 Core 的 PlatformRequestDispatcher，审批结果经消息总线回到网关执行。
/// </summary>
[OhMyBotPlugin(
    PluginId,
    "QqApproval",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
    SupportedPlatforms = PluginSupportedPlatforms.QQ)]
public sealed class QqApprovalPlugin : CommandPlugin
{
    public const string PluginId = "com.ohmybot.qqapproval";

    protected override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 宿主只往插件容器塞了 ILoggerFactory，开放泛型 ILogger<> 要插件自己注册。
        // 其它插件是靠 AddHttpClient 的副作用捡到的，本插件不发 HTTP，必须显式加，
        // 否则容器在宿主的 ValidateOnBuild 阶段就构造失败、插件直接 Faulted。
        services.TryAddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddOptions<QqApprovalOptions>().BindConfiguration("QqApproval");

        services.AddDbContext<QqApprovalDbContext>((provider, options) =>
        {
            var hostConfiguration = provider
                .GetRequiredService<IPluginHostServices>()
                .Services
                .GetRequiredService<IConfiguration>();
            var connectionString = hostConfiguration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
            }

            options.UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_QqApproval"));
        });

        // 宿主单例：回调 payload、QQ 编号菜单索引、出站消息总线。
        services.AddSingleton(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<CallbackActionStore>());
        services.AddSingleton(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<QqMenuStore>());
        services.AddSingleton<INotificationPublisher>(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<INotificationPublisher>());
        services.AddSingleton<IPlatformRequestDecisionPublisher>(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<IPlatformRequestDecisionPublisher>());
        services.AddScoped<INotificationSubscriptionService, HostNotificationSubscriptionServiceBridge>();

        services.AddScoped<QqApprovalSettingsService>();
        services.AddScoped<QqApprovalService>();
        services.AddPluginEfBaseline<QqApprovalDbContext>(new EfBaselineOptions(PluginId));
    }

    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
        builder.AddPlatformCommand<QqApprovalCommandDslProvider>();
        builder.AddCallbackHandler<QqApprovalCallbackHandler>();
        builder.AddNotificationSource<QqFriendAddNotificationSource>();
        builder.AddNotificationSource<QqGroupInviteNotificationSource>();
        builder.AddNotificationSource<QqGroupAddNotificationSource>();
        builder.AddPluginHostedService<QqApprovalListenerService>();
    }
}
