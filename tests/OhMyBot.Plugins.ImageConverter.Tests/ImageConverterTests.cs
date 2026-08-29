using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Identity;
using OhMyBot.Plugin.Abstractions;

namespace OhMyBot.Plugins.ImageConverter.Tests;

[TestClass]
public sealed class ImageConverterTests
{
    [TestMethod]
    public void DeclaresStableTelegramOnlyIdentity()
    {
        var metadata = typeof(ImageConverterPlugin).GetCustomAttribute<OhMyBotPluginAttribute>();

        Assert.IsNotNull(metadata);
        Assert.AreEqual(ImageConverterPlugin.PluginId, metadata.Id);
        Assert.AreEqual("1.0.10", metadata.Version);
        Assert.AreEqual("[1.1.0,2.0.0)", metadata.CoreApi);
        Assert.AreEqual(PluginSupportedPlatforms.Telegram, metadata.SupportedPlatforms);
    }

    [TestMethod]
    public void DetectsGifFromContentInsteadOfFileMetadata()
    {
        var detected = MediaHeaderDetector.Detect(CreateTransparentAnimatedGif());

        Assert.IsTrue(detected.HasValue);
        Assert.AreEqual("gif", detected.Value.Extension);
        Assert.AreEqual("image/gif", detected.Value.ContentType);
        Assert.IsTrue(detected.Value.IsAnimated);
    }

    [TestMethod]
    public void VideoStickerCompressionPlannerUsesOutputSizeAndRefinesQuality()
    {
        var slightlyOversized = FfmpegMediaConverter.MaxVideoStickerBytes + 1;
        var substantiallyOversized = FfmpegMediaConverter.MaxVideoStickerBytes * 4L;

        Assert.AreEqual(
            38,
            VideoStickerCompressionPlanner.SelectNextCrf(
                VideoStickerCompressionPlanner.InitialCrf,
                slightlyOversized,
                FfmpegMediaConverter.MaxVideoStickerBytes));
        Assert.IsTrue(
            VideoStickerCompressionPlanner.SelectNextCrf(
                VideoStickerCompressionPlanner.InitialCrf,
                substantiallyOversized,
                FfmpegMediaConverter.MaxVideoStickerBytes) > 38);
        Assert.AreEqual(53, VideoStickerCompressionPlanner.SelectRefinementCrf(50, 56));
        Assert.AreEqual(32, VideoStickerCompressionPlanner.SelectInitialCrf(100));
        Assert.AreEqual(48, VideoStickerCompressionPlanner.SelectInitialCrf(50));
        Assert.AreEqual(63, VideoStickerCompressionPlanner.SelectInitialCrf(1));
        Assert.IsNull(
            VideoStickerCompressionPlanner.SelectNextCrf(
                VideoStickerCompressionPlanner.MaximumCrf,
                substantiallyOversized,
                FfmpegMediaConverter.MaxVideoStickerBytes));
        Assert.AreEqual(
            254_279,
            VideoStickerCompressionPlanner.SelectTargetOutputBytes(
                100,
                FfmpegMediaConverter.MaxVideoStickerBytes));
        Assert.AreEqual(
            127_139,
            VideoStickerCompressionPlanner.SelectTargetOutputBytes(
                50,
                FfmpegMediaConverter.MaxVideoStickerBytes));
        Assert.AreEqual(
            753_419,
            VideoStickerCompressionPlanner.SelectInitialBitrate(254_279, 2.16d));
        Assert.AreEqual(
            554_166,
            VideoStickerCompressionPlanner.SelectNextBitrate(700_000, 300_000, 250_000));

        Assert.IsTrue(VideoStickerFrameRate.TryParse("60/1", out var sixtyFps));
        Assert.AreEqual("30/1", sixtyFps.ClampTo(FfmpegMediaConverter.MaximumPreservedFrameRate).FilterExpression);
        Assert.IsTrue(VideoStickerFrameRate.TryParse("24000/1001", out var filmFrameRate));
        Assert.AreEqual(
            "24000/1001",
            filmFrameRate.ClampTo(FfmpegMediaConverter.MaximumPreservedFrameRate).FilterExpression);
        Assert.IsTrue(VideoStickerFrameRate.TryParse("120/1", out var excessiveFrameRate));
        Assert.AreEqual(
            "30/1",
            excessiveFrameRate.ClampTo(FfmpegMediaConverter.MaximumPreservedFrameRate).FilterExpression);
    }

