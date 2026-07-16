using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Infrastructure.Data;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.Plugins;
using OhMyBot.Core.Infrastructure.ScheduledTasks;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Skland;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.Skland;

[OhMyBotPlugin(
    "com.ohmybot.skland",
    "Skland",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
    SupportedPlatforms = PluginSupportedPlatforms.All)]
public sealed class SklandPlugin : CommandPlugin
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<SklandOptions>()
            .BindConfiguration("Skland");
        services.AddOptions<ScheduledTaskOptions>("SklandAutoSign")
            .Configure<IConfiguration>((options, configuration) =>
                ScheduledTaskOptions.Bind(options, configuration.GetSection("ScheduledTask")))
            .ValidateOnStart();

        services.AddDbContext<SklandDbContext>((provider, options) =>
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
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Skland"));
        });

        services.AddSingleton(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<CallbackActionStore>());
        services.AddSingleton<INotificationPublisher>(provider => provider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<INotificationPublisher>());
        services.AddScoped<ISecretProtector, HostSecretProtectorBridge>();

        services.AddScoped<INotificationSubscriptionService, HostNotificationSubscriptionServiceBridge>();
        services.AddPluginEfBaseline<SklandDbContext>(new EfBaselineOptions(
            "com.ohmybot.skland", "__EFMigrationsHistory_Skland", ["SklandAccounts", "SklandGameRoles"], "SklandAccounts"));
        services.AddHttpClient<SklandHttpClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SklandOptions>>().Value;
            client.Timeout = options.Timeout;
        });
        services.AddScoped<SklandAccountService>();
        services.AddScoped<SklandSignService>();
        services.AddScoped<SklandResponseBuilder>();
    }

    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
        builder.AddPlatformCommand<SklandCommandDslProvider>();
        builder.AddCallbackHandler<SklandCallbackHandler>();
        builder.AddNotificationSource<SklandNotificationSource>();
        builder.AddManagedTask<SklandAutoSignManagedTask>();
    }

    private sealed class HostSecretProtectorBridge(
        IPluginHostServices hostServices) : ISecretProtector
    {
        public string Protect(string plaintext)
        {
            using var scope = hostServices.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(plaintext);
        }

        public string Unprotect(string ciphertext)
        {
            using var scope = hostServices.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(ciphertext);
        }
    }
}
