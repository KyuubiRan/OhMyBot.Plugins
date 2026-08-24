# Mihoyo

Mihoyo 是米游社与 HoYoLAB 账号、社区任务和游戏签到插件，支持 QQ 和 Telegram。所有指令仅限私聊，要求用户至少具有 `VerifiedUser` 权限。

## 功能

- 绑定并管理多个米游社或 HoYoLAB 账号，自动识别国服与国际服。
- 执行国服米游社签到、浏览、点赞和分享任务。
- 同步游戏角色并执行国服或国际服签到。
- 按账号、社区任务和游戏角色配置自动签到。
- 通过 `/notify` 按账号订阅自动签到结果。

当前游戏签到范围：

- 国服与国际服：《原神》《崩坏：星穹铁道》《绝区零》《崩坏3》《未定事件簿》。
- 仅国服：《崩坏学园2》。

## 指令

| 指令 | 说明 |
| --- | --- |
| `/mihoyo bind <cookie>` | 绑定米游社或 HoYoLAB 账号 |
| `/mihoyo list` | 查看已绑定账号和角色 |
| `/mihoyo signin [accountId] [signin\|view\|like\|share ...]` | 执行国服米游社任务 |
| `/mihoyo game init [accountId]` | 同步账号的游戏角色 |
| `/mihoyo game signin [accountId] [genshin\|sr\|zzz\|honkai3\|themis\|honkai2\|all]` | 执行游戏签到 |
| `/mihoyo autosign` | 管理自动签到 |
| `/mihoyo delete` | 删除账号绑定 |

国服游戏签到至少需要 `cookie_token`；如含 `stoken`，插件可续期并执行米游社社区任务。国际服需要 `ltoken`。Cookie 由 Core 的密钥保护服务加密保存，绑定成功后会为当前会话启用该账号的通知订阅。

## 配置

配置模板为 [pluginsettings.template.json](pluginsettings.template.json)：

- `Mihoyo`：客户端版本、签名参数、社区任务参数和请求超时。
- `ScheduledTask.Enabled`：是否启用自动签到定时任务。
- `ScheduledTask.Cron`：定时任务 Cron 表达式，默认 `10 0 * * *`。

运行时配置使用 `pluginsettings.json`，不得提交真实 Cookie。插件还依赖 Host 的 PostgreSQL 连接、密钥保护、通知和计划任务服务。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build Mihoyo/OhMyBot.Plugins.Mihoyo.csproj
dotnet test tests/OhMyBot.Plugins.Mihoyo.Tests/OhMyBot.Plugins.Mihoyo.Tests.csproj
```
