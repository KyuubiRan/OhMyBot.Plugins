using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugins.PlaywrightProvider;
using OhMyBot.Plugins.Skland.Data;

namespace OhMyBot.Plugins.Skland.Tests;

[TestClass]
public sealed class PluginProjectTests
{
    [TestMethod]
    public void DeclaresAllSupportedPlatforms()
    {
        var metadata = typeof(SklandPlugin).GetCustomAttribute<OhMyBotPluginAttribute>();
        Assert.IsNotNull(metadata);
        Assert.AreEqual("1.0.1", metadata.Version);
        Assert.AreEqual(PluginSupportedPlatforms.All, metadata.SupportedPlatforms);
        var dependency = typeof(SklandPlugin).GetCustomAttribute<OhMyBotDependencyAttribute>();
        Assert.IsNotNull(dependency);
        Assert.AreEqual(PlaywrightProviderPlugin.PluginId, dependency.PluginId);
    }

    [TestMethod]
    public void PackageHasExpectedIdentityAndCleanContents()
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(), "build", "OhMyBot.Plugins.Skland", "bin", "Debug", "net10.0",
            "plugin-package", "Plugin.dll");
        Assert.IsTrue(File.Exists(packagePath), "Build the Skland plugin before running this assertion.");
        Assert.AreEqual("OhMyBot.Plugins.Skland", AssemblyName.GetAssemblyName(packagePath).Name);
        CollectionAssert.AreEqual(
            new[] { "Plugin.dll", "Plugin.pdb", "pluginsettings.template.json" },
            Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(packagePath)!, ".playwright")));
    }

    [TestMethod]
    public void OwnsItsEfModelAndMigrationLine()
    {
        using var context = new SklandDbContext(new DbContextOptionsBuilder<SklandDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var actual = context.Model.GetRelationalModel().Tables.Select(table => table.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "CoreUsers", "SklandAccounts", "SklandGameRoles" }, actual);
        CollectionAssert.AreEqual(
            new[] { "InitialBaseline", "ExpandSklandDeviceId" },
            context.Database.GetMigrations()
                .Select(migration => migration[(migration.IndexOf('_') + 1)..])
                .ToArray());
        Assert.AreEqual(
            256,
            context.Model.FindEntityType(typeof(SklandAccount))!
                .FindProperty(nameof(SklandAccount.DeviceId))!
                .GetMaxLength());
    }

    [TestMethod]
    public void LocalDeploymentKeepsRealConfigurationWithoutPlaywrightRuntime()
    {
        var repositoryRoot = FindRepositoryRoot();
        var deployPath = Path.GetFullPath(Path.Combine(
            repositoryRoot, "..", "OhMyBot", "build", "OhMyBot.Core.Host", "bin", "Debug", "net10.0",
            "Plugins", "Skland"));

        Assert.IsTrue(File.Exists(Path.Combine(deployPath, "Plugin.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(deployPath, "pluginsettings.json")));
        Assert.IsFalse(File.Exists(Path.Combine(deployPath, "Microsoft.Playwright.dll")));
        Assert.IsFalse(Directory.Exists(Path.Combine(deployPath, ".playwright")));
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
