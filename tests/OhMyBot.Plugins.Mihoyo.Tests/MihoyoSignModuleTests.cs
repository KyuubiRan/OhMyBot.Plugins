using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Mihoyo;
using OhMyBot.Plugin.Commanding.SignIn;
using OhMyBot.Plugins.Mihoyo.Data;

namespace OhMyBot.Plugins.Mihoyo.Tests;

[TestClass]
public sealed class MihoyoSignModuleTests
{
    [TestMethod]
    public async Task CommunityTimeoutExhaustsThreeRetriesButGameStillRuns()
    {
        await using var db = CreateDbContext();
        var handler = new StubHandler(request => IsMission(request)
            ? throw new TimeoutException()
            : DefaultResponse(request));
        var account = CreateAccount();
        var service = CreateService(db, handler);

        var result = await service.ExecuteAutoSignAsync(account);

        Assert.AreEqual(4, handler.Requests.Count(IsMission));
        Assert.AreEqual(1, handler.Requests.Count(IsGameInfo));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("社区签到执行失败") && line.Contains("已重试 3 次")));
        Assert.Contains("签到结果：今日已签到", result.Lines);
    }

    [TestMethod]
    public async Task GameTimeoutKeepsCompletedCommunityResult()
    {
        await using var db = CreateDbContext();
        var handler = new StubHandler(request => IsGameInfo(request)
            ? throw new TimeoutException()
            : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(CreateAccount());

        Assert.Contains("社区签到：今日已完成", result.Lines);
        Assert.AreEqual(1, handler.Requests.Count(IsMission));
        Assert.AreEqual(4, handler.Requests.Count(IsGameInfo));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("原神签到执行失败") && line.Contains("已重试 3 次")));
        Assert.IsFalse(result.Lines.Any(line => line.Contains("Operation canceled")));
    }

    [TestMethod]
    public async Task FailedGameRoleDoesNotPreventOtherGameRole()
    {
        await using var db = CreateDbContext();
        var account = CreateAccount();
        account.Roles.Add(new MihoyoGameRole
        {
            GameBiz = "hkrpg_cn", GameName = "星穹铁道", GameUid = 200,
            Region = "prod_gf_cn", AutoSignEnabled = true
        });
        var handler = new StubHandler(request => IsGameInfo(request) && request.RequestUri!.Query.Contains("uid=100")
            ? throw new TimeoutException()
            : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        Assert.AreEqual(4, handler.Requests.Count(request => IsGameInfo(request) && request.RequestUri!.Query.Contains("uid=100")));
        Assert.AreEqual(1, handler.Requests.Count(request => IsGameInfo(request) && request.RequestUri!.Query.Contains("uid=200")));
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.IsTrue(result.Lines.Any(line => line.Contains("原神签到执行失败")));
    }

    [TestMethod]
    public async Task EveryHttpRetryRecreatesRequestAndPostBody()
    {
        var attempt = 0;
        var handler = new StubHandler(_ => ++attempt < 4 ? throw new TimeoutException() : Json("""{"retcode":0,"data":{}}"""));
        var client = CreateClient(handler);

        var result = await client.CnGameSignAsync("cookie_token=demo", MihoyoGameCatalog.FindByKey("genshin")!, "cn_gf01", "100");

        Assert.IsTrue(result.Ok);
        Assert.AreEqual(4, attempt);
        Assert.AreEqual(4, handler.Requests.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.IsTrue(handler.Requests.All(request => request.Method == HttpMethod.Post));
        Assert.IsTrue(handler.Bodies.All(body => body.Contains("\"uid\":\"100\"")));
        Assert.AreEqual(1, handler.Bodies.Distinct().Count());
    }

    [TestMethod]
    public async Task SuccessfulCommunityTaskIsNotRepeatedWhenLaterTaskFails()
    {
        await using var db = CreateDbContext();
        var account = CreateAccount();
        account.BbsTaskFlags = MihoyoBbsTaskFlags.SignIn | MihoyoBbsTaskFlags.SharePosts;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("getShareConf")
            ? throw new TimeoutException()
            : IsMission(request) ? PendingMissions() : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteBbsSignAsync(account, account.BbsTaskFlags);

        Assert.Contains("社区签到：成功", result.Lines);
        Assert.AreEqual(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("signIn")));
        Assert.AreEqual(4, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("getShareConf")));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("分享帖子执行失败") && line.Contains("已重试 3 次")));
    }

    [TestMethod]
    public async Task CompletedPostReadIsPreservedWhenNextReadExhaustsRetries()
    {
        await using var db = CreateDbContext();
        var account = CreateAccount();
        account.BbsTaskFlags = MihoyoBbsTaskFlags.ViewPosts;
        var reads = 0;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("getPostFull") && ++reads > 1
            ? throw new TimeoutException()
            : IsMission(request) ? PendingMissions() : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteBbsSignAsync(account, account.BbsTaskFlags);

        Assert.AreEqual(5, reads);
        Assert.Contains("浏览帖子：成功 1 次，剩余任务未完成", result.Lines);
        Assert.IsTrue(result.Lines.Any(line => line.Contains("浏览帖子执行失败") && line.Contains("已重试 3 次")));
    }

    [TestMethod]
    public async Task CommunityCaptchaDoesNotRetryAndGameStillRuns()
    {
        await using var db = CreateDbContext();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("signIn")
            ? Json("""{"retcode":1034,"message":"验证码"}""")
            : IsMission(request) ? PendingMissions() : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(CreateAccount());

        Assert.AreEqual(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("signIn")));
        Assert.Contains("社区签到：触发验证码，已跳过（不支持自动打码）", result.Lines);
        Assert.Contains("签到结果：今日已签到", result.Lines);
    }

    [TestMethod]
    public async Task CommunityStokenExpiryKeepsGameCookieAndDoesNotRetryLoginFailure()
    {
        await using var db = CreateDbContext();
        var account = CreateAccount();
        db.MihoyoAccounts.Add(account);
        await db.SaveChangesAsync();
        var cookie = account.CookieCiphertext;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("signIn")
            ? Json("""{"retcode":-100,"message":"未登录"}""")
            : IsMission(request) ? PendingMissions() : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(account);

        var saved = await db.MihoyoAccounts.FindAsync(account.Id);
        Assert.AreEqual(cookie, saved!.CookieCiphertext);
        Assert.AreEqual(string.Empty, saved.StokenCiphertext);
        Assert.AreEqual(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("signIn")));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("社区 stoken 已失效")));
        Assert.Contains("签到结果：今日已签到", result.Lines);
    }

    [TestMethod]
    public async Task ServerFailureIsRetriedButUnauthorizedHttpStatusIsNot()
    {
        var calls = 0;
        var transient = new StubHandler(_ => ++calls < 4
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
            : Json("""{"retcode":0,"data":{}}"""));
        var result = await CreateClient(transient).GetMissionsAsync("cookie_token=demo");
        Assert.IsTrue(result.Ok);
        Assert.AreEqual(4, calls);

        var unauthorized = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized")
        });
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => CreateClient(unauthorized).GetMissionsAsync("cookie_token=demo"));
        Assert.HasCount(1, unauthorized.Requests);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndDoesNotRetryOrStartGame()
    {
        await using var db = CreateDbContext();
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            CreateService(db, handler).ExecuteAutoSignAsync(CreateAccount(), cancellation.Token));

        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual(0, handler.Requests.Count(IsGameInfo));
    }

    [TestMethod]
    public async Task TimedOutGamePostAcceptsAlreadySignedOnRetryWithoutRepeatingOtherRequests()
    {
        await using var db = CreateDbContext();
        var calls = 0;
        var handler = new StubHandler(request => IsGameInfo(request)
            ? Json("""{"retcode":0,"data":{"is_sign":false,"total_sign_day":1}}""")
            : request.RequestUri!.AbsolutePath.EndsWith("/sign")
                ? ++calls == 1 ? throw new TimeoutException() : Json("""{"retcode":-5003,"message":"已签到"}""")
                : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteGameSignAsync(CreateAccount());

        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, handler.Requests.Count(IsGameInfo));
        Assert.AreEqual(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("/home")));
    }

    [TestMethod]
    public async Task CookieRefreshTimeoutDoesNotClearEitherCredential()
    {
        await using var db = CreateDbContext();
        var account = CreateAccount();
        db.MihoyoAccounts.Add(account);
        await db.SaveChangesAsync();
        var cookie = account.CookieCiphertext;
        var stoken = account.StokenCiphertext;
        var handler = new StubHandler(request => IsGameInfo(request)
            ? Json("""{"retcode":-100,"message":"未登录"}""")
            : request.RequestUri!.AbsolutePath.EndsWith("getCookieAccountInfoBySToken")
                ? throw new TimeoutException()
                : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        var saved = await db.MihoyoAccounts.FindAsync(account.Id);
        Assert.AreEqual(cookie, saved!.CookieCiphertext);
        Assert.AreEqual(stoken, saved.StokenCiphertext);
        Assert.AreEqual(4, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("getCookieAccountInfoBySToken")));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("已重试 3 次")));
    }

    [TestMethod]
    public async Task RewardQueryTimeoutDoesNotPreventGameSignIn()
    {
        await using var db = CreateDbContext();
        var handler = new StubHandler(request => IsGameInfo(request)
            ? Json("""{"retcode":0,"data":{"is_sign":false,"total_sign_day":1}}""")
            : request.RequestUri!.AbsolutePath.EndsWith("/home")
                ? throw new TimeoutException()
                : DefaultResponse(request));

        var result = await CreateService(db, handler).ExecuteGameSignAsync(CreateAccount());

        Assert.AreEqual(4, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("/home")));
        Assert.AreEqual(1, handler.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("/sign")));
        Assert.Contains("签到结果：成功", result.Lines);
        Assert.IsTrue(result.Lines.Any(line => line.Contains("游戏奖励查询执行失败") && line.Contains("已重试 3 次")));
    }

    private static MihoyoDbContext CreateDbContext() => new(new DbContextOptionsBuilder<MihoyoDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MihoyoAccount CreateAccount() => new()
    {
        Id = 1, CoreUserId = 1, DisplayName = "tester", Stuid = 10, Region = MihoyoRegion.Cn,
        CookieCiphertext = "account_id=10;cookie_token=demo", StokenCiphertext = "demo-stoken",
        BbsTaskFlags = MihoyoBbsTaskFlags.SignIn, AutoSignEnabled = true,
        Roles = { new MihoyoGameRole { GameBiz = "hk4e_cn", GameName = "原神", GameUid = 100, Region = "cn_gf01", AutoSignEnabled = true } }
    };

    private static SignInExecutor CreateExecutor() => new(NullLogger<SignInExecutor>.Instance, retryDelay: TimeSpan.Zero);

    private static MihoyoHttpClient CreateClient(StubHandler handler) => new(new HttpClient(handler), Options.Create(new MihoyoOptions()), CreateExecutor());

    private static MihoyoSignService CreateService(MihoyoDbContext db, StubHandler handler)
    {
        var client = CreateClient(handler);
        var accountService = new MihoyoAccountService(db, client, new PlainSecretProtector(), TimeProvider.System);
        var credentials = new MihoyoSignCredentialService(client, accountService);
        var executor = CreateExecutor();
        var time = new ImmediateTimeProvider();
        return new MihoyoSignService(
            new MihoyoCommunitySignModule(client, accountService, credentials, Options.Create(new MihoyoOptions()), executor, time),
            new MihoyoGameSignModule(client, accountService, credentials, executor, NullLogger<MihoyoGameSignModule>.Instance, time),
            executor);
    }

    private static bool IsMission(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("getUserMissionsState");
    private static bool IsGameInfo(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/info");
    private static HttpResponseMessage PendingMissions() => Json("""{"retcode":0,"data":{"can_get_points":10,"states":[]}}""");

    private static HttpResponseMessage DefaultResponse(HttpRequestMessage request)
    {
        if (IsMission(request)) return Json("""{"retcode":0,"data":{"can_get_points":0,"states":[]}}""");
        if (IsGameInfo(request)) return Json("""{"retcode":0,"data":{"is_sign":true,"total_sign_day":1}}""");
        if (request.RequestUri!.AbsolutePath.EndsWith("getForumPostList"))
            return Json("""{"retcode":0,"data":{"list":[{"post":{"post_id":"1"}}]}}""");
        return Json("""{"retcode":0,"message":"OK","data":{}}""");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class PlainSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}
