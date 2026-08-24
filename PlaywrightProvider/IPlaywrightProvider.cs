using Microsoft.Playwright;

namespace OhMyBot.Plugins.PlaywrightProvider;

/// <summary>Provides access to the shared Playwright runtime and browser process.</summary>
public interface IPlaywrightProvider
{
    /// <summary>
    /// Runs a callback with the shared browser. The callback must not close the supplied browser and
    /// must dispose any contexts, pages, or other resources it creates.
    /// </summary>
    Task<TResult> UseBrowserAsync<TResult>(
        Func<IPlaywright, IBrowser, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);
}
