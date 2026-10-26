using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Notifications;
using OhMyBot.Core.Infrastructure.Data;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Messaging;
using OhMyBot.Core.Infrastructure.ScheduledTasks;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Kuro;
using OhMyBot.Plugins.Kuro.Data;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Plugins.Kuro.Tests;

[TestClass]
public sealed class KuroAutoSignFailureTests
{
    [TestMethod]
    public async Task GameHttpTimeoutNotificationPreservesCompletedCommunityTasks()
    {
        var publisher = new RecordingPublisher();
        var status = await RunAutoSignAsync(new GameFailureHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The HTTP timeout should cancel the request.");
        }, communityAlreadySigned: false), publisher, httpTimeout: TimeSpan.FromMilliseconds(100));

        var message = publisher.Messages.Single();
        Assert.Contains("[库街区-自动签到]", message);
        Assert.Contains("账号：测试账号 (1001)", message);
        Assert.Contains("社区签到结果：成功", message);
        Assert.Contains("游戏签到执行失败：接口请求超时", message);
        Assert.Contains("已重试 3 次", message);
        Assert.Contains("时间：", message);
        StringAssert.Matches(message, new System.Text.RegularExpressions.Regex(@"错误 id: [0-9a-f]{6}"));
        Assert.DoesNotContain("Operation canceled", message);
        Assert.DoesNotContain("自动签到执行失败：", message);
        Assert.AreEqual(ManagedTaskOutcome.Failed, status.Outcome);
        Assert.IsNotNull(status.LastErrorId);
        Assert.Contains(nameof(SignInRetryExhaustedException), status.LastErrorDetails!);
    }

    [TestMethod]
    public async Task UnexpectedGameFailureKeepsCommunityResultsWithoutExposingExceptionDetails()
    {
        var publisher = new RecordingPublisher();
        await RunAutoSignAsync(new GameFailureHandler(_ => throw new InvalidOperationException(
            "internal-host/database?token=private-value")), publisher);

        var message = publisher.Messages.Single();
        Assert.Contains("社区签到结果：今日已签到", message);
        Assert.Contains("游戏签到执行失败：签到处理异常", message);
        Assert.Contains("错误 id:", message);
        Assert.DoesNotContain("internal-host", message);
        Assert.DoesNotContain("private-value", message);
    }

    [TestMethod]
    public async Task CommunityFailureNotificationExplainsHttpErrorWithoutRawException()
    {
        var publisher = new RecordingPublisher();
        var handler = new GameFailureHandler(_ => Task.FromResult(AlreadySigned()), failCommunity: true);
        var status = await RunAutoSignAsync(handler, publisher);

        var message = publisher.Messages.Single();
        Assert.Contains("社区签到执行失败：接口请求失败", message);
        Assert.Contains("已重试 3 次", message);
        Assert.Contains("签到结果：今日已签到", message);
        Assert.AreEqual(4, handler.CommunityCalls);
        Assert.AreEqual(1, handler.GameCalls);
        Assert.Contains("账号：测试账号 (1001)", message);
        Assert.Contains("时间：", message);
        Assert.DoesNotContain("private-value", message);
        Assert.DoesNotContain("社区签到结果：今日已签到", message);
        Assert.AreEqual(ManagedTaskOutcome.Failed, status.Outcome);
    }

    [TestMethod]
    public async Task GameRecoversOnThirdRetryWithoutRepeatingCommunity()
    {
        var publisher = new RecordingPublisher();
        var attempts = 0;
        var handler = new GameFailureHandler(_ =>
        {
            if (++attempts < 4) throw new HttpRequestException("Temporary failure", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(AlreadySigned());
        });
        var status = await RunAutoSignAsync(handler, publisher);

        Assert.AreEqual(4, attempts);
        Assert.AreEqual(1, handler.CommunityCalls);
        var message = publisher.Messages.Single();
        Assert.Contains("社区签到结果：今日已签到", message);
        Assert.Contains("签到结果：今日已签到", message);
        Assert.DoesNotContain("执行失败", message);
        Assert.AreEqual(ManagedTaskOutcome.Succeeded, status.Outcome);
        Assert.AreEqual(0, status.FailureCount);
    }

    [TestMethod]
    public async Task SuccessfulGamePostWithLostResponseIsNotSubmittedTwice()
    {
        var publisher = new RecordingPublisher();
        var signed = false;
        var submissions = 0;
        await RunAutoSignAsync(new ScenarioHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/encourage/level/getTaskProcess")
                return Task.FromResult(CommunityCompleted());
            if (request.RequestUri?.AbsolutePath == "/encourage/signIn/initSignInV2")
                return Task.FromResult(signed ? AlreadySigned() : Json("""{"code":200,"success":true,"data":{"isSigIn":false,"sigInNum":0}}"""));
            Assert.AreEqual("/encourage/signIn/v2", request.RequestUri?.AbsolutePath);
            signed = true;
            submissions++;
            throw new TaskCanceledException("Response lost", new TimeoutException("Timeout", new IOException("Operation canceled")));
        }), publisher);

        Assert.AreEqual(1, submissions);
        Assert.Contains("社区签到结果：今日已签到", publisher.Messages.Single());
        Assert.Contains("签到结果：今日已签到", publisher.Messages.Single());
        Assert.DoesNotContain("执行失败", publisher.Messages.Single());
    }

    [TestMethod]
    public async Task OneGameRoleFailureDoesNotBlockAnotherRole()
    {
        var publisher = new RecordingPublisher();
        var failingRoleCalls = 0;
        var successfulRoleCalls = 0;
        await RunAutoSignAsync(new ScenarioHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/encourage/level/getTaskProcess") return CommunityCompleted();
            var body = await request.Content!.ReadAsStringAsync(token);
            if (body.Contains("gameId=3", StringComparison.Ordinal))
            {
                failingRoleCalls++;
                throw new HttpRequestException("Temporary failure");
            }

            successfulRoleCalls++;
            return AlreadySigned();
        }), publisher, addRole: true);

        Assert.AreEqual(4, failingRoleCalls);
        Assert.AreEqual(1, successfulRoleCalls);
        Assert.Contains("游戏签到执行失败", publisher.Messages.Single());
        Assert.Contains("[战双帕弥什] 指挥官", publisher.Messages.Single());
        Assert.Contains("签到结果：今日已签到", publisher.Messages.Single());
    }

    [TestMethod]
    public async Task CompletedCommunityPostReadIsRetainedWhenNextReadExhaustsRetries()
    {
        var publisher = new RecordingPublisher();
        var reads = 0;
        await RunAutoSignAsync(new ScenarioHandler((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/encourage/level/getTaskProcess" => Task.FromResult(Json("""{"code":200,"success":true,"data":{"dailyTask":[{"remark":"浏览3篇帖子","completeTimes":0,"needActionTimes":3}]}}""")),
                "/forum/list" => Task.FromResult(Json("""{"code":200,"success":true,"data":{"postList":[{"postId":"first"},{"postId":"second"}]}}""")),
                "/forum/getPostDetail" when ++reads == 1 => Task.FromResult(Json("""{"code":200,"success":true,"data":{}}""")),
                "/forum/getPostDetail" => throw new TimeoutException(),
                "/encourage/signIn/initSignInV2" => Task.FromResult(AlreadySigned()),
                _ => throw new AssertFailedException("Unexpected request")
            };
        }), publisher, bbsFlags: KuroBbsTaskFlags.ViewPosts);

        Assert.AreEqual(5, reads);
        Assert.Contains("浏览帖子结果：成功 1/2", publisher.Messages.Single());
        Assert.Contains("浏览帖子执行失败：接口请求超时", publisher.Messages.Single());
        Assert.Contains("已重试 3 次", publisher.Messages.Single());
        Assert.Contains("签到结果：今日已签到", publisher.Messages.Single());
    }

    [TestMethod]
    public async Task ExpiredTokenInPostListIsNotReportedAsEmptySuccessfulTask()
    {
        var publisher = new RecordingPublisher();
        var postLists = 0;
        await RunAutoSignAsync(new ScenarioHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/encourage/level/getTaskProcess") return Task.FromResult(CommunityCompleted());
            Assert.AreEqual("/forum/list", request.RequestUri?.AbsolutePath);
            postLists++;
            return Task.FromResult(Json("""{"code":220,"success":false,"msg":"Token expired"}"""));
        }), publisher, bbsFlags: KuroBbsTaskFlags.ViewPosts);

        Assert.AreEqual(1, postLists);
        Assert.Contains("Token 已失效，请重新绑定库街区账号", publisher.Messages.Single());
        Assert.DoesNotContain("成功 0/0", publisher.Messages.Single());
        Assert.DoesNotContain("已重试", publisher.Messages.Single());
    }

    [TestMethod]
    public async Task TaskCancellationIsPropagatedWithoutSendingFailureNotification()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new RecordingPublisher();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAutoSignAsync(
            new GameFailureHandler(cancellationToken =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new AssertFailedException("Task cancellation must interrupt the HTTP request.");
            }), publisher, cancellation.Token));

        Assert.HasCount(0, publisher.Messages);
    }

    private static async Task<ManagedTaskStatus> RunAutoSignAsync(
        HttpMessageHandler handler,
        RecordingPublisher publisher,
        CancellationToken cancellationToken = default,
        TimeSpan? httpTimeout = null,
        bool addRole = false,
        long bbsFlags = KuroBbsTaskFlags.SignIn)
    {
        await using var kuroDb = new KuroDbContext(new DbContextOptionsBuilder<KuroDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        kuroDb.CoreUsers.Add(new PluginCoreUser { Id = 1, Privilege = UserPrivilege.VerifiedUser });
        kuroDb.KuroAccounts.Add(new KuroAccount
        {
            Id = 10,
            CoreUserId = 1,
            BbsUserId = 1001,
            DisplayName = "测试账号",
            TokenCiphertext = "test-token",
            AutoSignEnabled = true,
            BbsTaskFlags = bbsFlags,
            Roles = [new KuroGameRole
            {
                GameId = 3, GameName = "鸣潮", ServerId = "1", RoleId = 2001,
                RoleName = "漂泊者", AutoSignEnabled = true
            }]
        });
        if (addRole)
        {
            kuroDb.KuroGameRoles.Add(new KuroGameRole
            {
                KuroAccountId = 10, GameId = 2, GameName = "战双帕弥什", ServerId = "1",
                RoleId = 3001, RoleName = "指挥官", AutoSignEnabled = true
            });
        }
        await kuroDb.SaveChangesAsync();

        await using var coreDb = new CoreDbContext(new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        coreDb.CoreUsers.Add(new CoreUser { Id = 1, Privilege = UserPrivilege.VerifiedUser });
        await coreDb.SaveChangesAsync();
        var subscriptions = new NotificationSubscriptionService(coreDb, TimeProvider.System);
        await subscriptions.EnableAsync(1, BotPlatform.Telegram, "tg", "chat", NotificationTypes.KuroAutoSign, 10);

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.kurobbs.com"),
            Timeout = httpTimeout ?? TimeSpan.FromSeconds(20)
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<ScheduledTaskOptions>("KuroAutoSign");
        services.AddSingleton(kuroDb);
        var executor = new SignInExecutor(NullLogger<SignInExecutor>.Instance, retryDelay: TimeSpan.Zero);
        services.AddSingleton(executor);
        services.AddSingleton(new KuroHttpClient(httpClient, Options.Create(new KuroOptions()), executor));
        services.AddSingleton<ISecretProtector, PlainSecretProtector>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<KuroAccountService>();
        services.AddScoped<KuroCommunitySignModule>();
        services.AddScoped<KuroGameSignModule>();
        services.AddScoped<KuroSignService>();
        services.AddSingleton<INotificationSubscriptionService>(subscriptions);
        services.AddSingleton<INotificationPublisher>(publisher);
        await using var provider = services.BuildServiceProvider();
        var task = new KuroAutoSignManagedTask(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<ScheduledTaskOptions>>(),
            TimeProvider.System,
            NullLogger<KuroAutoSignManagedTask>.Instance);

        await task.ExecuteAsync(cancellationToken);
        return task.Status;
    }

    private sealed class GameFailureHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> gameRequest,
        bool failCommunity = false,
        bool communityAlreadySigned = true) : HttpMessageHandler
    {
        public int CommunityCalls { get; private set; }
        public int GameCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/encourage/level/getTaskProcess")
            {
                CommunityCalls++;
                if (failCommunity)
                {
                    throw new HttpRequestException("Connection to internal-host failed: private-value");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(communityAlreadySigned
                        ? """{"code":200,"success":true,"data":{"dailyTask":[{"remark":"用户签到","finished":true}]}}"""
                        : """{"code":200,"success":true,"data":{"dailyTask":[]}}""")
                });
            }

            if (request.RequestUri?.AbsolutePath == "/user/signIn")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":200,"success":true,"msg":"请求成功","data":{}}""")
                });
            }

            Assert.AreEqual("/encourage/signIn/initSignInV2", request.RequestUri?.AbsolutePath);
            GameCalls++;
            return gameRequest(cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage AlreadySigned() => Json("""{"code":200,"success":true,"data":{"isSigIn":true,"sigInNum":1}}""");
    private static HttpResponseMessage CommunityCompleted() => Json("""{"code":200,"success":true,"data":{"dailyTask":[{"remark":"用户签到","finished":true}]}}""");

    private sealed class ScenarioHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }

    private sealed class PlainSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class RecordingPublisher : INotificationPublisher
    {
        public List<string> Messages { get; } = [];

        public Task PublishAsync(BotPlatform platform, string botInstanceId, string chatId,
            IReadOnlyList<string> messages, IReadOnlyList<string>? menuTokens = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task PublishTelegramAsync(string botInstanceId, string chatId, IReadOnlyList<string> messages,
            CancellationToken cancellationToken = default) => PublishAsync(
                BotPlatform.Telegram, botInstanceId, chatId, messages, cancellationToken: cancellationToken);
    }
}
