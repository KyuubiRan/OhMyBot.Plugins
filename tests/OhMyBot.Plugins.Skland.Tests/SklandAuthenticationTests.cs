using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Security;
using OhMyBot.Core.Integrations.Skland;
using OhMyBot.Plugins.PlaywrightProvider;
using OhMyBot.Plugins.Skland.Data;

namespace OhMyBot.Plugins.Skland.Tests;

[TestClass]
public sealed class SklandAuthenticationTests
{
    private const string WebUserAgent = "Skland-Web-Test/1.0";
    private static readonly string DeviceA = "B" + new string('a', 88);
    private static readonly string DeviceB = "B" + new string('b', 88);

    [TestMethod]
    public async Task BindRegeneratesDeviceAndRetriesAfterDeviceInvalidResponse()
    {
        await using var dbContext = CreateDbContext();
        var handler = new SklandApiHandler(rejectFirstDevice: true);
        var devices = new QueuedDeviceIdProvider(DeviceA, DeviceB);
        var service = CreateAccountService(dbContext, handler, devices);

        var result = await service.BindAsync(7, "hg-token");

        Assert.AreEqual(DeviceB, result.Account.DeviceId);
        Assert.AreEqual("Skland User", result.Account.DisplayName);
        Assert.AreEqual(2, devices.CallCount);
        Assert.AreEqual(2, handler.GrantCount);
        Assert.AreEqual(2, handler.GenerateCredCount);
        CollectionAssert.AreEqual(new[] { DeviceA, DeviceB }, handler.GenerateCredDeviceIds.ToArray());
        Assert.IsTrue(handler.GenerateCredTimestamps.All(IsCurrentUnixTimestamp));
        Assert.IsTrue(handler.GenerateCredUserAgents.All(userAgent => userAgent == WebUserAgent));
        Assert.IsTrue(handler.GenerateCredReferrers.All(referer => referer == "https://www.skland.com/"));
        Assert.IsTrue(handler.GenerateCredRequestedWith.All(string.IsNullOrEmpty));
        Assert.IsTrue(handler.LastSignedSignatureValid);
    }

    [TestMethod]
    public async Task RebindUpdatesExistingAccountWithOfficialDeviceId()
    {
        await using var dbContext = CreateDbContext();
        dbContext.SklandAccounts.Add(new SklandAccount
        {
            CoreUserId = 7,
            SklandUserId = "skland-user",
            DeviceId = "0123456789abcdef0123456789abcdef",
            DisplayName = "Old Name",
            HgTokenCiphertext = "old-hg",
            CredCiphertext = "old-cred",
            SignTokenCiphertext = "old-sign"
        });
        await dbContext.SaveChangesAsync();

        var handler = new SklandApiHandler();
        var devices = new QueuedDeviceIdProvider(DeviceA);
        var service = CreateAccountService(dbContext, handler, devices);

        var result = await service.BindAsync(7, "new-hg-token");

        Assert.IsTrue(result.UpdatedExisting);
        Assert.AreEqual(DeviceA, result.Account.DeviceId);
        Assert.AreEqual("new-hg-token", result.Account.HgTokenCiphertext);
    }

    [TestMethod]
    public async Task SignedRequestsKeepLegacyDeviceHeaderCompatibility()
    {
        var handler = new SklandApiHandler();
        var client = CreateHttpClient(handler);
        const string legacyDeviceId = "0123456789abcdef0123456789abcdef";

        var response = await client.GetBindingAsync("sign-token", "cred", legacyDeviceId);

        Assert.IsTrue(response.IsOk);
        Assert.AreEqual("B" + legacyDeviceId, handler.LastSignedDeviceId);
        Assert.AreEqual("com.hypergryph.skland", handler.LastSignedRequestedWith);
        Assert.IsTrue(handler.LastSignedSignatureValid);
    }

    [TestMethod]
    public void DeviceIdClassificationAcceptsOnlyExpectedOfficialAndLegacyForms()
    {
        Assert.IsTrue(SklandDeviceId.IsOfficial(DeviceA));
        Assert.IsFalse(SklandDeviceId.IsOfficial("B" + new string('a', 87)));
        Assert.IsTrue(SklandDeviceId.IsLegacy("0123456789abcdef0123456789abcdef"));
        Assert.IsFalse(SklandDeviceId.IsLegacy("not-a-device-id"));
        Assert.AreEqual(DeviceA, SklandDeviceId.ToHeaderValue(DeviceA));
    }

