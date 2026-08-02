using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.TemplatePluginName;

[OhMyBotPlugin(
    "__PLUGIN_ID__",
    "__DISPLAY_NAME__",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
#if (SupportedPlatforms == "Telegram")
    SupportedPlatforms = PluginSupportedPlatforms.Telegram)]
#elif (SupportedPlatforms == "QQ")
    SupportedPlatforms = PluginSupportedPlatforms.QQ)]
#else
    SupportedPlatforms = PluginSupportedPlatforms.All)]
#endif
public sealed class TemplatePluginNamePlugin : CommandPlugin
{
    protected override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
    }
}
