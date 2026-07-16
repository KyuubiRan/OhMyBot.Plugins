using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Infrastructure.Data;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.Plugins;
using OhMyBot.Core.Infrastructure.ScheduledTasks;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Kuro;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.Kuro;

[OhMyBotPlugin(
    "com.ohmybot.kuro",
    "Kuro",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
    SupportedPlatforms = PluginSupportedPlatforms.All)]
public sealed class KuroPlugin : CommandPlugin
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<KuroOptions>()
            .BindConfiguration("Kuro");
        services.AddOptions<ScheduledTaskOptions>("KuroAutoSign")
            .Configure<IConfiguration>((options, configuration) =>
                ScheduledTaskOptions.Bind(options, configuration.GetSection("ScheduledTask")));

        services.AddDbContext<KuroDbContext>((serviceProvider, options) =>
        {
            var hostServices = serviceProvider.GetRequiredService<IPluginHostServices>();
            var hostConfiguration = hostServices.Services.GetRequiredService<IConfiguration>();
            var connectionString = hostConfiguration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionStrings:Postgres is required by the Kuro plugin.");
            }

            options.UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Kuro"));
        });

        services.AddSingleton(serviceProvider =>
            GetHostService<CallbackActionStore>(serviceProvider));
        services.AddScoped<ISecretProtector, HostSecretProtectorBridge>();
        services.AddSingleton<INotificationPublisher>(serviceProvider =>
            GetHostService<INotificationPublisher>(serviceProvider));

        services.AddScoped<INotificationSubscriptionService, HostNotificationSubscriptionServiceBridge>();
        services.AddPluginEfBaseline<KuroDbContext>(new EfBaselineOptions(
            "com.ohmybot.kuro", "__EFMigrationsHistory_Kuro", ["KuroAccounts", "KuroGameRoles"], "KuroAccounts"));
        services.AddScoped<KuroAccountService>();
        services.AddScoped<KuroSignService>();
        services.AddScoped<KuroResponseBuilder>();

        services.AddHttpClient<KuroHttpClient>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<KuroOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = options.Timeout;
        });
    }

    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
        builder.AddPlatformCommand<KuroCommandDslProvider>();
        builder.AddCallbackHandler<KuroCallbackHandler>();
        builder.AddNotificationSource<KuroNotificationSource>();
        builder.AddManagedTask<KuroAutoSignManagedTask>();
    }

    private static TService GetHostService<TService>(IServiceProvider serviceProvider)
        where TService : notnull
    {
        return serviceProvider
            .GetRequiredService<IPluginHostServices>()
            .Services
            .GetRequiredService<TService>();
    }
}

internal sealed class HostSecretProtectorBridge(IPluginHostServices hostServices) : ISecretProtector
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
