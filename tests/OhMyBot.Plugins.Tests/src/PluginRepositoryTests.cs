using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugins.Kuro;
using OhMyBot.Plugins.Kuro.Data;
using OhMyBot.Plugins.Mihoyo;
using OhMyBot.Plugins.Mihoyo.Data;
using OhMyBot.Plugins.Skland;
using OhMyBot.Plugins.Skland.Data;

namespace OhMyBot.Plugins.Tests;

[TestClass]
public sealed class PluginRepositoryTests
{
    [TestMethod]
    public void PublicPluginsDeclareAllSupportedPlatforms()
    {
        foreach (var pluginType in new[] { typeof(KuroPlugin), typeof(MihoyoPlugin), typeof(SklandPlugin) })
        {
            var metadata = pluginType.GetCustomAttribute<OhMyBotPluginAttribute>();
            Assert.IsNotNull(metadata);
            Assert.AreEqual(PluginSupportedPlatforms.All, metadata.SupportedPlatforms);
        }
    }

    [TestMethod]
    public void PublicPluginPackagesHaveUniqueIdentityAndCleanContents()
    {
        var repository = FindRepositoryRoot();
        foreach (var plugin in new[] { "Kuro", "Mihoyo", "Skland" })
        {
            var packagePath = Path.Combine(
                repository,
                "build",
                $"OhMyBot.Plugins.{plugin}",
                "bin",
                "Debug",
                "net10.0",
                "plugin-package",
                "Plugin.dll");
            Assert.IsTrue(File.Exists(packagePath), $"Build the {plugin} plugin before running this assertion.");
            Assert.AreEqual($"OhMyBot.Plugins.{plugin}", AssemblyName.GetAssemblyName(packagePath).Name);

            var files = Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "Plugin.dll", "Plugin.pdb", "pluginsettings.template.json" },
                files);
        }
    }

    [TestMethod]
    public void PublicPluginsOwnIndependentEfModelsAndMigrationLines()
    {
        using var kuro = new KuroDbContext(new DbContextOptionsBuilder<KuroDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);
        using var mihoyo = new MihoyoDbContext(new DbContextOptionsBuilder<MihoyoDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);
        using var skland = new SklandDbContext(new DbContextOptionsBuilder<SklandDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        AssertTables(kuro, "CoreUsers", "KuroAccounts", "KuroGameRoles");
        AssertTables(mihoyo, "CoreUsers", "MihoyoAccounts", "MihoyoGameRoles");
        AssertTables(skland, "CoreUsers", "SklandAccounts", "SklandGameRoles");

        var migrationIds = new DbContext[] { kuro, mihoyo, skland }
            .Select(context => context.Database.GetMigrations().Single())
            .ToArray();
        Assert.IsTrue(migrationIds.All(id => id.EndsWith("_InitialBaseline", StringComparison.Ordinal)));
        Assert.HasCount(3, migrationIds.Distinct(StringComparer.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OhMyBot.Plugins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Unable to locate the OhMyBot.Plugins repository root.");
    }

    private static void AssertTables(DbContext context, params string[] expected)
    {
        var actual = context.Model.GetRelationalModel().Tables
            .Select(table => table.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
    }
}