    [TestMethod]
    public async Task ConvertsHeaderDetectedGifToPngAndJpegWithExpectedDimensions()
    {
        var converter = CreateFfmpegConverterOrSkip();
        var input = CreateTransparentAnimatedGif();

        var png = await converter.ConvertImageAsync(
            input,
            "wrong-name.jpg",
            new ImageConversionOptions(ImageOutputFormat.Png, 20, 20));
        var jpeg = await converter.ConvertImageAsync(
            input,
            "wrong-name.png",
            new ImageConversionOptions(ImageOutputFormat.Jpeg, 20, 20, 75));

        await AssertImageAsync(png, "png", "image/png", 20, 15);
        await AssertImageAsync(jpeg, "jpg", "image/jpeg", 20, 15);
        await AssertHasTransparentAndOpaquePixelsAsync(png.Content, png.Extension);
    }

    [TestMethod]
    public async Task ConvertsMatroskaWithNonVp9CodecWithoutForcingWebmDecoder()
    {
        var converter = CreateFfmpegConverterOrSkip();
        var input = CreateMatroskaVideoFixture();
        var detected = MediaHeaderDetector.Detect(input);

        Assert.IsTrue(detected.HasValue);
        Assert.AreEqual("mkv", detected.Value.Extension);
        Assert.AreEqual("video/x-matroska", detected.Value.ContentType);

        var png = await converter.ConvertImageAsync(
            input,
            "misleading.webm",
            new ImageConversionOptions(ImageOutputFormat.Png, 32, 24));

        await AssertImageAsync(png, "png", "image/png", 32, 24);
    }

    [TestMethod]
    public async Task PreservesGifTransparencyInWebpOutput()
    {
        var converter = CreateFfmpegConverterOrSkip();
        RequireFfmpegEncoderOrSkip("libwebp");

        var webp = await converter.ConvertImageAsync(
            CreateTransparentAnimatedGif(),
            "transparent.gif",
            new ImageConversionOptions(ImageOutputFormat.Webp));

        await AssertHasTransparentAndOpaquePixelsAsync(webp.Content, webp.Extension);
    }

    [TestMethod]
    public async Task PreservesGifTransparencyInVideoSticker()
    {
        var converter = CreateFfmpegConverterOrSkip();
        RequireFfmpegEncoderOrSkip("libvpx-vp9");

        var sticker = await converter.ConvertVideoStickerAsync(
            CreateTransparentAnimatedGif(),
            "transparent.jpg");

        Assert.AreEqual("webm", sticker.Extension);
        Assert.IsTrue(sticker.Content.Length <= FfmpegMediaConverter.MaxVideoStickerBytes);
        var detected = MediaHeaderDetector.Detect(sticker.Content);
        Assert.IsTrue(detected.HasValue);
        Assert.AreEqual("webm", detected.Value.Extension);
        await AssertVideoFrameRateAsync(sticker.Content, sticker.Extension, "2/1");
        await AssertHasTransparentAndOpaquePixelsAsync(sticker.Content, sticker.Extension);

        var preview = await converter.ConvertImageAsync(
            sticker.Content,
            "misleading.mkv",
            new ImageConversionOptions(ImageOutputFormat.Png));
        await AssertHasTransparentAndOpaquePixelsAsync(preview.Content, preview.Extension);
    }

