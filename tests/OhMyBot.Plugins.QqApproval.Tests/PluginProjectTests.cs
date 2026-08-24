using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugins.QqApproval.Data;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval.Tests;

[TestClass]
public sealed class PluginProjectTests
{
    // QQ 专用插件：声明成 All 会让 Telegram 也路由到这里，而审批动作只有 QQ 网关能执行。
    [TestMethod]
    public void DeclaresQqOnlyPlatform()
    {
        var metadata = typeof(QqApprovalPlugin).GetCustomAttribute<OhMyBotPluginAttribute>();
        Assert.IsNotNull(metadata);
        Assert.AreEqual(PluginSupportedPlatforms.QQ, metadata.SupportedPlatforms);
    }

    [TestMethod]
    public void PackageHasExpectedIdentityAndCleanContents()
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(), "build", "OhMyBot.Plugins.QqApproval", "bin", "Debug", "net10.0",
            "plugin-package", "Plugin.dll");
        Assert.IsTrue(File.Exists(packagePath), "Build the QqApproval plugin before running this assertion.");
        Assert.AreEqual("OhMyBot.Plugins.QqApproval", AssemblyName.GetAssemblyName(packagePath).Name);
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
        using var context = new QqApprovalDbContext(new DbContextOptionsBuilder<QqApprovalDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var actual = context.Model.GetRelationalModel().Tables.Select(table => table.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[] { "QqApprovalListenerSettings", "QqApprovalRequests", "QqApprovalRules" },
            actual);
        Assert.IsTrue(context.Database.GetMigrations().Single().EndsWith("_InitialBaseline", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NotificationSourcesShareLastBotMessageCategoryAndHaveStableOrder()
    {
        var options = Options.Create(new QqApprovalOptions());
        QqApprovalNotificationSource[] sources =
        [
            new QqFriendAddNotificationSource(null!, null!, options),
            new QqGroupInviteNotificationSource(null!, null!, options),
            new QqGroupAddNotificationSource(null!, null!, options)
        ];

        CollectionAssert.AreEqual(new[] { 100, 200, 300 }, sources.Select(source => source.Order).ToArray());
        Assert.IsTrue(sources.All(source => source.Category.Id == "bot-messages"));
        Assert.IsTrue(sources.All(source => source.Category.DisplayName == "Bot消息通知"));
        Assert.IsTrue(sources.All(source => source.Category.Order == int.MaxValue));
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
