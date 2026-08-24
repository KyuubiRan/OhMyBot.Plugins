using Microsoft.Extensions.DependencyInjection;
using OhMyBot.Plugin.Abstractions;

namespace OhMyBot.Plugins.PlaywrightProvider;

[OhMyBotPlugin(
    PluginId,
    "Playwright Provider",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 1000,
    SupportedPlatforms = PluginSupportedPlatforms.All)]
public sealed class PlaywrightProviderPlugin : BasicPlugin
{
    public const string PluginId = "com.ohmybot.playwright-provider";

    public override void Configure(IPluginBuilder builder)
    {
        builder.Services.AddLogging();
        builder.Services.AddOptions<PlaywrightProviderOptions>()
            .Bind(builder.Configuration.GetSection("Playwright"));
        builder.Export<IPlaywrightProvider, SharedPlaywrightProvider>();
    }
}
