namespace OhMyBot.Core.Integrations.Skland;

public sealed class SklandOptions
{
    public string HgBaseUrl { get; set; } = "https://as.hypergryph.com";

    public string SklandBaseUrl { get; set; } = "https://zonai.skland.com";

    public string WebBaseUrl { get; set; } = "https://www.skland.com/";

    /// <summary>森空岛 OAuth app code。</summary>
    public string AppCode { get; set; } = "4ca99fa6b56cc2ba";

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Linux; Android 12; SM-A5560 Build/V417IR; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/129.0.6668.100 Mobile Safari/537.36 SKLand/1.52.1";

    /// <summary>官方 Web 设备 SDK 与绑定请求共同使用的浏览器 User-Agent。</summary>
    public string WebUserAgent { get; set; } =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

    public string VName { get; set; } = "1.0.0";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan DeviceIdTimeout { get; set; } = TimeSpan.FromSeconds(45);
}
