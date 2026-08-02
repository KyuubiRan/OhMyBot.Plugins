using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugins.Kuro.Data;

namespace OhMyBot.Plugins.Kuro.Tests;

[TestClass]
public sealed class PluginProjectTests
{
    [TestMethod]
    public void DeclaresAllSupportedPlatforms()
    {
        var metadata = typeof(KuroPlugin).GetCustomAttribute<OhMyBotPluginAttribute>();
        Assert.IsNotNull(metadata);
        Assert.AreEqual(PluginSupportedPlatforms.All, metadata.SupportedPlatforms);
    }

    [TestMethod]
    public void PackageHasExpectedIdentityAndCleanContents()
    {
        AssertPackage("Kuro");
    }

    [TestMethod]
    public void OwnsItsEfModelAndMigrationLine()
    {
        using var context = new KuroDbContext(new DbContextOptionsBuilder<KuroDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        AssertTables(context, "CoreUsers", "KuroAccounts", "KuroGameRoles");
        Assert.IsTrue(context.Database.GetMigrations().Single().EndsWith("_InitialBaseline", StringComparison.Ordinal));
    }

    private static void AssertPackage(string pluginName)
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(), "build", $"OhMyBot.Plugins.{pluginName}", "bin", "Debug", "net10.0",
            "plugin-package", "Plugin.dll");
        Assert.IsTrue(File.Exists(packagePath), $"Build the {pluginName} plugin before running this assertion.");
        Assert.AreEqual($"OhMyBot.Plugins.{pluginName}", AssemblyName.GetAssemblyName(packagePath).Name);
        CollectionAssert.AreEqual(
            new[] { "Plugin.dll", "Plugin.pdb", "pluginsettings.template.json" },
            Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
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

    private static void AssertTables(DbContext context, params string[] expected)
    {
        var actual = context.Model.GetRelationalModel().Tables.Select(table => table.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
    }
}