    [TestMethod]
    public async Task CapsSixtyFpsVideoStickerAtThirtyFps()
    {
        var converter = CreateFfmpegConverterOrSkip();
        RequireFfmpegEncoderOrSkip("libvpx-vp9");

        var sticker = await converter.ConvertVideoStickerAsync(
            CreateVideoFixture(60),
            "sixty-fps.mp4");

        Assert.IsTrue(sticker.Content.Length <= FfmpegMediaConverter.MaxVideoStickerBytes);
        await AssertVideoFrameRateAsync(sticker.Content, sticker.Extension, "30/1");
    }

    [TestMethod]
    public async Task WebmQualityPercentageFurtherCompressesWithoutChangingFrameRate()
    {
        var converter = CreateFfmpegConverterOrSkip();
        RequireFfmpegEncoderOrSkip("libvpx-vp9");
        var input = CreateVideoFixture(60);

        var baseline = await converter.ConvertVideoStickerAsync(
            input,
            "source.mp4",
            new VideoStickerConversionOptions(
                Quality: 100,
                FrameRate: 30d,
                Background: VideoStickerBackground.Black));
        var compressed = await converter.ConvertVideoStickerAsync(
            input,
            "source.mp4",
            new VideoStickerConversionOptions(
                Quality: 50,
                FrameRate: 30d,
                Background: VideoStickerBackground.Black));

        Assert.IsTrue(compressed.Content.Length < baseline.Content.Length);
        await AssertVideoFrameRateAsync(compressed.Content, compressed.Extension, "30/1");
    }

    [TestMethod]
    public async Task FlattensTransparentGifOntoSelectedBackgroundWithoutChangingFrameRate()
    {
        var converter = CreateFfmpegConverterOrSkip();
        RequireFfmpegEncoderOrSkip("libvpx-vp9");
        var input = CreateTransparentAnimatedGif();

        var black = await converter.ConvertVideoStickerAsync(
            input,
            "transparent.gif",
            new VideoStickerConversionOptions(Background: VideoStickerBackground.Black));
        var white = await converter.ConvertVideoStickerAsync(
            input,
            "transparent.gif",
            new VideoStickerConversionOptions(Background: VideoStickerBackground.White));

        Assert.IsTrue(black.Content.Length <= FfmpegMediaConverter.MaxVideoStickerBytes);
        Assert.IsTrue(white.Content.Length <= FfmpegMediaConverter.MaxVideoStickerBytes);
        await AssertVideoFrameRateAsync(black.Content, black.Extension, "2/1");
        await AssertVideoFrameRateAsync(white.Content, white.Extension, "2/1");
        await AssertOpaqueBackgroundAsync(black.Content, black.Extension, 0);
        await AssertOpaqueBackgroundAsync(white.Content, white.Extension, 255);
    }

    [TestMethod]
    public async Task StickerWebpTakesFirstFrameFromAnimatedInput()
    {
        RequireFfmpegEncoderOrSkip("libwebp");
        var provider = CreateProvider();
        var request = CreateRequest(
            "animated.gif",
            "image/gif",
            CommandMediaKind.Animation,
            CreateTransparentAnimatedGif(),
            "sticker",
            "webp");

        var response = await provider.GetNodes().Single().Handler!(CreateContext(request));
        var sticker = response.Telegram.Messages.Single().Sticker;

        Assert.AreEqual("imgcvt_animated.webp", sticker.FileName);
        await AssertHasTransparentAndOpaquePixelsAsync(sticker.Content.ToByteArray(), "webp");
    }

    [TestMethod]
    public async Task StickerWebmConvertsStaticInputWithRequestedFrameRate()
    {
        var provider = CreateProvider();
        RequireFfmpegEncoderOrSkip("libvpx-vp9");
        var request = CreateRequest(
            "still.png",
            "image/png",
            CommandMediaKind.Document,
            CreateStaticPng(),
            "sticker",
            "type=webm",
            "q=50",
            "fps=30",
            "bg=black");

        var response = await provider.GetNodes().Single().Handler!(CreateContext(request));
        var sticker = response.Telegram.Messages.Single().Sticker;

        Assert.AreEqual("imgcvt_still.webm", sticker.FileName);
        await AssertVideoFrameRateAsync(sticker.Content.ToByteArray(), "webm", "30/1");
    }

