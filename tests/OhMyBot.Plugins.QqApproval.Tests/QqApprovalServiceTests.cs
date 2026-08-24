using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Commanding.Platform;
using OhMyBot.Core.Commanding.Qq;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Plugins.QqApproval.Data;
using OhMyBot.Plugins.QqApproval.Data.Entities;
using OhMyBot.Plugins.QqApproval.Integrations;

namespace OhMyBot.Plugins.QqApproval.Tests;

/// <summary>
/// 审批流的行为约束。这里测的是「谁该被打扰、谁不该」和「谁能替 owner 做决定」，
/// 而不是字段搬运——这两件事错了会直接变成机器人被陌生人加好友或拉进群。
/// </summary>
[TestClass]
public sealed class QqApprovalServiceTests
{
    private const string OwnerQq = "10001";
    private const string SecondOwnerQq = "10002";

    // 群成员入群申请默认不接入：这是配置层的全局开关，个人接收开关留给 /notify。
    [TestMethod]
    public async Task GroupAddIsIgnoredUntilExplicitlyEnabled()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupAdd, "flag-a"));

        Assert.AreEqual(0, await harness.DbContext.QqApprovalRequests.CountAsync());
        Assert.AreEqual(0, harness.Notifications.Count);

        harness.Configuration.RequestTypes.GroupAdd.Enabled = true;
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupAdd, "flag-b"));

        Assert.AreEqual(1, await harness.DbContext.QqApprovalRequests.CountAsync());
        Assert.AreEqual(1, harness.Notifications.Count);
    }

    // 每个已订阅用户都要收到，且必须带菜单 token——没有 token 的通知在 QQ 上是死文本，无法审批。
    [TestMethod]
    public async Task NotifiesEverySubscriberWithSelectableMenu()
    {
        using var harness = new Harness(deliveries:
        [
            Delivery(1, OwnerQq),
            Delivery(2, SecondOwnerQq)
        ]);
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        CollectionAssert.AreEquivalent(
            new[] { OwnerQq, SecondOwnerQq },
            harness.Notifications.Select(item => item.ChatId).ToArray());
        Assert.IsTrue(harness.Notifications.All(item =>
            item.MenuTokens is { Count: 1 } && !string.IsNullOrEmpty(item.MenuTokens[0])));
    }

    // 权限被降级后即使订阅行仍存在也不能继续收到敏感请求。
    [TestMethod]
    public async Task SkipsSubscriberBelowRequiredPrivilege()
    {
        using var harness = new Harness(deliveries:
        [
            Delivery(1, OwnerQq),
            Delivery(2, SecondOwnerQq, UserPrivilege.Admin)
        ]);

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        CollectionAssert.AreEqual(
            new[] { OwnerQq },
            harness.Notifications.Select(item => item.ChatId).ToArray());
    }

    [TestMethod]
    public async Task DoesNotSendRequestAcrossBotInstances()
    {
        using var harness = new Harness(deliveries:
        [
            Delivery(1, OwnerQq),
            new NotificationDelivery(2, UserPrivilege.Owner, BotPlatform.Qq, "qq-other", SecondOwnerQq)
        ]);

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        CollectionAssert.AreEqual(
            new[] { OwnerQq },
            harness.Notifications.Select(item => item.ChatId).ToArray());
    }

    [TestMethod]
    public async Task NoSubscriptionStoresPendingRequestWithoutSendingNotification()
    {
        using var harness = new Harness(deliveries: []);

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        Assert.AreEqual(0, harness.Notifications.Count);
        Assert.AreEqual(QqApprovalStatus.Pending, (await harness.DbContext.QqApprovalRequests.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task DenyRuleAutoRejectsWithoutBotheringOwner()
    {
        using var harness = new Harness();
        await harness.Settings.SetRulesEnabledAsync(PlatformRequestKind.FriendAdd, true);
        await harness.Settings.UpsertRuleAsync(
            PlatformRequestKind.FriendAdd, QqApprovalRuleScope.Requester, "20001", QqApprovalRuleAction.Reject, "spam");

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        Assert.AreEqual(0, harness.Notifications.Count);
        var stored = await harness.DbContext.QqApprovalRequests.SingleAsync();
        Assert.AreEqual(QqApprovalStatus.AutoRejected, stored.Status);
        Assert.AreEqual(1, harness.Decisions.Count);
        Assert.IsFalse(harness.Decisions[0].Approve);
        Assert.AreEqual("flag-1", harness.Decisions[0].Flag);
    }

    [TestMethod]
    public async Task AllowRuleAutoApproves()
    {
        using var harness = new Harness();
        harness.Configuration.RequestTypes.GroupAdd.Enabled = true;
        await harness.Settings.SetRulesEnabledAsync(PlatformRequestKind.GroupAdd, true);
        await harness.Settings.UpsertRuleAsync(
            PlatformRequestKind.GroupAdd, QqApprovalRuleScope.Group, "30001", QqApprovalRuleAction.Approve, "自家群");

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupAdd, "flag-1"));

        Assert.AreEqual(0, harness.Notifications.Count);
        Assert.AreEqual(QqApprovalStatus.AutoApproved, (await harness.DbContext.QqApprovalRequests.SingleAsync()).Status);
        Assert.IsTrue(harness.Decisions.Single().Approve);
    }

    // 「也可以不开黑/白名单」：开关关掉时规则必须完全不参与判断，一律转人工。
    [TestMethod]
    public async Task RulesAreInertWhileDisabled()
    {
        using var harness = new Harness();
        await harness.Settings.UpsertRuleAsync(
            PlatformRequestKind.FriendAdd, QqApprovalRuleScope.Requester, "20001", QqApprovalRuleAction.Reject, string.Empty);

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        Assert.AreEqual(0, harness.Decisions.Count);
        Assert.AreEqual(1, harness.Notifications.Count);
        Assert.AreEqual(QqApprovalStatus.Pending, (await harness.DbContext.QqApprovalRequests.SingleAsync()).Status);
    }

    // 同一申请人同时命中黑白名单时按拒绝处理：放行的代价不可逆，拦错了还能人工补。
    [TestMethod]
    public async Task DenyWinsOverAllow()
    {
        using var harness = new Harness();
        harness.Configuration.RequestTypes.GroupAdd.Enabled = true;
        await harness.Settings.SetRulesEnabledAsync(PlatformRequestKind.GroupAdd, true);
        await harness.Settings.UpsertRuleAsync(
            PlatformRequestKind.GroupAdd, QqApprovalRuleScope.Group, "30001", QqApprovalRuleAction.Approve, string.Empty);
        await harness.Settings.UpsertRuleAsync(
            PlatformRequestKind.GroupAdd, QqApprovalRuleScope.Requester, "20001", QqApprovalRuleAction.Reject, string.Empty);

        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupAdd, "flag-1"));

        Assert.IsFalse(harness.Decisions.Single().Approve);
    }

    // 网关重连后 NapCat 会重推同一条请求；重复打扰 owner 会让通知失去信噪比。
    [TestMethod]
    public async Task DuplicateFlagDoesNotNotifyTwice()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        Assert.AreEqual(1, await harness.DbContext.QqApprovalRequests.CountAsync());
        Assert.AreEqual(1, harness.Notifications.Count);
    }

    // 多个 owner 同时点同一条：只能有一个决定真正下发，否则会给 QQ 发两次互相矛盾的回执。
    [TestMethod]
    public async Task SecondDecisionOnSameRequestIsRejected()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));
        var requestId = (await harness.DbContext.QqApprovalRequests.SingleAsync()).Id;

        var first = await harness.Service.DecideAsync(requestId, approve: true, decidedByCoreUserId: 1);
        var second = await harness.Service.DecideAsync(requestId, approve: false, decidedByCoreUserId: 2);

        StringAssert.Contains(first, "已同意");
        StringAssert.Contains(second, "已经处理过");
        Assert.AreEqual(1, harness.Decisions.Count);
        Assert.IsTrue(harness.Decisions[0].Approve);
    }

    // 通知里要能看清「是谁」：昵称、性别、等级、头像图都得带上，否则 owner 只看到一串 QQ 号没法决策。
    [TestMethod]
    public async Task NotificationCarriesRequesterProfileAndAvatar()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1", profile: new Dictionary<string, string>
        {
            [PlatformRequestProfileKeys.Nickname] = "小明",
            [PlatformRequestProfileKeys.Gender] = "male",
            [PlatformRequestProfileKeys.Age] = "18",
            [PlatformRequestProfileKeys.Level] = "32",
            [PlatformRequestProfileKeys.AvatarUrl] = "https://q.qlogo.cn/headimg_dl?dst_uin=20001&spec=640"
        }));

        var text = harness.Notifications.Single().Messages.Single();
        StringAssert.Contains(text, "申请人");
        StringAssert.Contains(text, "性别 男");
        StringAssert.Contains(text, "18 岁");
        StringAssert.Contains(text, "QQ 等级 32");
        StringAssert.Contains(text, "[CQ:image,file=https://q.qlogo.cn/headimg_dl?dst_uin=20001&amp;spec=640]");
    }

    // 昵称和附言是对方完全可控的：不转义的话，写一段 CQ 码就能借机器人之口对 owner 发任意消息段。
    [TestMethod]
    public async Task RequesterControlledTextIsCqEscaped()
    {
        using var harness = new Harness();
        var notice = Notice(PlatformRequestKind.FriendAdd, "flag-1") with
        {
            RequesterName = "[CQ:at,qq=all]",
            Comment = "看这个 [CQ:image,file=http://evil/x.jpg]"
        };
        await harness.Service.HandleAsync(notice);

        var text = harness.Notifications.Single().Messages.Single();
        Assert.IsFalse(text.Contains("[CQ:at", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("[CQ:image,file=http://evil", StringComparison.Ordinal));
        StringAssert.Contains(text, "&#91;CQ:at");
    }

    // 档案缺失时不写「未知」占位：查不到就整行不出现，别把噪声塞进通知。
    [TestMethod]
    public async Task MissingProfileOmitsDetailLine()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.FriendAdd, "flag-1"));

        var text = harness.Notifications.Single().Messages.Single();
        Assert.IsFalse(text.Contains("资料：", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("CQ:image", StringComparison.Ordinal));
    }

    // 邀请入群只显示群号的话根本判断不了该不该进；群名查到就必须用上，查不到才退回群号。
    [TestMethod]
    public async Task GroupRequestShowsGroupNameWhenKnown()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupInvite, "flag-1", profile: new Dictionary<string, string>
        {
            [PlatformRequestProfileKeys.GroupName] = "摸鱼交流群"
        }));

        var text = harness.Notifications.Single().Messages.Single();
        StringAssert.Contains(text, "群：摸鱼交流群(30001)");

        var pending = await harness.Service.ListPendingAsync(10);
        StringAssert.Contains(QqApprovalService.Summarize(pending.Single()), "摸鱼交流群(30001)");
    }

    [TestMethod]
    public async Task GroupRequestFallsBackToGroupIdWhenNameUnknown()
    {
        using var harness = new Harness();
        await harness.Service.HandleAsync(Notice(PlatformRequestKind.GroupInvite, "flag-1"));

        StringAssert.Contains(harness.Notifications.Single().Messages.Single(), "群：群 30001");
    }

    private static PlatformRequestNotice Notice(
        PlatformRequestKind kind,
        string flag,
        IReadOnlyDictionary<string, string>? profile = null)
    {
        return new PlatformRequestNotice(
            BotPlatform.Qq,
            "qq-test",
            kind,
            flag,
            RequesterId: "20001",
            RequesterName: "申请人",
            GroupId: kind == PlatformRequestKind.FriendAdd ? string.Empty : "30001",
            Comment: "验证消息",
            OccurredAt: DateTimeOffset.UnixEpoch,
            RequesterProfile: profile);
    }

    private static NotificationDelivery Delivery(
        long coreUserId,
        string chatId,
        UserPrivilege privilege = UserPrivilege.Owner) =>
        new(coreUserId, privilege, BotPlatform.Qq, "qq-test", chatId);

    private sealed class Harness : IDisposable
    {
        public Harness(
            IReadOnlyList<NotificationDelivery>? deliveries = null,
            UserPrivilege required = UserPrivilege.Owner)
        {
            Configuration = new QqApprovalOptions
            {
                ApprovalRequiredPrivilege = UserPrivilegeNames.Format(required),
                PendingTtl = TimeSpan.FromHours(1)
            };
            Configuration.RequestTypes.FriendAdd.RequiredPrivilege = UserPrivilegeNames.Format(required);
            Configuration.RequestTypes.GroupInvite.RequiredPrivilege = UserPrivilegeNames.Format(required);
            Configuration.RequestTypes.GroupAdd.RequiredPrivilege = UserPrivilegeNames.Format(required);
            var options = Microsoft.Extensions.Options.Options.Create(Configuration);
            // 跑在真实 PostgreSQL 上（与生产同 provider）：并发裁决依赖带条件的原子 UPDATE，
            // 而 InMemory 压根不支持 ExecuteUpdate、SQLite 又在 DateTimeOffset 排序等处与 Npgsql 行为不同——
            // 用替身 provider 等于把「这条查询在生产能不能跑」排除在测试之外。
            // 每个 harness 建一个一次性库，用完删掉；连接串可用 OHMYBOT_TEST_POSTGRES 覆盖。
            DbContext = new QqApprovalDbContext(new DbContextOptionsBuilder<QqApprovalDbContext>()
                .UseNpgsql(ConnectionString(_database))
                .Options);
            DbContext.Database.EnsureDeleted();
            DbContext.Database.EnsureCreated();
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            Settings = new QqApprovalSettingsService(DbContext, TimeProvider.System);
            Subscriptions = new FakeNotificationSubscriptionService(
                deliveries ?? [Delivery(1, OwnerQq)]);
            Service = new QqApprovalService(
                DbContext,
                Settings,
                new CallbackActionStore(cache, Options.Create(new CallbackActionOptions())),
                new QqMenuStore(cache, Options.Create(new QqMenuOptions())),
                Subscriptions,
                new FakeNotificationPublisher(Notifications),
                new FakeDecisionPublisher(Decisions),
                options,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<QqApprovalService>.Instance);
        }

        private readonly string _database = $"ohmybot_qqapproval_test_{Guid.NewGuid():N}";

        public QqApprovalDbContext DbContext { get; }
        public QqApprovalOptions Configuration { get; }
        public QqApprovalSettingsService Settings { get; }
        public QqApprovalService Service { get; }
        public FakeNotificationSubscriptionService Subscriptions { get; }
        public List<SentNotification> Notifications { get; } = [];
        public List<SentDecision> Decisions { get; } = [];

        private static string ConnectionString(string database)
        {
            var server = Environment.GetEnvironmentVariable("OHMYBOT_TEST_POSTGRES")
                ?? $"Host=localhost;Port=5432;Username={Environment.UserName}";
            return $"{server};Database={database}";
        }

        public void Dispose()
        {
            DbContext.Database.EnsureDeleted();
            DbContext.Dispose();
        }
    }

    private sealed record SentNotification(string ChatId, IReadOnlyList<string> Messages, IReadOnlyList<string>? MenuTokens);

    private sealed record SentDecision(PlatformRequestKind Kind, string Flag, bool Approve);

    private sealed class FakeNotificationPublisher(List<SentNotification> sink) : INotificationPublisher
    {
        public Task PublishAsync(
            BotPlatform platform,
            string botInstanceId,
            string chatId,
            IReadOnlyList<string> messages,
            IReadOnlyList<string>? menuTokens = null,
            CancellationToken cancellationToken = default)
        {
            sink.Add(new SentNotification(chatId, messages, menuTokens));
            return Task.CompletedTask;
        }

        public Task PublishTelegramAsync(
            string botInstanceId,
            string chatId,
            IReadOnlyList<string> messages,
            CancellationToken cancellationToken = default)
            => PublishAsync(BotPlatform.Telegram, botInstanceId, chatId, messages, null, cancellationToken);
    }

    private sealed class FakeDecisionPublisher(List<SentDecision> sink) : IPlatformRequestDecisionPublisher
    {
        public Task PublishAsync(
            BotPlatform platform,
            string botInstanceId,
            PlatformRequestKind kind,
            string flag,
            bool approve,
            string reason,
            CancellationToken cancellationToken = default)
        {
            sink.Add(new SentDecision(kind, flag, approve));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotificationSubscriptionService(IReadOnlyList<NotificationDelivery> deliveries)
        : INotificationSubscriptionService
    {
        public Task<HashSet<long>> GetEnabledTargetIdsAsync(long coreUserId, BotPlatform platform,
            string notificationType, IReadOnlyCollection<long> knownTargetIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HashSet<long>());

        public Task<List<NotificationDelivery>> ListEnabledDeliveriesByTargetAsync(string notificationType,
            long targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(deliveries.ToList());

        public Task ToggleAsync(long coreUserId, BotPlatform platform, string botInstanceId, string chatId,
            string notificationType, long targetId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnableAsync(long coreUserId, BotPlatform platform, string botInstanceId, string chatId,
            string notificationType, long targetId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ToggleAllAsync(long coreUserId, BotPlatform platform, string botInstanceId, string chatId,
            string notificationType, IReadOnlyCollection<long> targetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteTargetAsync(long coreUserId, string notificationType, long targetId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
