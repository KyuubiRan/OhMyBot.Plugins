namespace OhMyBot.Plugins.PlaywrightProvider;

public sealed class PlaywrightProviderOptions
{
    public bool Headless { get; set; } = true;

    public string BrowserChannel { get; set; } = "chrome";

    public string BrowserExecutablePath { get; set; } = string.Empty;
}
