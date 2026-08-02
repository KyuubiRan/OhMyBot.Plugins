using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Commanding.Qq;
using OhMyBot.Core.Infrastructure.Data;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Identity;
using OhMyBot.Core.Integrations.Skland;

namespace OhMyBot.Plugins.Skland.Tests;

/// <summary>
/// 森空岛 autosign 面板。这个插件此前没有任何 UI 测试，而分页、两列布局和
/// 「开启/关闭全部」都是新加的——分页 off-by-one 和 toggle-all 语义是典型易错点。
/// </summary>
[TestClass]
public class V2SklandTests
{
    [TestMethod]
    public async Task SklandAutoSignPanelOmitsPageSuffixForSinglePage()
    {
        // 四个插件的面板要在「账号不多」这个常见情形下逐字一致：单页不带页码后缀、无翻页按钮。
        var builder = CreateBuilder();
        var accounts = CreateAccounts(2);

        var response = await builder.BuildAutoSignPanelAsync(CreateContext(), accounts);

        // 面板正文是 MarkdownV2 转义过的，还原成用户实际看到的样子再断言。
        var plain = MarkdownV2.ToPlain(response.TgText());
        Assert.Contains("[森空岛-自动签到]", plain);
        Assert.Contains("点击账号进入设置：", plain);
        Assert.Contains("[开] #1 Skland1", plain);
        Assert.Contains("[关] #2 Skland2", plain);
        Assert.DoesNotContain("第 ", plain);
        Assert.IsFalse(response.TgButtonTexts().Any(text => text is "上一页" or "下一页"));
    }

    [TestMethod]
    public async Task SklandAutoSignPanelPagesAccountsInTwoColumns()
    {
        var builder = CreateBuilder();
        var accounts = CreateAccounts(9);

        var response = await builder.BuildAutoSignPanelAsync(CreateContext(), accounts);

        Assert.Contains("第 1/2 页", MarkdownV2.ToPlain(response.TgText()));
        Assert.IsTrue(response.TgButtonTexts().Any(text => text == "下一页"));
        // 第 9 个账号属于第二页，不该出现在首页的按钮里。
        Assert.IsFalse(response.TgButtonTexts().Any(text => text.Contains("Skland9", StringComparison.Ordinal)));
        // 账号按钮两列排布，与其余三个插件一致。
        Assert.HasCount(2, response.TgButtonRows()[0].Buttons);
    }

    [TestMethod]
    public async Task SklandAutoSignPanelAlwaysOffersToggleAll()
    {
        // 之前四个插件里只有 AiRouter 有这个按钮，本次统一补齐。
        var builder = CreateBuilder();

        var single = await builder.BuildAutoSignPanelAsync(CreateContext(), CreateAccounts(1));
        var paged = await builder.BuildAutoSignPanelAsync(CreateContext(), CreateAccounts(9));

        Assert.IsTrue(single.TgButtonTexts().Any(text => text == "开启/关闭全部"));
        Assert.IsTrue(paged.TgButtonTexts().Any(text => text == "开启/关闭全部"));
    }

    [TestMethod]
    public async Task SklandAutoSignPanelOmitsToggleAllWhenNoAccounts()
    {
        // 没有账号时挂一个「开启/关闭全部」按钮毫无意义，正文也应是未绑定提示。
        var response = await CreateBuilder().BuildAutoSignPanelAsync(CreateContext(), []);

        Assert.Contains("尚未绑定森空岛账号", MarkdownV2.ToPlain(response.TgText()));
        Assert.IsEmpty(response.TgButtonRows());
    }

    [TestMethod]
    public async Task SklandAutoSignAccountPanelPagesRoles()
    {
        var builder = CreateBuilder();
        var account = CreateAccounts(1)[0];
        for (var index = 1; index <= 7; index++)
        {
            account.Roles.Add(new SklandGameRole
            {
                Id = index,
                SklandAccountId = account.Id,
                GameId = 1,
                GameName = "明日方舟",
                NickName = $"Dr{index}",
                AutoSignEnabled = index % 2 == 0
            });
        }

        var response = await builder.BuildAutoSignAccountPanelAsync(CreateContext(), [account], account.Id);

        var plain = MarkdownV2.ToPlain(response.TgText());
        Assert.Contains("[森空岛-自动签到]", plain);
        Assert.Contains("总开关：开启", plain);
        Assert.Contains("第 1/2 页", plain);
        Assert.Contains("[关] 明日方舟/Dr1", plain);
        Assert.IsTrue(response.TgButtonTexts().Any(text => text == "下一页"));
        Assert.IsTrue(response.TgButtonTexts().Any(text => text == "开启/关闭全部"));
        Assert.IsTrue(response.TgButtonTexts().Any(text => text == "返回账号列表"));
    }

    [TestMethod]
    public async Task AutoSignPanelSurvivesQqNumberedMenuConversion()
    {
        // QQ 用不了官方按钮，面板在 gRPC 边界由 QqMenuConverter 拍平成编号菜单。
        // 插件本身没有任何平台分支，所以这次改的文案和新增按钮应当原样传导到 QQ；
        // 尤其是账号按钮从 1 列改成 2 列——行结构会被拍平，对 QQ 不应有任何影响。
        var cache = new FakeDistributedCache();
        var converter = new QqMenuConverter(
            new QqMenuStore(cache, Options.Create(new QqMenuOptions())));
        var panel = await CreateBuilder().BuildAutoSignPanelAsync(CreateContext(), CreateAccounts(2));

        var qq = await converter.ToQqAsync(panel, BotChatType.Private);

        var text = qq.QqText();
        // 正文的 MarkdownV2 转义已被还原成用户实际看到的样子。
        Assert.Contains("[森空岛-自动签到]", text);
        Assert.Contains("点击账号进入设置：", text);
        Assert.Contains("[开] #1 Skland1", text);
        // 两列的账号按钮 + 全部开关拍平成连续编号，一个都不能丢。
        Assert.Contains("1. [开] Skland1 #1", text);
        Assert.Contains("2. [关] Skland2 #2", text);
        Assert.Contains("3. 开启/关闭全部", text);
        Assert.IsFalse(string.IsNullOrEmpty(qq.Qq.Messages[0].MenuToken));
    }

    private static SklandResponseBuilder CreateBuilder()
    {
        var coreDbContext = new CoreDbContext(new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        return new SklandResponseBuilder(
            new CallbackActionStore(new FakeDistributedCache(), Options.Create(new CallbackActionOptions())),
            new NotificationSubscriptionService(coreDbContext, TimeProvider.System),
            TimeProvider.System);
    }

    private static SklandAccount[] CreateAccounts(int count)
    {
        return [.. Enumerable.Range(1, count).Select(index => new SklandAccount
        {
            Id = index,
            CoreUserId = 1,
            SklandUserId = $"skland-{index}",
            DisplayName = $"Skland{index}",
            AutoSignEnabled = index % 2 == 1
        })];
    }

    private static CommandContext CreateContext()
    {
        return new CommandContext(
            new CommandRequest
            {
                Platform = BotPlatform.Telegram,
                BotInstanceId = "tg",
                ChatId = "chat",
                UserId = "user",
                MessageId = "message",
                ChatType = BotChatType.Private
            },
            new ResolvedIdentity(1, UserPrivilege.VerifiedUser, BotPlatform.Telegram, "tg"),
            TimeProvider.System.GetTimestamp(),
            CancellationToken.None);
    }

    private sealed class FakeDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

        public byte[]? Get(string key)
        {
            _items.TryGetValue(key, out var value);
            return value;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _items[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _items.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
