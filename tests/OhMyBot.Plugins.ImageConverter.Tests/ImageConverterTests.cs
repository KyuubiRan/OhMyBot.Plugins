using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Identity;
using OhMyBot.Plugin.Abstractions;
using OpenCvSharp;

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
        Assert.AreEqual("1.0.0", metadata.Version);
        Assert.AreEqual(PluginSupportedPlatforms.Telegram, metadata.SupportedPlatforms);
    }

    [TestMethod]
    public void ConvertsAllFormatsAndPreservesAspectRatio()
    {
        var converter = new OpenCvImageConverter();
        var input = CreatePng(800, 400);

        var png = converter.Convert(input, new ImageConversionOptions(ImageOutputFormat.Png, 200, 200));
        var jpeg = converter.Convert(input, new ImageConversionOptions(ImageOutputFormat.Jpeg, 200, 200, 75));
        var webp = converter.Convert(input, new ImageConversionOptions(ImageOutputFormat.Webp, 200, 200, 80));
        var sticker = converter.Convert(input, new ImageConversionOptions(ImageOutputFormat.Sticker));

        AssertImage(png, ".png", "image/png", 200, 100, 4);
        AssertImage(jpeg, ".jpg", "image/jpeg", 200, 100, 3);
        AssertImage(webp, ".webp", "image/webp", 200, 100, 4);
        AssertImage(sticker, ".webp", "image/webp", 512, 256, 4);
    }

    [TestMethod]
    public void PackageContainsManagedAndArm64NativeRuntimes()
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
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "OpenCvSharp.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "libOpenCvSharpExtern.dylib")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "libOpenCvSharpExtern.so")));
    }

    [TestMethod]
    public async Task CommandHandlerReturnsConvertedTelegramDocument()
    {
        var provider = new ImageConverterCommandDslProvider(
            new OpenCvImageConverter(),
            new FfmpegStickerConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = new CommandRequest
        {
            Platform = BotPlatform.Telegram,
            ChatType = BotChatType.Private,
            Command = "imgcvt",
            MessageId = "message",
            ReplyMedia = new CommandMedia
            {
                FileName = "source.png",
                ContentType = "image/png",
                Content = Google.Protobuf.ByteString.CopyFrom(CreatePng(100, 50))
            }
        };
        request.Args.Add("webp");
        var context = new CommandContext(
            request,
            new ResolvedIdentity(1, UserPrivilege.User, BotPlatform.Telegram, "user"),
            0,
            CancellationToken.None);

        var response = await node.Handler!(context);

        Assert.AreEqual(CommandResponse.PlatformResponseOneofCase.Telegram, response.PlatformResponseCase);
        Assert.AreEqual("imgcvt_source.webp", response.Telegram.Messages[0].Document.FileName);
        Assert.IsTrue(response.Telegram.Messages[0].Document.Content.Length > 0);
        Assert.IsTrue(node.AcceptsReplyMedia);
        Assert.AreEqual(SupportedPlatforms.Telegram, node.SupportPlatforms);
    }

    [TestMethod]
    public async Task StickerFormatReturnsTelegramStickerInsteadOfDocument()
    {
        var provider = new ImageConverterCommandDslProvider(
            new OpenCvImageConverter(),
            new FfmpegStickerConverter(Options.Create(new ImageConverterOptions())),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = new CommandRequest
        {
            Platform = BotPlatform.Telegram,
            ChatType = BotChatType.Private,
            Command = "imgcvt",
            MessageId = "message",
            ReplyMedia = new CommandMedia
            {
                FileName = "source.png",
                ContentType = "image/png",
                Kind = CommandMediaKind.Photo,
                Content = Google.Protobuf.ByteString.CopyFrom(CreatePng(100, 50))
            }
        };
        request.Args.Add("sticker");
        var context = new CommandContext(
            request,
            new ResolvedIdentity(1, UserPrivilege.User, BotPlatform.Telegram, "user"),
            0,
            CancellationToken.None);

        var response = await node.Handler!(context);
        var message = response.Telegram.Messages.Single();

        Assert.IsNotNull(message.Sticker);
        Assert.IsNull(message.Document);
        Assert.AreEqual("imgcvt_source.webp", message.Sticker.FileName);
    }

    [TestMethod]
    public async Task AnimatedTgsStickerIsRejectedWithoutCallingFfmpeg()
    {
        var provider = new ImageConverterCommandDslProvider(
            new OpenCvImageConverter(),
            new FfmpegStickerConverter(Options.Create(new ImageConverterOptions
            {
                FfmpegPath = "/path/that/does/not/exist"
            })),
            NullLogger<ImageConverterCommandDslProvider>.Instance);
        var node = provider.GetNodes().Single();
        var request = new CommandRequest
        {
            Platform = BotPlatform.Telegram,
            ChatType = BotChatType.Private,
            Command = "imgcvt",
            MessageId = "message",
            ReplyMedia = new CommandMedia
            {
                FileName = "animated.tgs",
                ContentType = "application/x-tgsticker",
                Kind = CommandMediaKind.Sticker,
                Content = Google.Protobuf.ByteString.CopyFromUtf8("not-a-real-tgs")
            }
        };
        request.Args.Add("sticker");
        var context = new CommandContext(
            request,
            new ResolvedIdentity(1, UserPrivilege.User, BotPlatform.Telegram, "user"),
            0,
            CancellationToken.None);

        var response = await node.Handler!(context);

        Assert.AreEqual("AnimatedStickerUnsupported", response.ErrorCode);
        Assert.AreEqual(1, response.Code);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC4, new Scalar(20, 40, 60, 128));
        Cv2.ImEncode(".png", image, out var content);
        return content;
    }

    private static void AssertImage(
        ImageConversionResult result,
        string extension,
        string contentType,
        int width,
        int height,
        int channels)
    {
        Assert.AreEqual(extension.TrimStart('.'), result.Extension);
        Assert.AreEqual(contentType, result.ContentType);
        Assert.AreEqual(width, result.Width);
        Assert.AreEqual(height, result.Height);
        using var decoded = Cv2.ImDecode(result.Content, ImreadModes.Unchanged);
        Assert.IsFalse(decoded.Empty());
        Assert.AreEqual(width, decoded.Width);
        Assert.AreEqual(height, decoded.Height);
        Assert.AreEqual(channels, decoded.Channels());
    }

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
