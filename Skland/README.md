# Skland

Skland 是森空岛账号与游戏签到插件，支持 QQ 和 Telegram。所有指令仅限私聊，要求用户至少具有 `VerifiedUser` 权限。

## 功能

- 使用鹰角网络 OAuth Token 绑定并管理多个森空岛账号。
- 同步《明日方舟》和《明日方舟：终末地》角色。
- 手动或定时执行游戏签到。
- 按账号和游戏角色配置自动签到。
- 通过 `/notify` 按账号订阅自动签到结果。

## 指令

| 指令 | 说明 |
| --- | --- |
| `/skland bind <token>` | 绑定森空岛账号 |
| `/skland list` | 查看已绑定账号和角色 |
| `/skland game init [accountId]` | 同步账号的游戏角色 |
| `/skland game signin [accountId] [arknights\|endfield\|all]` | 执行游戏签到 |
| `/skland autosign` | 管理自动签到 |
| `/skland delete` | 删除账号绑定 |

Token 由 Core 的密钥保护服务加密保存。绑定成功后会为当前会话启用该账号的通知订阅，之后可通过 `/notify` 调整。

## 浏览器依赖

插件依赖 [PlaywrightProvider](../PlaywrightProvider/README.md)，通过森空岛官方网页 SDK 生成设备标识。运行环境必须同时部署 `PlaywrightProvider`，并提供可用的 Chrome 或 Chromium；初始化失败时应先检查该插件的浏览器配置。

## 配置

配置模板为 [pluginsettings.template.json](pluginsettings.template.json)：

- `Skland`：鹰角与森空岛 API 地址、客户端标识、User-Agent、请求超时和设备标识生成超时。
- `ScheduledTask.Enabled`：是否启用自动签到定时任务。
- `ScheduledTask.Cron`：定时任务 Cron 表达式，默认 `10 0 * * *`。

运行时配置使用 `pluginsettings.json`，不得提交真实 Token。插件还依赖 Host 的 PostgreSQL 连接、密钥保护、通知和计划任务服务。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build Skland/OhMyBot.Plugins.Skland.csproj
dotnet test tests/OhMyBot.Plugins.Skland.Tests/OhMyBot.Plugins.Skland.Tests.csproj
```
