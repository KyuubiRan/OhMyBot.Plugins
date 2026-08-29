namespace OhMyBot.Plugins.ImageConverter;

internal static class VideoStickerCompressionPlanner
{
    public const int InitialCrf = 32;
    public const int MaximumCrf = 63;
    public const int MinimumTargetBitrate = 32_000;
    public const int MaximumTargetBitrate = 8_000_000;

    private const int MinimumCrfStep = 6;
    private const int MaximumCrfStep = 16;
    private const double TargetSizeRatio = 0.97;
    private const double InitialBitrateHeadroom = 0.80;
    private const double RetryBitrateHeadroom = 0.95;

    public static int SelectInitialCrf(int quality)
    {
        var normalizedQuality = int.Clamp(quality, 1, 100) / 100d;
        return InitialCrf + (int)Math.Round(
            (1d - normalizedQuality) * (MaximumCrf - InitialCrf),
            MidpointRounding.AwayFromZero);
    }

    public static int? SelectNextCrf(int currentCrf, long outputBytes, long limitBytes)
    {
        if (outputBytes <= limitBytes || currentCrf >= MaximumCrf)
        {
            return null;
        }

        var targetBytes = limitBytes * TargetSizeRatio;
        var sizeRatio = outputBytes / targetBytes;
        var estimatedStep = (int)Math.Ceiling(Math.Log2(sizeRatio) * 6d);
        var step = int.Clamp(estimatedStep, MinimumCrfStep, MaximumCrfStep);
        return Math.Min(MaximumCrf, currentCrf + step);
    }

    public static int? SelectRefinementCrf(int highestFailedCrf, int successfulCrf)
    {
        if (highestFailedCrf < InitialCrf || successfulCrf - highestFailedCrf < 2)
        {
            return null;
        }

        return highestFailedCrf + ((successfulCrf - highestFailedCrf) / 2);
    }

    public static long SelectTargetOutputBytes(int quality, long limitBytes)
        => Math.Max(
            1,
            (long)Math.Floor(
                limitBytes
                * TargetSizeRatio
                * int.Clamp(quality, 1, 100)
                / 100d));

    public static int SelectInitialBitrate(long targetBytes, double durationSeconds)
    {
        var safeDuration = Math.Max(durationSeconds, 1d / FfmpegMediaConverter.MaximumPreservedFrameRate);
        var bitrate = targetBytes * 8d / safeDuration * InitialBitrateHeadroom;
        return int.Clamp(
            (int)Math.Round(bitrate, MidpointRounding.AwayFromZero),
            MinimumTargetBitrate,
            MaximumTargetBitrate);
    }

    public static int? SelectNextBitrate(int currentBitrate, long outputBytes, long targetBytes)
    {
        if (outputBytes <= targetBytes || currentBitrate <= MinimumTargetBitrate)
        {
            return null;
        }

        var ratio = targetBytes / (double)outputBytes;
        var nextBitrate = (int)Math.Floor(currentBitrate * ratio * RetryBitrateHeadroom);
        nextBitrate = Math.Min(nextBitrate, currentBitrate - 1_000);
        return Math.Max(MinimumTargetBitrate, nextBitrate);
    }
}

internal readonly record struct VideoStickerFrameRate(int Numerator, int Denominator)
{
    public static VideoStickerFrameRate Default => new(FfmpegMediaConverter.DefaultFrameRate, 1);

    public double FramesPerSecond => (double)Numerator / Denominator;

    public string FilterExpression => $"{Numerator}/{Denominator}";

    public VideoStickerFrameRate ClampTo(int maximumFrameRate)
        => FramesPerSecond <= maximumFrameRate
            ? this
            : new VideoStickerFrameRate(maximumFrameRate, 1);

    public static bool TryParse(string value, out VideoStickerFrameRate frameRate)
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var numerator)
            && int.TryParse(parts[1], out var denominator)
            && numerator > 0
            && denominator > 0)
        {
            frameRate = new VideoStickerFrameRate(numerator, denominator);
            return true;
        }

        frameRate = default;
        return false;
    }

    public static VideoStickerFrameRate FromFramesPerSecond(double framesPerSecond)
    {
        var scaled = (int)Math.Round(framesPerSecond * 1000d, MidpointRounding.AwayFromZero);
        var divisor = GreatestCommonDivisor(scaled, 1000);
        return new VideoStickerFrameRate(scaled / divisor, 1000 / divisor);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }
}
