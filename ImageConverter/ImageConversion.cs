namespace OhMyBot.Plugins.ImageConverter;

public enum ImageOutputFormat
{
    Png,
    Jpeg,
    Webp,
    Sticker
}

public sealed record ImageConversionOptions(
    ImageOutputFormat Format,
    int? Width = null,
    int? Height = null,
    int Quality = 100);

public sealed record ImageConversionResult(
    byte[] Content,
    string Extension,
    string ContentType,
    int Width,
    int Height);

public enum VideoStickerBackground
{
    Transparent,
    Black,
    White
}

public sealed record VideoStickerConversionOptions(
    int Quality = 100,
    double? FrameRate = null,
    VideoStickerBackground Background = VideoStickerBackground.Transparent);

public class ImageConversionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class FileSizeLimitExceededException(long actualBytes, long limitBytes)
    : ImageConversionException($"Converted file size {actualBytes} exceeds the limit of {limitBytes} bytes.")
{
    public long ActualBytes { get; } = actualBytes;

    public long LimitBytes { get; } = limitBytes;
}

public readonly record struct ImageSize(int Width, int Height);

public static class ImageConversionLimits
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 40_000_000;
    public const int MaxStaticStickerBytes = 512 * 1024;

    public static ImageSize ResolveOutputSize(
        int sourceWidth,
        int sourceHeight,
        ImageConversionOptions options)
    {
        ValidateSize(sourceWidth, sourceHeight, "Input image dimensions are outside the allowed range.");

        var boundWidth = options.Width ?? sourceWidth;
        var boundHeight = options.Height ?? sourceHeight;
        ValidateSize(boundWidth, boundHeight, "Output image dimensions are outside the allowed range.");

        if (options.Format == ImageOutputFormat.Sticker)
        {
            var stickerScale = 512d / Math.Max(boundWidth, boundHeight);
            boundWidth = Math.Max(1, (int)Math.Round(boundWidth * stickerScale));
            boundHeight = Math.Max(1, (int)Math.Round(boundHeight * stickerScale));
        }

        var scale = Math.Min((double)boundWidth / sourceWidth, (double)boundHeight / sourceHeight);
        var output = new ImageSize(
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        ValidateSize(output.Width, output.Height, "Output image dimensions are outside the allowed range.");
        return output;
    }

    private static void ValidateSize(int width, int height, string error)
    {
        if (width <= 0 || height <= 0
            || width > MaxDimension || height > MaxDimension
            || (long)width * height > MaxPixels)
        {
            throw new ImageConversionException(error);
        }
    }
}

public static class ImageOutputFormatExtensions
{
    public static bool TryParse(string value, out ImageOutputFormat format)
    {
        switch (value.ToLowerInvariant())
        {
            case "png":
                format = ImageOutputFormat.Png;
                return true;
            case "jpg":
            case "jpeg":
                format = ImageOutputFormat.Jpeg;
                return true;
            case "webp":
                format = ImageOutputFormat.Webp;
                return true;
            case "sticker":
                format = ImageOutputFormat.Sticker;
                return true;
            default:
                format = default;
                return false;
        }
    }
}
