using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using OhMyBot.Plugins.PlaywrightProvider;

namespace OhMyBot.Core.Integrations.Skland;

public interface ISklandDeviceIdProvider
{
    Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);
}

public sealed class PlaywrightSklandDeviceIdProvider(
    IPlaywrightProvider playwrightProvider,
    IOptions<SklandOptions> options,
    ILogger<PlaywrightSklandDeviceIdProvider> logger) : ISklandDeviceIdProvider
{
    private const string DeviceReadyExpression = """
        () => {
            const sdk = window.SMSdk;
            if (!sdk || typeof sdk.getDeviceId !== "function") {
                return false;
            }

            const value = sdk.getDeviceId();
            return typeof value === "string" && value.startsWith("B");
        }
        """;

    private const string ReadDeviceIdExpression = "() => window.SMSdk.getDeviceId()";

    private readonly SklandOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetDeviceIdCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> GetDeviceIdCoreAsync(CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = (float)Math.Clamp(
            _options.DeviceIdTimeout.TotalMilliseconds,
            TimeSpan.FromSeconds(5).TotalMilliseconds,
            TimeSpan.FromMinutes(2).TotalMilliseconds);

        try
        {
            var deviceId = await playwrightProvider.UsePageAsync(async (page, token) =>
            {
                await page.GotoAsync(_options.WebBaseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = timeoutMilliseconds
                }).WaitAsync(token);
                await page.WaitForFunctionAsync(DeviceReadyExpression, null, new PageWaitForFunctionOptions
                {
                    Timeout = timeoutMilliseconds,
                    PollingInterval = 250
                }).WaitAsync(token);

                return await page.EvaluateAsync<string>(ReadDeviceIdExpression).WaitAsync(token);
            }, new BrowserNewContextOptions
            {
                UserAgent = _options.WebUserAgent
            }, timeoutMilliseconds, cancellationToken);
            if (!SklandDeviceId.IsOfficial(deviceId))
            {
                throw new SklandDeviceIdException("森空岛设备 SDK 返回了无效的设备标识。");
            }

            logger.LogInformation("Generated a Skland device ID through the official browser SDK.");
            return deviceId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SklandDeviceIdException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to generate a Skland device ID through the browser SDK.");
            throw new SklandDeviceIdException(
                "无法初始化森空岛设备校验，请联系管理员检查 Chrome/Playwright 配置。",
                exception);
        }
    }

}

public static class SklandDeviceId
{
    public const int OfficialLength = 89;

    public static bool IsOfficial(string? value)
    {
        return value is { Length: OfficialLength }
               && value[0] == 'B'
               && value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
    }

    public static bool IsLegacy(string? value)
    {
        return value is { Length: 32 } && value.All(Uri.IsHexDigit);
    }

    public static string ToHeaderValue(string value)
    {
        return IsLegacy(value) ? "B" + value : value;
    }
}

public sealed class SklandDeviceIdException : Exception
{
    public SklandDeviceIdException(string message) : base(message)
    {
    }

    public SklandDeviceIdException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
