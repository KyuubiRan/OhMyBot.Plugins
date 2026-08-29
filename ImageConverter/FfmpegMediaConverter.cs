using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts;

namespace OhMyBot.Plugins.ImageConverter;

public sealed class ImageConverterOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record VideoStickerConversionResult(
    byte[] Content,
    string Extension,
    string ContentType,
    int Width,
    int Height);

public sealed class FfmpegUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class FfmpegMediaConverter(
    IOptions<ImageConverterOptions> options,
    ILogger<FfmpegMediaConverter>? logger = null)
{
    public const int MaxVideoStickerBytes = 256 * 1024;
    public const int StickerDimension = 512;
    public const int MaxDurationSeconds = 3;
    public const int MaximumPreservedFrameRate = 30;
    public const int DefaultFrameRate = 30;

    private const int MaximumBitrateAttempts = 5;

    private readonly ImageConverterOptions _options = options.Value;

    public async Task<ImageConversionResult> ConvertImageAsync(
        byte[] input,
        string sourceFileName,
        ImageConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);

        var timeoutSeconds = int.Clamp(_options.TimeoutSeconds, 5, 120);
        var workingDirectory = CreateWorkingDirectory();
        var inputPath = Path.Combine(workingDirectory, GetInputFileName(sourceFileName, input));
        var encoding = ResolveImageEncoding(options);
        var outputPath = Path.Combine(workingDirectory, $"image.{encoding.Extension}");

        try
        {
            await File.WriteAllBytesAsync(inputPath, input, cancellationToken);
            var sourceSize = await ProbeDimensionsAsync(inputPath, timeoutSeconds, cancellationToken);
            var outputSize = ImageConversionLimits.ResolveOutputSize(
                sourceSize.Width,
                sourceSize.Height,
                options);
            await RunImageConversionAsync(
                inputPath,
                outputPath,
                sourceSize,
                outputSize,
                options,
                timeoutSeconds,
                cancellationToken);

            var output = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (output.Length == 0 || output.Length > CommandMediaLimits.MaxContentBytes)
            {
                throw new ImageConversionException("Converted image size is outside the allowed range.");
            }

            if (options.Format == ImageOutputFormat.Sticker
                && output.Length > ImageConversionLimits.MaxStaticStickerBytes)
            {
                throw new FileSizeLimitExceededException(
                    output.Length,
                    ImageConversionLimits.MaxStaticStickerBytes);
            }

            return new ImageConversionResult(
                output,
                encoding.Extension,
                encoding.ContentType,
                outputSize.Width,
                outputSize.Height);
        }
        finally
        {
            TryDeleteWorkingDirectory(workingDirectory);
        }
    }

    public async Task<VideoStickerConversionResult> ConvertVideoStickerAsync(
        byte[] input,
        string sourceFileName,
        VideoStickerConversionOptions? conversionOptions = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        conversionOptions ??= new VideoStickerConversionOptions();

        var timeoutSeconds = int.Clamp(_options.TimeoutSeconds, 5, 120);
        var workingDirectory = CreateWorkingDirectory();
        var inputPath = Path.Combine(workingDirectory, GetInputFileName(sourceFileName, input));
        var outputPath = Path.Combine(workingDirectory, "sticker.webm");

        try
        {
            await File.WriteAllBytesAsync(inputPath, input, cancellationToken);
            var sourceFrameRate = conversionOptions.FrameRate is { } configuredFrameRate
                ? VideoStickerFrameRate.FromFramesPerSecond(configuredFrameRate)
                    .ClampTo(MaximumPreservedFrameRate)
                : await ProbeFrameRateAsync(inputPath, timeoutSeconds, cancellationToken);

            byte[]? output;
            long smallestOutputBytes;
            if (conversionOptions.Background == VideoStickerBackground.Transparent)
            {
                var attempt = await TryConvertVideoStickerAtFrameRateAsync(
                    inputPath,
                    outputPath,
                    sourceFrameRate,
                    VideoStickerCompressionPlanner.SelectInitialCrf(conversionOptions.Quality),
                    timeoutSeconds,
                    cancellationToken);
                output = attempt.Output;
                smallestOutputBytes = attempt.SmallestOutputBytes;
                if (output is not null)
                {
                    logger?.LogInformation(
                        "Selected transparent video sticker frameRate={FrameRate}, CRF={Crf}: {OutputBytes} bytes.",
                        sourceFrameRate.FilterExpression,
                        attempt.Crf,
                        output.Length);
                }
            }
            else
            {
                var durationSeconds = await ProbeDurationAsync(
                    inputPath,
                    sourceFrameRate,
                    timeoutSeconds,
                    cancellationToken);
                var attempt = await TryConvertFlattenedVideoStickerAsync(
                    inputPath,
                    outputPath,
                    workingDirectory,
                    sourceFrameRate,
                    durationSeconds,
                    conversionOptions,
                    timeoutSeconds,
                    cancellationToken);
                output = attempt.Output;
                smallestOutputBytes = attempt.SmallestOutputBytes;
                if (output is not null)
                {
                    logger?.LogInformation(
                        "Selected flattened video sticker background={Background}, frameRate={FrameRate}, bitrate={Bitrate}: {OutputBytes} bytes.",
                        conversionOptions.Background,
                        sourceFrameRate.FilterExpression,
                        attempt.Bitrate,
                        output.Length);
                }
            }

            if (output is not null)
            {
                return new VideoStickerConversionResult(
                    output,
                    "webm",
                    "video/webm",
                    StickerDimension,
                    StickerDimension);
            }

            throw new FileSizeLimitExceededException(smallestOutputBytes, MaxVideoStickerBytes);
        }
        finally
        {
            TryDeleteWorkingDirectory(workingDirectory);
        }
    }

    private async Task<VideoStickerEncodingAttempt> TryConvertVideoStickerAtFrameRateAsync(
        string inputPath,
        string outputPath,
        VideoStickerFrameRate frameRate,
        int initialCrf,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var crf = initialCrf;
        var highestFailedCrf = crf - 1;
        byte[]? acceptedOutput = null;
        var acceptedCrf = 0;
        var smallestOutputBytes = long.MaxValue;
        var isRefining = false;

        while (true)
        {
            await RunVideoStickerConversionAsync(
                inputPath,
                outputPath,
                frameRate,
                crf,
                timeoutSeconds,
                cancellationToken);

            var output = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            smallestOutputBytes = Math.Min(smallestOutputBytes, output.Length);
            logger?.LogInformation(
                "Encoded video sticker at frameRate={FrameRate}, CRF={Crf}: {OutputBytes} bytes (limit {LimitBytes}).",
                frameRate.FilterExpression,
                crf,
                output.Length,
                MaxVideoStickerBytes);
            if (output.Length <= MaxVideoStickerBytes)
            {
                acceptedOutput = output;
                acceptedCrf = crf;
                if (!isRefining
                    && VideoStickerCompressionPlanner.SelectRefinementCrf(highestFailedCrf, crf) is { } refinementCrf)
                {
                    crf = refinementCrf;
                    isRefining = true;
                    continue;
                }

                break;
            }

            highestFailedCrf = Math.Max(highestFailedCrf, crf);
            if (isRefining && acceptedOutput is not null)
            {
                break;
            }

            if (VideoStickerCompressionPlanner.SelectNextCrf(
                    crf,
                    output.Length,
                    MaxVideoStickerBytes) is not { } nextCrf)
            {
                break;
            }

            crf = nextCrf;
        }

        return new VideoStickerEncodingAttempt(acceptedOutput, acceptedCrf, smallestOutputBytes);
    }

    private async Task<VideoStickerBitrateEncodingAttempt> TryConvertFlattenedVideoStickerAsync(
        string inputPath,
        string outputPath,
        string workingDirectory,
        VideoStickerFrameRate frameRate,
        double durationSeconds,
        VideoStickerConversionOptions conversionOptions,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var qualityTargetBytes = VideoStickerCompressionPlanner.SelectTargetOutputBytes(
            conversionOptions.Quality,
            MaxVideoStickerBytes);
        var sizeTargetBytes = VideoStickerCompressionPlanner.SelectTargetOutputBytes(
            100,
            MaxVideoStickerBytes);
        var bitrate = VideoStickerCompressionPlanner.SelectInitialBitrate(qualityTargetBytes, durationSeconds);
        byte[]? acceptedOutput = null;
        var acceptedBitrate = bitrate;
        var smallestOutputBytes = long.MaxValue;

        for (var attemptIndex = 0; attemptIndex < MaximumBitrateAttempts; attemptIndex++)
        {
            var passLogPath = Path.Combine(workingDirectory, $"vpx-pass-{attemptIndex}");
            await RunFlattenedVideoStickerConversionAsync(
                inputPath,
                outputPath,
                passLogPath,
                frameRate,
                conversionOptions.Background,
                bitrate,
                timeoutSeconds,
                cancellationToken);

            var output = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            smallestOutputBytes = Math.Min(smallestOutputBytes, output.Length);
            logger?.LogInformation(
                "Encoded flattened video sticker background={Background}, frameRate={FrameRate}, bitrate={Bitrate}: {OutputBytes} bytes (quality target {QualityTargetBytes}, limit {LimitBytes}).",
                conversionOptions.Background,
                frameRate.FilterExpression,
                bitrate,
                output.Length,
                qualityTargetBytes,
                MaxVideoStickerBytes);

            if (output.Length <= MaxVideoStickerBytes)
            {
                acceptedOutput = output;
                acceptedBitrate = bitrate;
                break;
            }

            if (VideoStickerCompressionPlanner.SelectNextBitrate(
                    bitrate,
                    output.Length,
                    sizeTargetBytes) is not { } nextBitrate)
            {
                break;
            }

            bitrate = nextBitrate;
        }

        return new VideoStickerBitrateEncodingAttempt(
            acceptedOutput,
            acceptedBitrate,
            smallestOutputBytes);
    }

    private async Task<ImageSize> ProbeDimensionsAsync(
        string inputPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(_options.FfprobePath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        AddAlphaPreservingInputDecoder(startInfo, inputPath);
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=width,height");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0:s=x");
        startInfo.ArgumentList.Add(inputPath);

        var result = await RunProcessAsync(startInfo, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateProcessFailure("FFprobe failed to inspect the media", result.StandardError);
        }

        var dimensions = result.StandardOutput.Trim().Split('x', 2);
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            throw new ImageConversionException("FFprobe returned invalid image dimensions.");
        }

        return new ImageSize(width, height);
    }

    private async Task<VideoStickerFrameRate> ProbeFrameRateAsync(
        string inputPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(_options.FfprobePath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        AddAlphaPreservingInputDecoder(startInfo, inputPath);
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=avg_frame_rate");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputPath);

        var result = await RunProcessAsync(startInfo, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateProcessFailure("FFprobe failed to inspect the media frame rate", result.StandardError);
        }

        if (!VideoStickerFrameRate.TryParse(result.StandardOutput.Trim(), out var frameRate))
        {
            logger?.LogWarning(
                "FFprobe returned invalid frame rate {FrameRate}; falling back to {FallbackFrameRate} FPS.",
                result.StandardOutput.Trim(),
                DefaultFrameRate);
            return VideoStickerFrameRate.Default;
        }

        return frameRate.ClampTo(MaximumPreservedFrameRate);
    }

    private async Task<double> ProbeDurationAsync(
        string inputPath,
        VideoStickerFrameRate frameRate,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(_options.FfprobePath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        AddAlphaPreservingInputDecoder(startInfo, inputPath);
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputPath);

        var result = await RunProcessAsync(startInfo, timeoutSeconds, cancellationToken);
        if (result.ExitCode == 0
            && double.TryParse(
                result.StandardOutput.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var durationSeconds)
            && durationSeconds > 0d)
        {
            return Math.Min(durationSeconds, MaxDurationSeconds);
        }

        var fallbackDuration = 1d / frameRate.FramesPerSecond;
        logger?.LogWarning(
            "FFprobe returned invalid duration {Duration}; falling back to one frame ({FallbackDuration} seconds).",
            result.StandardOutput.Trim(),
            fallbackDuration);
        return fallbackDuration;
    }

    private async Task RunImageConversionAsync(
        string inputPath,
        string outputPath,
        ImageSize sourceSize,
        ImageSize outputSize,
        ImageConversionOptions options,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateFfmpegStartInfo(inputPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");

        var downscaling = outputSize.Width < sourceSize.Width || outputSize.Height < sourceSize.Height;
        var interpolation = downscaling ? "area" : "lanczos";
        var pixelFormat = options.Format == ImageOutputFormat.Jpeg ? "yuvj420p" : "rgba";
        startInfo.ArgumentList.Add(
            $"scale={outputSize.Width}:{outputSize.Height}:flags={interpolation},format={pixelFormat}");

        var quality = int.Clamp(options.Quality, 1, 100);
        switch (options.Format)
        {
            case ImageOutputFormat.Png:
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("png");
                startInfo.ArgumentList.Add("-pix_fmt");
                startInfo.ArgumentList.Add("rgba");
                break;
            case ImageOutputFormat.Jpeg:
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("mjpeg");
                startInfo.ArgumentList.Add("-q:v");
                startInfo.ArgumentList.Add(MapJpegQuality(quality).ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("-pix_fmt");
                startInfo.ArgumentList.Add("yuvj420p");
                break;
            case ImageOutputFormat.Webp:
            case ImageOutputFormat.Sticker:
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("libwebp");
                startInfo.ArgumentList.Add("-lossless");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-quality");
                startInfo.ArgumentList.Add(quality.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("-pix_fmt");
                startInfo.ArgumentList.Add("bgra");
                break;
            default:
                throw new ImageConversionException("Unsupported output format.");
        }

        startInfo.ArgumentList.Add(outputPath);
        await RunOutputProcessAsync(startInfo, outputPath, timeoutSeconds, cancellationToken);
    }

    private async Task RunVideoStickerConversionAsync(
        string inputPath,
        string outputPath,
        VideoStickerFrameRate frameRate,
        int crf,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateFfmpegStartInfo(inputPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(MaxDurationSeconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(
            $"format=rgba,scale={StickerDimension}:{StickerDimension}:force_original_aspect_ratio=decrease:flags=lanczos," +
            $"pad={StickerDimension}:{StickerDimension}:(ow-iw)/2:(oh-ih)/2:color=black@0.0," +
            $"fps={frameRate.FilterExpression},setsar=1,format=yuva420p");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx-vp9");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuva420p");
        startInfo.ArgumentList.Add("-auto-alt-ref");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-deadline");
        startInfo.ArgumentList.Add("good");
        startInfo.ArgumentList.Add("-cpu-used");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add("-metadata:s:v:0");
        startInfo.ArgumentList.Add("alpha_mode=1");
        startInfo.ArgumentList.Add(outputPath);

        await RunOutputProcessAsync(startInfo, outputPath, timeoutSeconds, cancellationToken);
    }

    private async Task RunFlattenedVideoStickerConversionAsync(
        string inputPath,
        string outputPath,
        string passLogPath,
        VideoStickerFrameRate frameRate,
        VideoStickerBackground background,
        int bitrate,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await RunFlattenedVideoStickerPassAsync(
            inputPath,
            outputPath,
            passLogPath,
            frameRate,
            background,
            bitrate,
            pass: 1,
            timeoutSeconds,
            cancellationToken);
        await RunFlattenedVideoStickerPassAsync(
            inputPath,
            outputPath,
            passLogPath,
            frameRate,
            background,
            bitrate,
            pass: 2,
            timeoutSeconds,
            cancellationToken);
    }

    private async Task RunFlattenedVideoStickerPassAsync(
        string inputPath,
        string outputPath,
        string passLogPath,
        VideoStickerFrameRate frameRate,
        VideoStickerBackground background,
        int bitrate,
        int pass,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var backgroundColor = background switch
        {
            VideoStickerBackground.Black => "black",
            VideoStickerBackground.White => "white",
            _ => throw new ImageConversionException("A flattened video sticker requires a background color.")
        };
        var startInfo = CreateFfmpegStartInfo(inputPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(MaxDurationSeconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(
            $"[0:v]format=rgba,scale={StickerDimension}:{StickerDimension}:force_original_aspect_ratio=decrease:flags=lanczos," +
            $"pad={StickerDimension}:{StickerDimension}:(ow-iw)/2:(oh-ih)/2:color=black@0.0," +
            $"fps={frameRate.FilterExpression},setsar=1[fg];" +
            $"color=c={backgroundColor}:s={StickerDimension}x{StickerDimension}:r={frameRate.FilterExpression}:d={MaxDurationSeconds}[bg];" +
            "[bg][fg]overlay=shortest=1:format=auto,format=yuv420p[v]");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[v]");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx-vp9");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add(bitrate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-deadline");
        startInfo.ArgumentList.Add("good");
        startInfo.ArgumentList.Add("-cpu-used");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add("-pass");
        startInfo.ArgumentList.Add(pass.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-passlogfile");
        startInfo.ArgumentList.Add(passLogPath);

        if (pass == 1)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("pipe:1");
            var result = await RunProcessAsync(startInfo, timeoutSeconds, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw CreateProcessFailure("FFmpeg VP9 first pass failed", result.StandardError);
            }

            return;
        }

        startInfo.ArgumentList.Add(outputPath);
        await RunOutputProcessAsync(startInfo, outputPath, timeoutSeconds, cancellationToken);
    }

    private ProcessStartInfo CreateFfmpegStartInfo(string inputPath)
    {
        var startInfo = CreateStartInfo(_options.FfmpegPath);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        AddAlphaPreservingInputDecoder(startInfo, inputPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        return startInfo;
    }

    private async Task RunOutputProcessAsync(
        ProcessStartInfo startInfo,
        string outputPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(startInfo, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw CreateProcessFailure("FFmpeg failed to convert the media", result.StandardError);
        }

        if (new FileInfo(outputPath).Length == 0)
        {
            throw new ImageConversionException("FFmpeg produced an empty output file.");
        }
    }

    private async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ImageConversionException($"{startInfo.FileName} could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new FfmpegUnavailableException(
                $"Media executable was not found: {startInfo.FileName}",
                exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ImageConversionException("FFmpeg conversion timed out.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
        => new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

    private static void AddAlphaPreservingInputDecoder(ProcessStartInfo startInfo, string inputPath)
    {
        if (!Path.GetExtension(inputPath).Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx-vp9");
    }

    private static (string Extension, string ContentType) ResolveImageEncoding(ImageConversionOptions options)
        => options.Format switch
        {
            ImageOutputFormat.Png => ("png", "image/png"),
            ImageOutputFormat.Jpeg => ("jpg", "image/jpeg"),
            ImageOutputFormat.Webp or ImageOutputFormat.Sticker => ("webp", "image/webp"),
            _ => throw new ImageConversionException("Unsupported output format.")
        };

    private static int MapJpegQuality(int quality)
        => 31 - (int)Math.Round((quality - 1) * 29d / 99d);

    private static string GetInputFileName(string sourceFileName, ReadOnlySpan<byte> input)
    {
        if (MediaHeaderDetector.Detect(input) is { } detected)
        {
            return $"input.{detected.Extension}";
        }

        var extension = Path.GetExtension(sourceFileName);
        return IsSafeExtension(extension)
                ? $"input{extension.ToLowerInvariant()}"
                : "input.bin";
    }

    private static bool IsSafeExtension(string extension)
    {
        if (extension.Length is < 2 or > 10 || extension[0] != '.')
        {
            return false;
        }

        foreach (var character in extension.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"ohmybot-imgcvt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        return workingDirectory;
    }

    private static void ValidateInput(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || input.Length > CommandMediaLimits.MaxContentBytes)
        {
            throw new ImageConversionException("Input media size is outside the allowed range.");
        }
    }

    private static ImageConversionException CreateProcessFailure(string message, string standardError)
    {
        var details = standardError.Trim();
        if (details.Length > 1000)
        {
            details = details[^1000..];
        }

        return string.IsNullOrWhiteSpace(details)
            ? new ImageConversionException(message + ".")
            : new ImageConversionException($"{message}: {details}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteWorkingDirectory(string workingDirectory)
    {
        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files are best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary files are best effort cleanup only.
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record VideoStickerEncodingAttempt(
        byte[]? Output,
        int Crf,
        long SmallestOutputBytes);

    private sealed record VideoStickerBitrateEncodingAttempt(
        byte[]? Output,
        int Bitrate,
        long SmallestOutputBytes);
}
