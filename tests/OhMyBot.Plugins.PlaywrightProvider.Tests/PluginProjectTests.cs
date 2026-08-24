using System.Reflection;
using OhMyBot.Plugin.Abstractions;

namespace OhMyBot.Plugins.PlaywrightProvider.Tests;

[TestClass]
public sealed class PluginProjectTests
{
    [TestMethod]
    public void DeclaresStableIdentityAndAllSupportedPlatforms()
    {
        var metadata = typeof(PlaywrightProviderPlugin).GetCustomAttribute<OhMyBotPluginAttribute>();
        Assert.IsNotNull(metadata);
        Assert.AreEqual(PlaywrightProviderPlugin.PluginId, metadata.Id);
        Assert.AreEqual("1.0.0", metadata.Version);
        Assert.AreEqual(PluginSupportedPlatforms.All, metadata.SupportedPlatforms);
    }

    [TestMethod]
    public void PackageOwnsPlaywrightRuntimeWithoutBundledBrowser()
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(), "build", "OhMyBot.Plugins.PlaywrightProvider", "bin", "Debug", "net10.0",
            "plugin-package");
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "Plugin.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "Microsoft.Playwright.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, "Microsoft.Bcl.AsyncInterfaces.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, ".playwright", "node", "darwin-arm64", "node")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, ".playwright", "node", "linux-arm64", "node")));
        Assert.IsTrue(File.Exists(Path.Combine(packagePath, ".playwright", "package", "cli.js")));
        Assert.IsFalse(Directory.Exists(Path.Combine(packagePath, ".playwright", "package", ".local-browsers")));
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