    [TestMethod]
    [TestCategory("Live")]
    public async Task OfficialSdkReturnsValidDeviceIdInHeadlessChrome()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("OHMYBOT_SKLAND_LIVE_DEVICE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set OHMYBOT_SKLAND_LIVE_DEVICE_TEST=1 to run the live browser SDK check.");
        }

        await using var playwrightProvider = new SharedPlaywrightProvider(
            Options.Create(new PlaywrightProviderOptions()),
            NullLogger<SharedPlaywrightProvider>.Instance);
        var provider = new PlaywrightSklandDeviceIdProvider(
            playwrightProvider,
            Options.Create(new SklandOptions()),
            NullLogger<PlaywrightSklandDeviceIdProvider>.Instance);

        var deviceId = await provider.GetDeviceIdAsync();

        Assert.IsTrue(SklandDeviceId.IsOfficial(deviceId));
    }

    private static SklandAccountService CreateAccountService(
        SklandDbContext dbContext,
        HttpMessageHandler handler,
        ISklandDeviceIdProvider deviceIdProvider)
    {
        return new SklandAccountService(
            dbContext,
            CreateHttpClient(handler),
            deviceIdProvider,
            new PlainSecretProtector(),
            TimeProvider.System);
    }

    private static SklandHttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new SklandHttpClient(
            new HttpClient(handler),
            Options.Create(new SklandOptions
            {
                HgBaseUrl = "https://hg.test",
                SklandBaseUrl = "https://skland.test",
                WebUserAgent = WebUserAgent
            }));
    }

    private static SklandDbContext CreateDbContext()
    {
        return new SklandDbContext(new DbContextOptionsBuilder<SklandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    private static bool IsCurrentUnixTimestamp(string value)
    {
        return long.TryParse(value, out var timestamp)
               && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) <= 5;
    }

    private sealed class QueuedDeviceIdProvider(params string[] deviceIds) : ISklandDeviceIdProvider
    {
        private readonly Queue<string> _deviceIds = new(deviceIds);

        public int CallCount { get; private set; }

        public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_deviceIds.Dequeue());
        }
    }

    private sealed class PlainSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class SklandApiHandler(bool rejectFirstDevice = false) : HttpMessageHandler
    {
        public int GrantCount { get; private set; }

        public int GenerateCredCount { get; private set; }

        public List<string> GenerateCredDeviceIds { get; } = [];

        public List<string> GenerateCredTimestamps { get; } = [];

        public List<string> GenerateCredUserAgents { get; } = [];

        public List<string> GenerateCredReferrers { get; } = [];

        public List<string> GenerateCredRequestedWith { get; } = [];

        public string LastSignedDeviceId { get; private set; } = string.Empty;

        public string LastSignedRequestedWith { get; private set; } = string.Empty;

        public bool LastSignedSignatureValid { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/user/oauth2/v2/grant" => Task.FromResult(Grant()),
                "/web/v1/user/auth/generate_cred_by_code" => Task.FromResult(GenerateCred(request)),
                "/api/v1/game/player/binding" => Task.FromResult(Binding(request)),
                "/api/v1/user/me" => Task.FromResult(UserInfo()),
                _ => throw new InvalidOperationException("Unexpected Skland test request: " + path)
            };
        }

        private HttpResponseMessage Grant()
        {
            GrantCount++;
            return Json("""{"code":0,"message":"","data":{"code":"grant-code"}}""");
        }

        private HttpResponseMessage GenerateCred(HttpRequestMessage request)
        {
            GenerateCredCount++;
            GenerateCredDeviceIds.Add(Header(request, "dId"));
            GenerateCredTimestamps.Add(Header(request, "timestamp"));
            GenerateCredUserAgents.Add(Header(request, "User-Agent"));
            GenerateCredReferrers.Add(request.Headers.Referrer?.AbsoluteUri ?? string.Empty);
            GenerateCredRequestedWith.Add(Header(request, "x-requested-with"));
            if (rejectFirstDevice && GenerateCredCount == 1)
            {
                return Json("""{"code":10001,"message":"设备信息无效","data":null}""");
            }

            return Json("""{"code":0,"message":"","data":{"token":"sign-token","cred":"cred","userId":"skland-user"}}""");
        }

        private HttpResponseMessage Binding(HttpRequestMessage request)
        {
            LastSignedDeviceId = Header(request, "dId");
            LastSignedRequestedWith = Header(request, "x-requested-with");
            LastSignedSignatureValid = Header(request, "sign") == ComputeExpectedSign(
                "sign-token",
                request.RequestUri!.AbsolutePath,
                Header(request, "timestamp"),
                LastSignedDeviceId);
            return Json("""{"code":0,"message":"","data":{"list":[]}}""");
        }

        private static string ComputeExpectedSign(
            string signToken,
            string path,
            string timestamp,
            string deviceId)
        {
            var signHeaderJson =
                $"{{\"platform\":\"3\",\"timestamp\":\"{timestamp}\",\"dId\":\"{deviceId}\",\"vName\":\"1.0.0\"}}";
            var message = path + timestamp + signHeaderJson;
            var hmac = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(signToken),
                Encoding.UTF8.GetBytes(message));
            var hmacHex = Convert.ToHexString(hmac).ToLowerInvariant();
            var md5 = MD5.HashData(Encoding.UTF8.GetBytes(hmacHex));
            return Convert.ToHexString(md5).ToLowerInvariant();
        }

        private static HttpResponseMessage UserInfo()
        {
            return Json("""{"code":0,"message":"","data":{"user":{"nickname":"Skland User","id":"skland-user"}}}""");
        }

        private static string Header(HttpRequestMessage request, string name)
        {
            return request.Headers.TryGetValues(name, out var values) ? values.Single() : string.Empty;
        }

        private static HttpResponseMessage Json(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
