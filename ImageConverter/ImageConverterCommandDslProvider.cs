using System.Text.RegularExpressions;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;

namespace OhMyBot.Plugins.ImageConverter;

public sealed partial class ImageConverterCommandDslProvider(
    OpenCvImageConverter imageConverter,
    FfmpegStickerConverter ffmpegStickerConverter,
    ILogger<ImageConverterCommandDslProvider> logger) : IPlatformCommandDslProvider
{
    private const string Usage = "用法（回复图片/GIF/视频）：/imgcvt <png|jpg|webp|sticker> [w=宽度] [h=高度] [q=质量]";

    public IEnumerable<CommandDslNode> GetNodes()
    {
        return
        [
            new CommandDslNode
            {
                Name = "imgcvt",
                Description = "转换图片格式或生成 Telegram 静态/动态贴纸",
                Usage = Usage,
                RequiredPrivilege = UserPrivilege.User,
                SupportPlatforms = SupportedPlatforms.Telegram,
                SupportChatTypes = SupportedChatTypes.All,
                AcceptsReplyMedia = true,
                Handler = ConvertAsync
            }
        ];
    }

    private async Task<CommandResponse> ConvertAsync(CommandContext context)
    {
        var media = context.Request.ReplyMedia;
        if (context.Request.Args.Count == 0 || media is null)
        {
            return CommandResponses.Text(Usage, context);
        }

        if (!ImageOutputFormatExtensions.TryParse(context.Request.Args[0], out var format))
        {
            return CommandResponses.Error(
                "ImageFormatUnsupported",
                $"不支持的格式：{context.Request.Args[0]}。支持 png、jpg、webp、sticker。",
                context);
        }

        if (!TryParseOptions(context.Request.Args.Skip(1), format, out var options, out var validationError))
        {
            return CommandResponses.Error("ImageOptionsInvalid", validationError, context);
        }

        if (IsTgsSticker(media))
        {
            return CommandResponses.Error(
                "AnimatedStickerUnsupported",
                "Telegram 的 TGS 动态贴纸是 Lottie 格式，FFmpeg 不能转换；请回复 GIF、动画或视频。",
                context);
        }

        try
        {
            var sourceName = Path.GetFileNameWithoutExtension(media.FileName);
            var safeName = SafeFileName().Replace(
                string.IsNullOrWhiteSpace(sourceName) ? "image" : sourceName,
                "_");

            if (format == ImageOutputFormat.Sticker && IsAnimatedMedia(media))
            {
                var result = await ffmpegStickerConverter.ConvertAsync(
                    media.Content.ToByteArray(),
                    media.FileName,
                    context.CancellationToken);
                return CommandResponses.TelegramSticker(
                    context.Identity,
                    $"imgcvt_{safeName}.{result.Extension}",
                    result.ContentType,
                    result.Content,
                    replyToMessageId: context.Request.MessageId);
            }

            var imageResult = imageConverter.Convert(media.Content.ToByteArray(), options);
            if (format == ImageOutputFormat.Sticker)
            {
                return CommandResponses.TelegramSticker(
                    context.Identity,
                    $"imgcvt_{safeName}.{imageResult.Extension}",
                    imageResult.ContentType,
                    imageResult.Content,
                    replyToMessageId: context.Request.MessageId);
            }

            return CommandResponses.TelegramDocument(
                context.Identity,
                $"imgcvt_{safeName}.{imageResult.Extension}",
                imageResult.ContentType,
                imageResult.Content,
                replyToMessageId: context.Request.MessageId);
        }
        catch (FfmpegUnavailableException exception)
        {
            logger.LogWarning(exception, "FFmpeg is unavailable for animated sticker conversion.");
            return CommandResponses.Error(
                "FfmpegUnavailable",
                "服务器未安装 FFmpeg，动态 sticker 暂不可用；请安装 ffmpeg 或设置 ImageConverter:FfmpegPath。",
                context);
        }
        catch (ImageConversionException exception)
        {
            var errorId = Guid.NewGuid().ToString("N")[..6];
            logger.LogWarning(
                exception,
                "Image conversion failed. errorId={ErrorId}, format={Format}.",
                errorId,
                format);
            return CommandResponses.Error(
                "ImageConversionFailed",
                $"图片转换失败，请确认回复的是有效图片或视频。（错误 id: {errorId}）",
                context);
        }
    }

    private static bool IsTgsSticker(CommandMedia media)
        => media.Kind == CommandMediaKind.Sticker
            && (media.ContentType.Contains("tgsticker", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(media.FileName).Equals(".tgs", StringComparison.OrdinalIgnoreCase));

    private static bool IsAnimatedMedia(CommandMedia media)
    {
        if (media.Kind is CommandMediaKind.Animation or CommandMediaKind.Video)
        {
            return true;
        }

        if (media.Kind == CommandMediaKind.Sticker)
        {
            return media.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }

        if (media.Kind != CommandMediaKind.Document)
        {
            return false;
        }

        return media.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || media.ContentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(media.FileName).ToLowerInvariant() is ".gif" or ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv";
    }

    private static bool TryParseOptions(
        IEnumerable<string> args,
        ImageOutputFormat format,
        out ImageConversionOptions options,
        out string error)
    {
        int? width = null;
        int? height = null;
        var quality = 100;

        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var value))
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "w" or "width":
                    width = value;
                    break;
                case "h" or "height":
                    height = value;
                    break;
                case "q" or "quality":
                    quality = int.Clamp(value, 1, 100);
                    break;
            }
        }

        if (width is <= 0 or > OpenCvImageConverter.MaxDimension
            || height is <= 0 or > OpenCvImageConverter.MaxDimension)
        {
            options = default!;
            error = $"宽高必须在 1 到 {OpenCvImageConverter.MaxDimension} 之间。";
            return false;
        }

        options = new ImageConversionOptions(format, width, height, quality);
        error = string.Empty;
        return true;
    }

    [GeneratedRegex("[^a-zA-Z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();
}
