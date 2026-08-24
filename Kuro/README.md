# Kuro

Kuro 是库街区账号与签到插件，支持 QQ 和 Telegram。所有指令仅限私聊，要求用户至少具有 `VerifiedUser` 权限。

## 功能

- 绑定并管理多个库街区账号，Token 由 Core 的密钥保护服务加密保存。
- 执行社区签到、浏览、点赞和分享任务。
- 同步《鸣潮》和《战双帕弥什》角色并执行游戏签到。
- 按账号、社区任务和游戏角色配置自动签到。
- 通过 `/notify` 按账号订阅自动签到结果。

## 指令

| 指令 | 说明 |
| --- | --- |
| `/kuro bind <token> [devCode] [distinctId]` | 绑定库街区账号；风控需要时可附加设备参数 |
| `/kuro list` | 查看已绑定账号和角色 |
| `/kuro signin [accountId] [signin\|view\|like\|share ...]` | 执行社区任务 |
| `/kuro game init [accountId]` | 同步账号的游戏角色 |
| `/kuro game signin [accountId] [wuwa\|pgr\|all]` | 执行游戏签到 |
| `/kuro autosign` | 管理自动签到 |
| `/kuro delete` | 删除账号绑定 |

不带账号 ID 时，单账号会直接使用；多账号会显示选择菜单。绑定成功后会为当前会话启用该账号的自动签到通知订阅，之后可通过 `/notify` 调整。

## 配置

配置模板为 [pluginsettings.template.json](pluginsettings.template.json)：

- `Kuro`：库街区 API 地址、客户端标识、设备默认值和请求超时。
- `ScheduledTask.Enabled`：是否启用自动签到定时任务。
- `ScheduledTask.Cron`：定时任务 Cron 表达式，默认 `10 0 * * *`。

运行时配置使用 `pluginsettings.json`，不得提交真实 Token 或设备信息。插件还依赖 Host 的 PostgreSQL 连接、密钥保护、通知和计划任务服务。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build Kuro/OhMyBot.Plugins.Kuro.csproj
dotnet test tests/OhMyBot.Plugins.Kuro.Tests/OhMyBot.Plugins.Kuro.Tests.csproj
```
