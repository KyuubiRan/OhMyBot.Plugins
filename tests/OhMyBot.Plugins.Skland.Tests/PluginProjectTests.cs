using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OhMyBot.Plugin.Abstractions;
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
        Assert.AreEqual(PluginSupportedPlatforms.All, metadata.SupportedPlatforms);
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
    }

    [TestMethod]
    public void OwnsItsEfModelAndMigrationLine()
    {
        using var context = new SklandDbContext(new DbContextOptionsBuilder<SklandDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var actual = context.Model.GetRelationalModel().Tables.Select(table => table.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "CoreUsers", "SklandAccounts", "SklandGameRoles" }, actual);
        Assert.IsTrue(context.Database.GetMigrations().Single().EndsWith("_InitialBaseline", StringComparison.Ordinal));
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
