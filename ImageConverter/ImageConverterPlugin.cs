using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.ImageConverter;

[OhMyBotPlugin(
    PluginId,
    "ImageConverter",
    "1.0.0",
    CoreApi = "[1.0.0,2.0.0)",
    LoadPriority = 100,
    SupportedPlatforms = PluginSupportedPlatforms.Telegram)]
public sealed class ImageConverterPlugin : CommandPlugin
{
    public const string PluginId = "com.ohmybot.imageconverter";

    protected override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddOptions<ImageConverterOptions>().BindConfiguration("ImageConverter");
        services.AddSingleton<OpenCvImageConverter>();
        services.AddSingleton<FfmpegStickerConverter>();
    }

    protected override void ConfigureCommanding(ICommandPluginBuilder builder)
    {
        builder.AddPlatformCommand<ImageConverterCommandDslProvider>();
    }
}
