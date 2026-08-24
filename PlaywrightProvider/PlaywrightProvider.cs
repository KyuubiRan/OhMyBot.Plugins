using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace OhMyBot.Plugins.PlaywrightProvider;

public sealed class SharedPlaywrightProvider(
    IOptions<PlaywrightProviderOptions> options,
    ILogger<SharedPlaywrightProvider> logger) : IPlaywrightProvider, IAsyncDisposable
{
    private readonly PlaywrightProviderOptions _options = options.Value;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    public async Task<TResult> UseBrowserAsync<TResult>(
        Func<IPlaywright, IBrowser, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (playwright, browser) = await GetBrowserAsync(cancellationToken);
        return await action(playwright, browser, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposeBrowserAsync();
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<(IPlaywright Playwright, IBrowser Browser)> GetBrowserAsync(
        CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playwright is not null && _browser is { IsConnected: true })
            {
                return (_playwright, _browser);
            }

            await DisposeBrowserAsync();
            var playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken);
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(CreateLaunchOptions())
                    .WaitAsync(cancellationToken);
                _playwright = playwright;
                _browser = browser;
                logger.LogInformation("Started the shared Playwright browser process.");
                return (playwright, browser);
            }
            catch
            {
                playwright.Dispose();
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask DisposeBrowserAsync()
    {
        var browser = Interlocked.Exchange(ref _browser, null);
        var playwright = Interlocked.Exchange(ref _playwright, null);
        try
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }
        }
        finally
        {
            playwright?.Dispose();
        }
    }

    private BrowserTypeLaunchOptions CreateLaunchOptions()
    {
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless
        };

        if (!string.IsNullOrWhiteSpace(_options.BrowserExecutablePath))
        {
            launchOptions.ExecutablePath = _options.BrowserExecutablePath.Trim();
        }
        else if (FindSystemBrowserExecutable() is { } systemBrowser)
        {
            launchOptions.ExecutablePath = systemBrowser;
        }
        else if (!string.IsNullOrWhiteSpace(_options.BrowserChannel))
        {
            launchOptions.Channel = _options.BrowserChannel.Trim();
        }

        return launchOptions;
    }

    private static string? FindSystemBrowserExecutable()
    {
        string[] candidates = OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Chromium.app/Contents/MacOS/Chromium"
            ]
            : OperatingSystem.IsLinux()
                ?
                [
                    "/usr/bin/google-chrome",
                    "/usr/bin/google-chrome-stable",
                    "/usr/bin/chromium",
                    "/usr/bin/chromium-browser"
                ]
                : [];
        return candidates.FirstOrDefault(File.Exists);
    }
}
