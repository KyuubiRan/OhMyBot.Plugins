using OhMyBot.Contracts;
using OpenCvSharp;

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

public sealed class ImageConversionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OpenCvImageConverter
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 40_000_000;
    public const int MaxStaticStickerBytes = 512 * 1024;

    public ImageConversionResult Convert(byte[] input, ImageConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || input.Length > CommandMediaLimits.MaxContentBytes)
        {
            throw new ImageConversionException("Input image size is outside the allowed range.");
        }

        try
        {
            using var source = Cv2.ImDecode(input, ImreadModes.Unchanged);
            if (source.Empty())
            {
                throw new ImageConversionException("OpenCV could not decode the input image.");
            }

            ValidateSource(source);
            var bounds = ResolveBounds(source.Width, source.Height, options);
            var outputSize = FitWithin(source.Width, source.Height, bounds.Width, bounds.Height);

            using var resized = new Mat();
            var encodeSource = source;
            if (source.Width != outputSize.Width || source.Height != outputSize.Height)
            {
                Cv2.Resize(
                    source,
                    resized,
                    outputSize,
                    interpolation: outputSize.Width < source.Width || outputSize.Height < source.Height
                        ? InterpolationFlags.Area
                        : InterpolationFlags.Lanczos4);
                encodeSource = resized;
            }

            using var jpegSource = PrepareJpegSource(encodeSource, options.Format);
            var finalSource = jpegSource ?? encodeSource;
            var encoding = ResolveEncoding(options);
            Cv2.ImEncode(encoding.Extension, finalSource, out var output, encoding.Parameters);

            if (output.Length == 0 || output.Length > CommandMediaLimits.MaxContentBytes)
            {
                throw new ImageConversionException("Converted image size is outside the allowed range.");
            }

            if (options.Format == ImageOutputFormat.Sticker && output.Length > MaxStaticStickerBytes)
            {
                throw new ImageConversionException(
                    $"Converted static sticker exceeds the Telegram limit of {MaxStaticStickerBytes} bytes.");
            }

            return new ImageConversionResult(
                output,
                encoding.Extension.TrimStart('.'),
                encoding.ContentType,
                outputSize.Width,
                outputSize.Height);
        }
        catch (ImageConversionException)
        {
            throw;
        }
        catch (OpenCVException exception)
        {
            throw new ImageConversionException("OpenCV failed to convert the image.", exception);
        }
    }

    private static void ValidateSource(Mat source)
    {
        if (source.Width <= 0 || source.Height <= 0
            || source.Width > MaxDimension || source.Height > MaxDimension
            || (long)source.Width * source.Height > MaxPixels)
        {
            throw new ImageConversionException("Input image dimensions are outside the allowed range.");
        }
    }

    private static Size ResolveBounds(int sourceWidth, int sourceHeight, ImageConversionOptions options)
    {
        var width = options.Width ?? sourceWidth;
        var height = options.Height ?? sourceHeight;
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension)
        {
            throw new ImageConversionException("Output image dimensions are outside the allowed range.");
        }

        if (options.Format != ImageOutputFormat.Sticker)
        {
            return new Size(width, height);
        }

        var scale = 512d / Math.Max(width, height);
        return new Size(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static Size FitWithin(int sourceWidth, int sourceHeight, int boundWidth, int boundHeight)
    {
        var scale = Math.Min((double)boundWidth / sourceWidth, (double)boundHeight / sourceHeight);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new ImageConversionException("Output image dimensions are outside the allowed range.");
        }

        return new Size(width, height);
    }

    private static Mat? PrepareJpegSource(Mat source, ImageOutputFormat format)
    {
        if (format != ImageOutputFormat.Jpeg || source.Channels() != 4)
        {
            return null;
        }

        var converted = new Mat();
        Cv2.CvtColor(source, converted, ColorConversionCodes.BGRA2BGR);
        return converted;
    }

    private static (string Extension, string ContentType, ImageEncodingParam[] Parameters) ResolveEncoding(
        ImageConversionOptions options)
    {
        var quality = int.Clamp(options.Quality, 1, 100);
        return options.Format switch
        {
            ImageOutputFormat.Png => (
                ".png",
                "image/png",
                [new ImageEncodingParam(ImwriteFlags.PngCompression, 3)]),
            ImageOutputFormat.Jpeg => (
                ".jpg",
                "image/jpeg",
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]),
            ImageOutputFormat.Webp or ImageOutputFormat.Sticker => (
                ".webp",
                "image/webp",
                [new ImageEncodingParam(ImwriteFlags.WebPQuality, quality)]),
            _ => throw new ImageConversionException("Unsupported output format.")
        };
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
