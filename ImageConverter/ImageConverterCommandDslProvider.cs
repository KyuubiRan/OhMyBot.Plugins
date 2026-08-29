using System.Globalization;
using System.Text.RegularExpressions;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;

namespace OhMyBot.Plugins.ImageConverter;

public sealed partial class ImageConverterCommandDslProvider(
    FfmpegMediaConverter mediaConverter,
    ILogger<ImageConverterCommandDslProvider> logger) : IPlatformCommandDslProvider
{
    private static readonly string Usage = string.Join('\n',
        "回复一张图片、GIF 或视频后发送命令。",
        string.Empty,
        "普通图片",
        "`/imgcvt <png|jpg|webp> [w=宽度] [h=高度] [q=1-100]`",
        "• GIF/视频会取第一帧；PNG/WebP 保留透明通道。",
        string.Empty,
        "Telegram 贴纸",
        "`/imgcvt sticker [webp|webm] [w=宽度] [h=高度] [q=1-100] [fps=1-30] [bg=black|white]`",
        "• `webp`：静态贴纸，GIF/视频只取第一帧。",
        "• `webm`：动态贴纸，静态图片也可以转换。",
        "• 不指定格式：静态图片自动用 WebP，GIF/视频自动用 WebM。",
        string.Empty,
        "参数",
        "• `w` / `h`：输出边界，保持原比例，范围 1-8192。",
        "• `q`：质量范围 1-100；100 为基准，数值越低压缩越强。",
        "• `fps`：仅 WebM 使用；不指定则保留源帧率，最高 30 FPS。",
        "• `bg`：仅 WebM 使用；填充透明区域为黑色或白色，并移除 Alpha 以便按目标码率压制。",
        "• WebM 最长 3 秒、最大 256KB；静态贴纸最大 512KB。",
        string.Empty,
        "示例",
        "`/imgcvt webp w=1920 q=85`",
        "`/imgcvt sticker webp q=90`",
        "`/imgcvt sticker webm q=100 fps=30 bg=white`");

    private static readonly string ReplyMediaRequired = string.Join('\n',
        "没有检测到回复的媒体。",
        "请先回复一张图片、GIF 或视频，再发送转换命令。",
        string.Empty,
        "例如：`/imgcvt webp q=85`",
        "发送 `/imgcvt` 查看完整参数和示例。");

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
                ProgressStyle = CommandProgressStyle.MediaConversion,
                Handler = ConvertAsync
            }
        ];
    }

    private async Task<CommandResponse> ConvertAsync(CommandContext context)
    {
        if (context.Request.Args.Count == 0)
        {
            return CommandResponses.Text(Usage, context);
        }

        var media = context.Request.ReplyMedia;
        if (media is null)
        {
            return CommandResponses.Text(ReplyMediaRequired, context);
        }

        if (!ImageOutputFormatExtensions.TryParse(context.Request.Args[0], out var format))
        {
            return CommandResponses.Error(
                "ImageFormatUnsupported",
                $"不支持的格式：{context.Request.Args[0]}。支持 png、jpg、webp、sticker。",
                context);
        }

        if (!TryParseArguments(
                context.Request.Args.Skip(1),
                format,
                out var arguments,
                out var validationError))
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
            var input = media.Content.ToByteArray();
            var detectedFormat = MediaHeaderDetector.Detect(input);
            var isAnimated = IsAnimatedMedia(media, detectedFormat);
            var stickerOutput = arguments.StickerOutput == StickerOutputFormat.Auto
                ? isAnimated
                    ? StickerOutputFormat.Webm
                    : StickerOutputFormat.Webp
                : arguments.StickerOutput;

            if (format == ImageOutputFormat.Sticker && stickerOutput == StickerOutputFormat.Webm)
            {
                if (!TryCreateVideoStickerOptions(arguments, out var videoOptions, out validationError))
                {
                    return CommandResponses.Error("ImageOptionsInvalid", validationError, context);
                }

                var result = await mediaConverter.ConvertVideoStickerAsync(
                    input,
                    media.FileName,
                    videoOptions,
                    context.CancellationToken);
                return CommandResponses.TelegramSticker(
                    context.Identity,
                    $"imgcvt_{safeName}.{result.Extension}",
                    result.ContentType,
                    result.Content,
                    replyToMessageId: context.Request.MessageId);
            }

            if (!TryCreateImageOptions(arguments, format, out var imageOptions, out validationError))
            {
                return CommandResponses.Error("ImageOptionsInvalid", validationError, context);
            }

            var imageResult = await mediaConverter.ConvertImageAsync(
                input,
                media.FileName,
                imageOptions,
                context.CancellationToken);
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
            logger.LogWarning(exception, "FFmpeg or FFprobe is unavailable for media conversion.");
            return CommandResponses.Error(
                "FfmpegUnavailable",
                "服务器缺少 FFmpeg/FFprobe，图片转换暂不可用；请安装对应工具或检查 ImageConverter 路径配置。",
                context);
        }
        catch (FileSizeLimitExceededException exception)
        {
            var errorId = Guid.NewGuid().ToString("N")[..6];
            logger.LogWarning(
                exception,
                "Converted file exceeds its size limit. errorId={ErrorId}, actualBytes={ActualBytes}, limitBytes={LimitBytes}, format={Format}.",
                errorId,
                exception.ActualBytes,
                exception.LimitBytes,
                format);
            return CreateFileSizeLimitResponse(context, exception.LimitBytes, errorId);
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

    internal static CommandResponse CreateFileSizeLimitResponse(
        CommandContext context,
        long limitBytes,
        string errorId)
    {
        var limitKilobytes = (long)Math.Ceiling(limitBytes / 1024d);
        return CommandResponses.Error(
            "FileExceedTheLimit",
            $"文件超出大小({limitKilobytes}KB)（错误 id: {errorId}）",
            context);
    }

    private static bool IsTgsSticker(CommandMedia media)
        => media.Kind == CommandMediaKind.Sticker
            && (media.ContentType.Contains("tgsticker", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(media.FileName).Equals(".tgs", StringComparison.OrdinalIgnoreCase));

    private static bool IsAnimatedMedia(CommandMedia media, DetectedMediaFormat? detectedFormat)
    {
        if (detectedFormat is not null)
        {
            return detectedFormat.Value.IsAnimated;
        }

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

    private static bool TryParseArguments(
        IEnumerable<string> args,
        ImageOutputFormat format,
        out ParsedConversionArguments options,
        out string error)
    {
        int? width = null;
        int? height = null;
        string? quality = null;
        string? frameRate = null;
        string? background = null;
        var stickerOutput = StickerOutputFormat.Auto;

        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2)
            {
                if (format == ImageOutputFormat.Sticker
                    && TryParseStickerOutput(arg, out var positionalOutput))
                {
                    stickerOutput = positionalOutput;
                }

                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "w" or "width":
                    if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth))
                    {
                        width = parsedWidth;
                    }
                    break;
                case "h" or "height":
                    if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight))
                    {
                        height = parsedHeight;
                    }
                    break;
                case "q" or "quality":
                    quality = parts[1];
                    break;
                case "fps":
                    frameRate = parts[1];
                    break;
                case "bg" or "background" or "alpha":
                    background = parts[1];
                    break;
                case "type" or "format" when format == ImageOutputFormat.Sticker:
                    if (!TryParseStickerOutput(parts[1], out stickerOutput))
                    {
                        options = default!;
                        error = "sticker 输出格式只支持 webp 或 webm。";
                        return false;
                    }
                    break;
            }
        }

        if (width is <= 0 or > ImageConversionLimits.MaxDimension
            || height is <= 0 or > ImageConversionLimits.MaxDimension)
        {
            options = default!;
            error = $"宽高必须在 1 到 {ImageConversionLimits.MaxDimension} 之间。";
            return false;
        }

        options = new ParsedConversionArguments(
            width,
            height,
            quality,
            frameRate,
            background,
            stickerOutput);
        error = string.Empty;
        return true;
    }

    private static bool TryCreateImageOptions(
        ParsedConversionArguments arguments,
        ImageOutputFormat format,
        out ImageConversionOptions options,
        out string error)
    {
        if (!string.IsNullOrWhiteSpace(arguments.FrameRate))
        {
            options = default!;
            error = "fps 只在转换为 sticker webm 时有效。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(arguments.Background))
        {
            options = default!;
            error = "bg 只在转换为 sticker webm 时有效。";
            return false;
        }

        var quality = 100;
        if (!string.IsNullOrWhiteSpace(arguments.Quality)
            && !int.TryParse(
                arguments.Quality,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out quality))
        {
            options = default!;
            error = "图片/WebP 的 q 必须是 1 到 100 的整数。";
            return false;
        }

        quality = int.Clamp(quality, 1, 100);
        options = new ImageConversionOptions(format, arguments.Width, arguments.Height, quality);
        error = string.Empty;
        return true;
    }

    private static bool TryCreateVideoStickerOptions(
        ParsedConversionArguments arguments,
        out VideoStickerConversionOptions options,
        out string error)
    {
        var quality = 100;
        if (!string.IsNullOrWhiteSpace(arguments.Quality)
            && (!int.TryParse(
                    arguments.Quality,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out quality)
                || quality < 1
                || quality > 100))
        {
            options = default!;
            error = "WebM 的 q 必须是 1 到 100 的整数；100 为基准质量，50 表示按 50% 质量起步压制。";
            return false;
        }

        double? frameRate = null;
        if (!string.IsNullOrWhiteSpace(arguments.FrameRate))
        {
            if (!double.TryParse(
                    arguments.FrameRate,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedFrameRate)
                || parsedFrameRate < 1d
                || parsedFrameRate > FfmpegMediaConverter.MaximumPreservedFrameRate)
            {
                options = default!;
                error = $"WebM 的 fps 必须在 1 到 {FfmpegMediaConverter.MaximumPreservedFrameRate} 之间。";
                return false;
            }

            frameRate = parsedFrameRate;
        }

        var background = arguments.Background?.ToLowerInvariant() switch
        {
            null or "" => VideoStickerBackground.Transparent,
            "black" => VideoStickerBackground.Black,
            "white" => VideoStickerBackground.White,
            _ => (VideoStickerBackground?)null
        };
        if (background is null)
        {
            options = default!;
            error = "WebM 的 bg 只支持 black 或 white；不指定则保留透明 Alpha。";
            return false;
        }

        options = new VideoStickerConversionOptions(quality, frameRate, background.Value);
        error = string.Empty;
        return true;
    }

    private static bool TryParseStickerOutput(string value, out StickerOutputFormat output)
    {
        switch (value.ToLowerInvariant())
        {
            case "webp":
                output = StickerOutputFormat.Webp;
                return true;
            case "webm":
                output = StickerOutputFormat.Webm;
                return true;
            default:
                output = default;
                return false;
        }
    }

    private enum StickerOutputFormat
    {
        Auto,
        Webp,
        Webm
    }

    private sealed record ParsedConversionArguments(
        int? Width,
        int? Height,
        string? Quality,
        string? FrameRate,
        string? Background,
        StickerOutputFormat StickerOutput);

    [GeneratedRegex("[^a-zA-Z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();
}
