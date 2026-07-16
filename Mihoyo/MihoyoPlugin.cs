using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Infrastructure.Data;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.Plugins;
using OhMyBot.Core.Infrastructure.ScheduledTasks;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Mihoyo;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.Mihoyo;

[OhMyBotPlugin(
    "com.ohmybot.mihoyo",
    "Mihoyo",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
    SupportedPlatforms = PluginSupportedPlatforms.All)]
public sealed class MihoyoPlugin : CommandPlugin
{
    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<MihoyoOptions>()
            .Bind(builder.Configuration.GetSection("Mihoyo"));
        services.AddOptions<ScheduledTaskOptions>("MihoyoAutoSign")
            .Configure(options => ScheduledTaskOptions.Bind(
                options,
                builder.Configuration.GetSection("ScheduledTasks:MihoyoAutoSign")))
            .ValidateOnStart();

        services.TryAddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddDbContext<MihoyoDbContext>((serviceProvider, options) =>
        {
            var hostServices = serviceProvider.GetRequiredService<IPluginHostServices>();
            var hostConfiguration = hostServices.Services.GetRequiredService<IConfiguration>();
            var connectionString = builder.Configuration.GetConnectionString("Postgres")
                ?? hostConfiguration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionStrings:Postgres is required by the Mihoyo plugin.");
            }

            options.UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Mihoyo"));
        });

        services.AddSingleton(serviceProvider =>
        {
            var hostServices = serviceProvider.GetRequiredService<IPluginHostServices>();
            return hostServices.Services.GetRequiredService<CallbackActionStore>();
        });
        services.AddSingleton<ISecretProtector>(serviceProvider =>
        {
            var hostServices = serviceProvider.GetRequiredService<IPluginHostServices>();
            return new HostSecretProtector(
                hostServices.Services.GetRequiredService<IServiceScopeFactory>());
        });
        services.AddSingleton<INotificationPublisher>(serviceProvider =>
        {
            var hostServices = serviceProvider.GetRequiredService<IPluginHostServices>();
            return new HostNotificationPublisher(
                hostServices.Services.GetRequiredService<IServiceScopeFactory>());
        });

        services.AddScoped<INotificationSubscriptionService, HostNotificationSubscriptionServiceBridge>();
        services.AddPluginEfBaseline<MihoyoDbContext>(new EfBaselineOptions(
            "com.ohmybot.mihoyo", "__EFMigrationsHistory_Mihoyo", ["MihoyoAccounts", "MihoyoGameRoles"], "MihoyoAccounts"));
        services.AddScoped<MihoyoAccountService>();
        services.AddScoped<MihoyoSignService>();
        services.AddScoped<MihoyoResponseBuilder>();
        services.AddHttpClient<MihoyoHttpClient>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<MihoyoOptions>>()
                .Value;
            client.Timeout = options.Timeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false
        });

        builder.AddPlatformCommand<MihoyoCommandDslProvider>();
        builder.AddCallbackHandler<MihoyoCallbackHandler>();
        builder.AddNotificationSource<MihoyoNotificationSource>();
        builder.AddManagedTask<MihoyoAutoSignManagedTask>();
    }
}