    [TestMethod]
    public async Task FpsIsRejectedForStickerWebp()
    {
        var provider = CreateProvider();
        var request = CreateRequest(
            "animated.gif",
            "image/gif",
            CommandMediaKind.Animation,
            CreateTransparentAnimatedGif(),
            "sticker",
            "webp",
            "fps=60");

        var response = await provider.GetNodes().Single().Handler!(CreateContext(request));

        Assert.AreEqual("ImageOptionsInvalid", response.ErrorCode);
        StringAssert.Contains(response.Telegram.Messages.Single().Text, "fps 只在转换为 sticker webm 时有效");
    }

    [TestMethod]
    public async Task StickerWebmRejectsFrameRateAboveThirty()
    {
        var provider = new ImageConverterCommandDslProvider(
            new FfmpegMediaConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var request = CreateRequest(
            "animated.gif",
            "image/gif",
            CommandMediaKind.Animation,
            [0x47, 0x49, 0x46],
            "sticker",
            "webm",
            "fps=60");

        var response = await provider.GetNodes().Single().Handler!(CreateContext(request));

        Assert.AreEqual("ImageOptionsInvalid", response.ErrorCode);
        StringAssert.Contains(response.Telegram.Messages.Single().Text, "fps 必须在 1 到 30 之间");
    }

    [TestMethod]
    public async Task BackgroundIsOnlyAcceptedForStickerWebm()
    {
        var provider = new ImageConverterCommandDslProvider(
            new FfmpegMediaConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var webpRequest = CreateRequest(
            "animated.gif",
            "image/gif",
            CommandMediaKind.Animation,
            [0x47, 0x49, 0x46],
            "sticker",
            "webp",
            "bg=black");
        var invalidColorRequest = CreateRequest(
            "animated.gif",
            "image/gif",
            CommandMediaKind.Animation,
            [0x47, 0x49, 0x46],
            "sticker",
            "webm",
            "bg=red");

        var webpResponse = await provider.GetNodes().Single().Handler!(CreateContext(webpRequest));
        var invalidColorResponse = await provider.GetNodes().Single().Handler!(CreateContext(invalidColorRequest));

        Assert.AreEqual("ImageOptionsInvalid", webpResponse.ErrorCode);
        StringAssert.Contains(webpResponse.Telegram.Messages.Single().Text, "bg 只在转换为 sticker webm 时有效");
        Assert.AreEqual("ImageOptionsInvalid", invalidColorResponse.ErrorCode);
        StringAssert.Contains(invalidColorResponse.Telegram.Messages.Single().Text, "bg 只支持 black 或 white");
    }

    [TestMethod]
    public async Task CommandHelpExplainsFormatsParametersLimitsAndExamples()
    {
        var provider = new ImageConverterCommandDslProvider(
            new FfmpegMediaConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "source.png",
            "image/png",
            CommandMediaKind.Photo,
            [0x89, 0x50, 0x4E, 0x47]);

        var response = await node.Handler!(CreateContext(request));
        var help = MarkdownV2.ToPlain(response.Telegram.Messages.Single().Text);

        StringAssert.Contains(help, "普通图片");
        StringAssert.Contains(help, "Telegram 贴纸");
        StringAssert.Contains(help, "100 为基准");
        StringAssert.Contains(help, "最高 30 FPS");
        StringAssert.Contains(help, "最大 256KB");
        StringAssert.Contains(help, "bg=black|white");
        StringAssert.Contains(help, "/imgcvt sticker webm q=100 fps=30 bg=white");
        StringAssert.Contains(node.Usage, "不指定格式");
    }

    [TestMethod]
    public async Task MissingReplyMediaExplainsHowToStart()
    {
        var provider = new ImageConverterCommandDslProvider(
            new FfmpegMediaConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "source.png",
            "image/png",
            CommandMediaKind.Photo,
            [0x89, 0x50, 0x4E, 0x47],
            "webp");
        request.ReplyMedia = null;

        var response = await node.Handler!(CreateContext(request));
        var message = MarkdownV2.ToPlain(response.Telegram.Messages.Single().Text);

        StringAssert.Contains(message, "没有检测到回复的媒体");
        StringAssert.Contains(message, "/imgcvt webp q=85");
        StringAssert.Contains(message, "发送 /imgcvt 查看完整参数和示例");
    }

    [TestMethod]
    public async Task CommandHandlerUsesGifHeaderWhenDocumentIsNamedJpeg()
    {
        var provider = CreateProvider();
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "transparent.jpg",
            "image/jpeg",
            CommandMediaKind.Document,
            CreateTransparentAnimatedGif(),
            "png");

        var response = await node.Handler!(CreateContext(request));

        var document = response.Telegram.Messages.Single().Document;
        Assert.AreEqual("imgcvt_transparent.png", document.FileName);
        await AssertHasTransparentAndOpaquePixelsAsync(document.Content.ToByteArray(), "png");
    }

    [TestMethod]
    public void PackageContainsOnlyPluginManagedArtifacts()
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(),
            "build",
            "OhMyBot.Plugins.ImageConverter",
            "bin",
            "Debug",
            "net10.0",
            "plugin-package");

        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "Plugin.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "pluginsettings.template.json")));
        Assert.IsFalse(File.Exists(Path.Combine(packagePath, "OpenCvSharp.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(packagePath, "libOpenCvSharpExtern.dylib")));
        Assert.IsFalse(File.Exists(Path.Combine(packagePath, "libOpenCvSharpExtern.so")));
    }

    [TestMethod]
    public async Task CommandHandlerReturnsConvertedTelegramDocument()
    {
        var provider = CreateProvider();
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "source.gif",
            "image/gif",
            CommandMediaKind.Document,
            CreateTransparentAnimatedGif(),
            "png");

        var response = await node.Handler!(CreateContext(request));

        Assert.AreEqual(CommandResponse.PlatformResponseOneofCase.Telegram, response.PlatformResponseCase);
        Assert.AreEqual("imgcvt_source.png", response.Telegram.Messages[0].Document.FileName);
        Assert.IsTrue(response.Telegram.Messages[0].Document.Content.Length > 0);
        Assert.IsTrue(node.AcceptsReplyMedia);
        Assert.AreEqual(CommandProgressStyle.MediaConversion, node.ProgressStyle);
        Assert.AreEqual(SupportedPlatforms.Telegram, node.SupportPlatforms);
    }

    [TestMethod]
    public async Task StickerFormatReturnsTelegramStickerInsteadOfDocument()
    {
        RequireFfmpegEncoderOrSkip("libvpx-vp9");
        var provider = CreateProvider();
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "source.gif",
            "image/gif",
            CommandMediaKind.Document,
            CreateTransparentAnimatedGif(),
            "sticker");

        var response = await node.Handler!(CreateContext(request));
        var message = response.Telegram.Messages.Single();

        Assert.IsNotNull(message.Sticker);
        Assert.IsNull(message.Document);
        Assert.AreEqual("imgcvt_source.webm", message.Sticker.FileName);
    }

