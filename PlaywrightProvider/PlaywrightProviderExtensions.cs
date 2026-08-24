using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OhMyBot.Plugin.Abstractions;

namespace OhMyBot.Plugins.PlaywrightProvider;

public static class PlaywrightProviderExtensions
{
    public static IServiceCollection AddPlaywrightProviderClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPlaywrightProvider, PlaywrightProviderClient>();
        return services;
    }

    public static Task<TResult> UseContextAsync<TResult>(
        this IPlaywrightProvider provider,
        Func<IBrowserContext, CancellationToken, Task<TResult>> action,
        BrowserNewContextOptions? contextOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(action);
        return provider.UseBrowserAsync(async (_, browser, token) =>
        {
            await using var context = await browser.NewContextAsync(contextOptions).WaitAsync(token);
            return await action(context, token);
        }, cancellationToken);
    }

    public static Task<TResult> UsePageAsync<TResult>(
        this IPlaywrightProvider provider,
        Func<IPage, CancellationToken, Task<TResult>> action,
        BrowserNewContextOptions? contextOptions = null,
        float? defaultTimeoutMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(action);
        return provider.UseContextAsync(async (context, token) =>
        {
            var page = await context.NewPageAsync().WaitAsync(token);
            if (defaultTimeoutMilliseconds is { } timeout)
            {
                page.SetDefaultTimeout(timeout);
                page.SetDefaultNavigationTimeout(timeout);
            }

            return await action(page, token);
        }, contextOptions, cancellationToken);
    }

}

internal sealed class PlaywrightProviderClient(
    IPluginHostServices hostServices) : IPlaywrightProvider
{
    public Task<TResult> UseBrowserAsync<TResult>(
        Func<IPlaywright, IBrowser, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        var provider = hostServices.GetExport<IPlaywrightProvider>(PlaywrightProviderPlugin.PluginId);
        return provider.UseBrowserAsync(action, cancellationToken);
    }
}
