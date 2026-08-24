# PlaywrightProvider

PlaywrightProvider 是供其他 OhMyBot 插件使用的浏览器基础设施插件。它没有用户指令，负责延迟启动并共享一个 Playwright Chromium 进程。

## 提供的能力

- 通过插件导出提供 `IPlaywrightProvider`。
- `UseBrowserAsync` 允许消费方使用共享浏览器实例。
- `UseContextAsync` 和 `UsePageAsync` 为每次调用创建并自动释放独立的浏览器上下文或页面。
- 浏览器断开后会在下一次调用时重新初始化，插件卸载时会释放共享进程。

消费插件应调用 `AddPlaywrightProviderClient()` 注册客户端，并声明对插件 ID `com.ohmybot.playwright-provider` 的依赖。直接使用 `UseBrowserAsync` 时，消费方必须释放自己创建的上下文和页面，不得关闭共享浏览器。

Skland 使用本插件运行森空岛官方网页 SDK，以生成设备标识。

## 浏览器选择与打包

浏览器按以下顺序选择：

1. `BrowserExecutablePath` 指定的可执行文件。
2. macOS 或 Linux 上已知路径中的 Chrome/Chromium。
3. `BrowserChannel` 指定的 Playwright 浏览器通道。

Release 包含 `linux-arm64` Playwright 驱动；Debug 包还包含 `osx-arm64` 驱动。浏览器本体不随插件提交，部署环境需要提供对应浏览器或另行安装 Playwright 浏览器。

## 配置

配置模板为 [pluginsettings.template.json](pluginsettings.template.json)：

- `Playwright.Headless`：是否使用无头模式，默认 `true`。
- `Playwright.BrowserChannel`：找不到系统浏览器且未指定路径时使用的通道，默认 `chrome`。
- `Playwright.BrowserExecutablePath`：浏览器可执行文件的显式路径，默认留空。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build PlaywrightProvider/OhMyBot.Plugins.PlaywrightProvider.csproj
dotnet test tests/OhMyBot.Plugins.PlaywrightProvider.Tests/OhMyBot.Plugins.PlaywrightProvider.Tests.csproj
```