    [TestMethod]
    public async Task AnimatedTgsStickerIsRejectedWithoutCallingFfmpeg()
    {
        var provider = new ImageConverterCommandDslProvider(
            new FfmpegMediaConverter(Options.Create(new ImageConverterOptions
            {
                FfmpegPath = "/path/that/does/not/exist",
                FfprobePath = "/path/that/does/not/exist"
            })),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = CreateRequest(
            "animated.tgs",
            "application/x-tgsticker",
            CommandMediaKind.Sticker,
            "not-a-real-tgs"u8.ToArray(),
            "sticker");

        var response = await node.Handler!(CreateContext(request));

        Assert.AreEqual("AnimatedStickerUnsupported", response.ErrorCode);
        Assert.AreEqual(1, response.Code);
    }

    [TestMethod]
    public void OversizedStickerUsesSpecificFileLimitResponse()
    {
        var response = ImageConverterCommandDslProvider.CreateFileSizeLimitResponse(
            CreateContext(CreateRequest(
                "large.gif",
                "image/gif",
                CommandMediaKind.Animation,
                [0x47, 0x49, 0x46],
                "sticker")),
            FfmpegMediaConverter.MaxVideoStickerBytes,
            "6fc402");

        Assert.AreEqual("FileExceedTheLimit", response.ErrorCode);
        Assert.AreEqual(
            "错误：文件超出大小(256KB)（错误 id: 6fc402）（FileExceedTheLimit）",
            response.Telegram.Messages.Single().Text);
    }

    private static ImageConverterCommandDslProvider CreateProvider()
        => new(
            CreateFfmpegConverterOrSkip(),
            NullLogger<ImageConverterCommandDslProvider>.Instance);

    private static CommandRequest CreateRequest(
        string fileName,
        string contentType,
        CommandMediaKind kind,
        byte[] content,
        params string[] commandArguments)
    {
        var request = new CommandRequest
        {
            Platform = BotPlatform.Telegram,
            ChatType = BotChatType.Private,
            Command = "imgcvt",
            MessageId = "message",
            ReplyMedia = new CommandMedia
            {
                FileName = fileName,
                ContentType = contentType,
                Kind = kind,
                Content = Google.Protobuf.ByteString.CopyFrom(content)
            }
        };
        request.Args.AddRange(commandArguments);
        return request;
    }

    private static CommandContext CreateContext(CommandRequest request)
        => new(
            request,
            new ResolvedIdentity(1, UserPrivilege.User, BotPlatform.Telegram, "user"),
            0,
            CancellationToken.None);

    private static byte[] CreateTransparentAnimatedGif()
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required to generate the transparent GIF fixture.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-fixture-{Guid.NewGuid():N}.gif");
        try
        {
            var startInfo = CreateProcessStartInfo(ffmpeg!);
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=black@0.0:s=32x24:r=2:d=1,format=rgba",
                "-f", "lavfi", "-i", "color=c=red:s=12x12:r=2:d=1,format=rgba",
                "-filter_complex",
                "[0:v][1:v]overlay=4:4:format=auto,split[a][b];" +
                "[a]palettegen=reserve_transparent=1:transparency_color=000000[p];" +
                "[b][p]paletteuse=alpha_threshold=128",
                "-plays", "0", outputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static byte[] CreateVideoFixture(int frameRate)
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required to generate the video fixture.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-fixture-{Guid.NewGuid():N}.mp4");
        try
        {
            var startInfo = CreateProcessStartInfo(ffmpeg!);
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=64x48:rate={frameRate}:duration=1",
                "-an", "-c:v", "mpeg4", "-q:v", "5", "-pix_fmt", "yuv420p",
                outputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static byte[] CreateMatroskaVideoFixture()
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required to generate the Matroska fixture.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-fixture-{Guid.NewGuid():N}.mkv");
        try
        {
            var startInfo = CreateProcessStartInfo(ffmpeg!);
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=32x24:rate=2:duration=1",
                "-an", "-c:v", "mpeg4", "-q:v", "5", "-pix_fmt", "yuv420p",
                outputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static byte[] CreateStaticPng()
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required to generate the PNG fixture.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-fixture-{Guid.NewGuid():N}.png");
        try
        {
            var startInfo = CreateProcessStartInfo(ffmpeg!);
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=red@0.5:s=64x48,format=rgba",
                "-frames:v", "1", outputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static FfmpegMediaConverter CreateFfmpegConverterOrSkip()
    {
        var ffmpeg = FindExecutable("ffmpeg");
        var ffprobe = FindExecutable("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            Assert.Inconclusive("FFmpeg and FFprobe are required for image conversion tests.");
        }

        return new FfmpegMediaConverter(Options.Create(new ImageConverterOptions
        {
            FfmpegPath = ffmpeg!,
            FfprobePath = ffprobe!
        }));
    }

    private static void RequireFfmpegEncoderOrSkip(string encoder)
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required for image conversion tests.");
            return;
        }

        var startInfo = CreateProcessStartInfo(ffmpeg);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!output.Contains(encoder, StringComparison.Ordinal))
        {
            Assert.Inconclusive($"The local FFmpeg build does not include the {encoder} encoder.");
        }
    }

    private static string? FindExecutable(string name)
    {
        var executableName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, executableName))
            .FirstOrDefault(File.Exists);
    }

