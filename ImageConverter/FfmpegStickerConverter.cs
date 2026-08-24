using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts;

namespace OhMyBot.Plugins.ImageConverter;

public sealed class ImageConverterOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";

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

public sealed class FfmpegStickerConverter(IOptions<ImageConverterOptions> options)
{
    public const int MaxStickerBytes = 256 * 1024;
    public const int StickerDimension = 512;
    public const int MaxDurationSeconds = 3;
    public const int MaxFrameRate = 30;

    private static readonly int[] QualityLevels = [32, 38, 44, 50];

    private readonly ImageConverterOptions _options = options.Value;

    public async Task<VideoStickerConversionResult> ConvertAsync(
        byte[] input,
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || input.Length > CommandMediaLimits.MaxContentBytes)
        {
            throw new ImageConversionException("Input media size is outside the allowed range.");
        }

        var timeoutSeconds = int.Clamp(_options.TimeoutSeconds, 5, 120);
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"ohmybot-imgcvt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var inputPath = Path.Combine(workingDirectory, GetInputFileName(sourceFileName));
        var outputPath = Path.Combine(workingDirectory, "sticker.webm");

        try
        {
            await File.WriteAllBytesAsync(inputPath, input, cancellationToken);
            foreach (var crf in QualityLevels)
            {
                var result = await RunFfmpegAsync(
                    inputPath,
                    outputPath,
                    crf,
                    timeoutSeconds,
                    cancellationToken);
                if (!result)
                {
                    continue;
                }

                var output = await File.ReadAllBytesAsync(outputPath, cancellationToken);
                if (output.Length <= MaxStickerBytes)
                {
                    return new VideoStickerConversionResult(
                        output,
                        "webm",
                        "video/webm",
                        StickerDimension,
                        StickerDimension);
                }
            }

            throw new ImageConversionException(
                $"FFmpeg output exceeds the Telegram video sticker limit of {MaxStickerBytes} bytes.");
        }
        finally
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
    }

    private async Task<bool> RunFfmpegAsync(
        string inputPath,
        string outputPath,
        int crf,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(MaxDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(
            $"scale={StickerDimension}:{StickerDimension}:force_original_aspect_ratio=decrease:flags=lanczos," +
            $"pad={StickerDimension}:{StickerDimension}:(ow-iw)/2:(oh-ih)/2:color=black@0.0," +
            $"fps={MaxFrameRate},setsar=1,format=yuva420p");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx-vp9");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuva420p");
        startInfo.ArgumentList.Add("-auto-alt-ref");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add(crf.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-deadline");
        startInfo.ArgumentList.Add("good");
        startInfo.ArgumentList.Add("-cpu-used");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ImageConversionException("FFmpeg could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new FfmpegUnavailableException(
                $"FFmpeg executable was not found: {_options.FfmpegPath}",
                exception);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ImageConversionException("FFmpeg conversion timed out.");
        }

        _ = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new ImageConversionException(
                "FFmpeg failed to convert the media.");
        }

        return true;
    }

    private static string GetInputFileName(string sourceFileName)
    {
        var extension = Path.GetExtension(sourceFileName);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 10
            ? "input.bin"
            : $"input{extension.ToLowerInvariant()}";
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
}
