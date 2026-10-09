using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Skland;
using OhMyBot.Plugin.Commanding.SignIn;
using OhMyBot.Plugins.Skland.Data;

namespace OhMyBot.Plugins.Skland.Tests;

[TestClass]
public sealed class SklandSignIsolationTests
{
    [TestMethod]
    public async Task AutoSignTimeoutRetriesThreeTimesAndPreservesOtherRoleResult()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"), ArknightsRole(12, "second"));
        var firstRequests = 0;
        var secondRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("uid=first", StringComparison.Ordinal))
            {
                firstRequests++;
                throw new TaskCanceledException("Operation canceled", new TimeoutException());
            }

            secondRequests++;
            return Task.FromResult(ArknightsAlreadySigned());
        });

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(account);

        Assert.AreEqual(4, firstRequests);
        Assert.AreEqual(1, secondRequests);
        Assert.HasCount(2, result.Lines.Where(line => line.StartsWith("[明日方舟]", StringComparison.Ordinal)).ToArray());
        Assert.IsTrue(result.Lines.Any(line => line.Contains("超时", StringComparison.Ordinal)));
        Assert.IsTrue(result.Lines.Any(line => line.Contains("重试", StringComparison.Ordinal) && line.Contains('3')));
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.DoesNotContain("Operation canceled", string.Join('\n', result.Lines));
    }

    [TestMethod]
    public async Task ExhaustedHttpFailureDoesNotBlockAnotherGameOrLeakResponse()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"), EndfieldRole(12));
        var arknightsRequests = 0;
        var endfieldRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/game/attendance")
            {
                arknightsRequests++;
                return Task.FromResult(Json("private upstream response", HttpStatusCode.ServiceUnavailable));
            }

            endfieldRequests++;
            return Task.FromResult(Json("""{"code":0,"data":{"hasToday":true}}"""));
        });

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        Assert.AreEqual(4, arknightsRequests);
        Assert.AreEqual(1, endfieldRequests);
        Assert.Contains("[终末地] Role12（uid-12）", result.Lines);
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.DoesNotContain("private upstream response", string.Join('\n', result.Lines));
    }

    [TestMethod]
    public async Task RetryingLaterRoleDoesNotRepeatAnEarlierSuccess()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"), EndfieldRole(12));
        var arknightsRequests = 0;
        var endfieldRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/game/attendance")
            {
                arknightsRequests++;
                return Task.FromResult(ArknightsAlreadySigned());
            }

            endfieldRequests++;
            return Task.FromResult(endfieldRequests < 3
                ? Json("""{"code":10003,"message":"服务暂不可用"}""")
                : Json("""{"code":0,"data":{"hasToday":true}}"""));
        });

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(account);

        Assert.AreEqual(1, arknightsRequests);
        Assert.AreEqual(3, endfieldRequests);
        Assert.AreEqual(2, result.Lines.Count(line => line == "签到结果：今日已签到"));
        Assert.IsFalse(result.Lines.Any(line => line.Contains("执行失败", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PostTimeoutChecksStateAndDoesNotSubmitSignTwice()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"));
        var signed = false;
        var statusRequests = 0;
        var signRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                signRequests++;
                signed = true;
                throw new TaskCanceledException("response lost after attendance completed", new TimeoutException());
            }

            statusRequests++;
            return Task.FromResult(signed
                ? ArknightsAlreadySigned()
                : Json("""{"code":0,"data":{"records":[],"awards":[]}}"""));
        });

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        Assert.AreEqual(2, statusRequests);
        Assert.AreEqual(1, signRequests);
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.IsFalse(result.Lines.Any(line => line.Contains("执行失败", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CredExpiryPreservesEarlierSuccessAndDoesNotRetryInvalidCredentials()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db,
            ArknightsRole(11, "first"), ArknightsRole(12, "second"), ArknightsRole(13, "third"));
        var requests = new List<string>();
        var handler = new DelegateHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.Query);
            return Task.FromResult(request.RequestUri.Query.Contains("uid=first", StringComparison.Ordinal)
                ? ArknightsAlreadySigned()
                : Json("""{"code":-10002,"message":"cred expired"}"""));
        });

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(account);

        Assert.HasCount(2, requests);
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.AreEqual(2, result.Lines.Count(line => line.Contains("请重新绑定账号", StringComparison.Ordinal)));
        Assert.IsEmpty(account.CredCiphertext);
        Assert.IsEmpty(account.SignTokenCiphertext);
        Assert.IsEmpty((await db.SklandAccounts.FindAsync(account.Id))!.CredCiphertext);
    }

    [TestMethod]
    public async Task EndfieldPostTimeoutChecksStateBeforeAnyResubmission()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, EndfieldRole(11));
        var signed = false;
        var statusRequests = 0;
        var signRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                signRequests++;
                signed = true;
                throw new TaskCanceledException("response lost after attendance completed", new TimeoutException());
            }

            statusRequests++;
            return Task.FromResult(Json(signed
                ? """{"code":0,"data":{"hasToday":true}}"""
                : """{"code":0,"data":{"hasToday":false}}"""));
        });

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        Assert.AreEqual(2, statusRequests);
        Assert.AreEqual(1, signRequests);
        Assert.Contains("签到结果：今日已签到", result.Lines);
        Assert.IsFalse(result.Lines.Any(line => line.Contains("执行失败", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReauthenticationUsesNewCredForRetriedRequestAndFollowingRole()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"), EndfieldRole(12));
        var attendanceCreds = new List<string>();
        var refreshRequests = 0;
        var grantRequests = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/web/v1/auth/refresh":
                    refreshRequests++;
                    return Task.FromResult(Json("""{"code":-10002,"message":"expired"}"""));
                case "/user/oauth2/v2/grant":
                    grantRequests++;
                    return Task.FromResult(Json("""{"code":0,"data":{"code":"grant-code"}}"""));
                case "/web/v1/user/auth/generate_cred_by_code":
                    return Task.FromResult(Json("""{"code":0,"data":{"cred":"new-cred","token":"new-sign-token","userId":"skland-user"}}"""));
                case "/api/v1/game/attendance":
                    attendanceCreds.Add(request.Headers.GetValues("cred").Single());
                    return Task.FromResult(attendanceCreds.Count == 1
                        ? Json("""{"code":-10001,"message":"token expired"}""")
                        : ArknightsAlreadySigned());
                case "/api/v1/game/endfield/attendance":
                    attendanceCreds.Add(request.Headers.GetValues("cred").Single());
                    return Task.FromResult(Json("""{"code":0,"data":{"hasToday":true}}"""));
                default:
                    throw new InvalidOperationException("Unexpected request: " + request.RequestUri.AbsolutePath);
            }
        });

        var result = await CreateService(db, handler).ExecuteGameSignAsync(account);

        Assert.AreEqual(1, refreshRequests);
        Assert.AreEqual(1, grantRequests);
        CollectionAssert.AreEqual(new[] { "cred", "new-cred", "new-cred" }, attendanceCreds);
        Assert.AreEqual(2, result.Lines.Count(line => line == "签到结果：今日已签到"));
        Assert.AreEqual("new-cred", account.CredCiphertext);
    }

    [TestMethod]
    public async Task ExternalCancellationStopsWithoutRetryingOrProcessingNextRole()
    {
        await using var db = CreateDbContext();
        var account = await StoreAccountAsync(db, ArknightsRole(11, "first"), ArknightsRole(12, "second"));
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        var handler = new DelegateHandler((_, token) =>
        {
            requests++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new AssertFailedException("The canceled request should have thrown.");
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateService(db, handler).ExecuteAutoSignAsync(account, cancellation.Token));

        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task UnconfiguredOrDisabledRolesDoNotSendRequests()
    {
        await using var db = CreateDbContext();
        var missingRole = EndfieldRole(11);
        missingRole.RoleId = string.Empty;
        var disabledRole = ArknightsRole(12, "second");
        disabledRole.AutoSignEnabled = false;
        var account = await StoreAccountAsync(db, missingRole, disabledRole);
        var requests = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            requests++;
            throw new AssertFailedException("These roles should not call the API.");
        });

        var result = await CreateService(db, handler).ExecuteAutoSignAsync(account);

        Assert.AreEqual(0, requests);
        Assert.IsTrue(result.Lines.Any(line => line.Contains("缺少 roleId/serverId", StringComparison.Ordinal)));
        Assert.DoesNotContain("[明日方舟] Role12（second）", result.Lines);
    }

    private static SklandSignService CreateService(SklandDbContext db, HttpMessageHandler handler)
    {
        var client = new SklandHttpClient(new HttpClient(handler), Options.Create(new SklandOptions
        {
            HgBaseUrl = "https://hg.test",
            SklandBaseUrl = "https://skland.test"
        }));
        var accountService = new SklandAccountService(
            db, client, new RejectDeviceIdProvider(), new PlainSecretProtector(), TimeProvider.System);
        return new SklandSignService(client, accountService, NullLogger<SklandSignService>.Instance,
            new SignInExecutor(NullLogger<SignInExecutor>.Instance, retryDelay: TimeSpan.Zero));
    }

    private static SklandDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SklandDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<SklandAccount> StoreAccountAsync(SklandDbContext db, params SklandGameRole[] roles)
    {
        var account = new SklandAccount
        {
            Id = 7,
            CoreUserId = 1,
            SklandUserId = "skland-user",
            DisplayName = "Skland User",
            DeviceId = "B" + new string('a', 88),
            HgTokenCiphertext = "hg-token",
            CredCiphertext = "cred",
            SignTokenCiphertext = "sign-token",
            AutoSignEnabled = true,
            Roles = roles.ToList()
        };
        db.SklandAccounts.Add(account);
        await db.SaveChangesAsync();
        return await db.SklandAccounts.AsNoTracking().Include(item => item.Roles).SingleAsync();
    }

    private static SklandGameRole ArknightsRole(long id, string uid) => new()
    {
        Id = id,
        SklandAccountId = 7,
        GameId = SklandGameNames.Arknights,
        GameName = "明日方舟",
        NickName = "Role" + id,
        Uid = uid,
        AutoSignEnabled = true
    };

    private static SklandGameRole EndfieldRole(long id) => new()
    {
        Id = id,
        SklandAccountId = 7,
        GameId = SklandGameNames.Endfield,
        GameName = "终末地",
        NickName = "Role" + id,
        Uid = "uid-" + id,
        RoleId = "role-" + id,
        ServerId = "server",
        AutoSignEnabled = true
    };

    private static HttpResponseMessage ArknightsAlreadySigned() => Json(JsonSerializer.Serialize(new
    {
        code = 0,
        data = new
        {
            records = new[] { new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } },
            awards = Array.Empty<object>()
        }
    }));

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class RejectDeviceIdProvider : ISklandDeviceIdProvider
    {
        public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("A valid official device ID must be retained.");
    }

    private sealed class PlainSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