    private static async Task AssertImageAsync(
        ImageConversionResult result,
        string extension,
        string contentType,
        int width,
        int height)
    {
        Assert.AreEqual(extension, result.Extension);
        Assert.AreEqual(contentType, result.ContentType);
        Assert.AreEqual(width, result.Width);
        Assert.AreEqual(height, result.Height);

        var pixels = await DecodeFirstFrameToRgbaAsync(result.Content, result.Extension);
        Assert.AreEqual(width * height * 4, pixels.Length);
    }

    private static async Task AssertHasTransparentAndOpaquePixelsAsync(byte[] content, string extension)
    {
        var pixels = await DecodeFirstFrameToRgbaAsync(content, extension);
        var hasTransparentPixel = false;
        var hasOpaquePixel = false;
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            hasTransparentPixel |= pixels[offset] == 0;
            hasOpaquePixel |= pixels[offset] == 255;
        }

        Assert.IsTrue(hasTransparentPixel, "The decoded output contains no fully transparent pixels.");
        Assert.IsTrue(hasOpaquePixel, "The decoded output contains no fully opaque pixels.");
    }

    private static async Task AssertOpaqueBackgroundAsync(
        byte[] content,
        string extension,
        byte expectedBackground)
    {
        var pixels = await DecodeFirstFrameToRgbaAsync(content, extension);
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            Assert.AreEqual(255, pixels[offset], "The flattened output still contains transparency.");
        }

        for (var channel = 0; channel < 3; channel++)
        {
            Assert.IsTrue(
                Math.Abs(pixels[channel] - expectedBackground) <= 10,
                $"The corner pixel does not match the selected background: channel={channel}, value={pixels[channel]}.");
        }
    }

    private static async Task AssertVideoFrameRateAsync(
        byte[] content,
        string extension,
        string expectedFrameRate)
    {
        var ffprobe = FindExecutable("ffprobe");
        if (ffprobe is null)
        {
            Assert.Inconclusive("FFprobe is required to inspect the converted frame rate.");
        }

        var inputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-rate-{Guid.NewGuid():N}.{extension.TrimStart('.')}");
        try
        {
            await File.WriteAllBytesAsync(inputPath, content);
            var startInfo = CreateProcessStartInfo(ffprobe!);
            foreach (var argument in new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=avg_frame_rate",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFprobe.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, error);
            Assert.AreEqual(expectedFrameRate, output.Trim());
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static async Task<byte[]> DecodeFirstFrameToRgbaAsync(byte[] content, string extension)
    {
        var ffmpeg = FindExecutable("ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Inconclusive("FFmpeg is required to inspect converted image pixels.");
        }

        var inputPath = Path.Combine(
            Path.GetTempPath(),
            $"ohmybot-imgcvt-test-{Guid.NewGuid():N}.{extension.TrimStart('.')}");
        try
        {
            await File.WriteAllBytesAsync(inputPath, content);
            var startInfo = CreateProcessStartInfo(ffmpeg!);
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (extension.Equals("webm", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("libvpx-vp9");
            }

            foreach (var argument in new[]
            {
                "-i", inputPath, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start FFmpeg.");
            using var output = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(copyTask, process.WaitForExitAsync());
            var error = await errorTask;
            Assert.AreEqual(0, process.ExitCode, error);
            return output.ToArray();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executable)
        => new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OhMyBot.Plugins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Unable to locate the plugin repository root.");
    }
}
