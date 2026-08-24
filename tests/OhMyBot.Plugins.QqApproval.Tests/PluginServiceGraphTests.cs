using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyBot.Plugin.Abstractions;
using OhMyBot.Plugin.Commanding;

namespace OhMyBot.Plugins.QqApproval.Tests;

/// <summary>
/// 复刻宿主加载插件时的容器装配与校验（PluginManager 建 PluginBuilder → BuildServiceProvider
/// with ValidateOnBuild）。这条测试存在的原因：容器构造失败发生在加载期，宿主只把原因写进插件
/// status、不打日志，而且一个插件失败会把同批插件全部标成 Faulted——线上表现是所有插件一起消失，
/// 却查不到任何错误。这类问题必须在这里被拦住。
/// </summary>
[TestClass]
public sealed class PluginServiceGraphTests
{
    [TestMethod]
    public void ServiceGraphBuildsUnderHostValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var builder = new TestPluginBuilder(configuration);

        // 与 PluginManager 给插件容器预置的服务保持一致：宿主只给这五样，
        // 其余（含开放泛型 ILogger<>）都得插件自己注册。
        builder.Services.TryAddSingleton<IConfiguration>(configuration);
        builder.Services.TryAddSingleton<IPluginHostServices>(new TestHostServices());
        builder.Services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.TryAddSingleton(TimeProvider.System);

        new QqApprovalPlugin().Configure(builder);

        Assert.AreEqual(
            3,
            builder.Registrations
                .OfType<CommandingComponentRegistration>()
                .Count(registration => registration.ComponentKind == CommandingComponentKind.NotificationSource));

        using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.IsNotNull(provider);
    }

    private sealed class TestPluginBuilder(IConfiguration configuration) : IPluginBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;

        public ICollection<IPluginRegistration> Registrations { get; } = [];
    }

    // 插件对宿主服务的解析都发生在运行时工厂里，构建期校验不会走到，
    // 这里只需要提供 DbContext 取连接串用的 IConfiguration。
    private sealed class TestHostServices : IPluginHostServices
    {
        public IServiceProvider Services { get; } = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>(
                    "ConnectionStrings:Postgres", "Host=localhost;Database=unused")])
                .Build())
            .BuildServiceProvider();

        public object GetExport(string pluginId, Type contractType)
            => throw new NotSupportedException();

        public bool TryGetExport(string pluginId, Type contractType, out object? service)
        {
            service = null;
            return false;
        }
    }
}
