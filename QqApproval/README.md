# QqApproval

QqApproval 是 QQ 专用的请求审批插件。它把需要人工审批的加好友、邀请进群和入群申请转给有权限且已订阅的用户，并通过编号菜单完成同意或拒绝。

## 请求与权限

- `ApprovalRequiredPrivilege` 控制查看请求、执行审批和管理自动规则所需的最低权限，默认 `owner`。
- `RequestTypes` 分别控制三类请求是否接入，以及用户在 `/notify` 中订阅该类型所需的最低权限。
- 默认接入加好友与邀请进群；入群申请默认关闭。
- 自动黑白名单默认关闭；开启前，已接入的请求一律进入人工审批。

有对应类型权限的用户可在 QQ 私聊执行 `/notify`，从 `Bot消息通知` 分类分别开启或关闭自己的好友申请、群邀请和入群申请通知。无目标权限的类型不会显示，菜单回调执行时也会重新校验当前权限。

## 指令

`/qqreq` 仅支持 QQ 私聊，并要求达到 `ApprovalRequiredPrivilege`：

| 指令 | 说明 |
| --- | --- |
| `/qqreq` | 查看审批接入、权限、自动规则和待审数量 |
| `/qqreq list` | 查看待审批请求并同意或拒绝 |
| `/qqreq recent` | 查看最近已处理的请求 |
| `/qqreq rules [on\|off <类型>]` | 查看或开关指定类型的自动黑白名单 |
| `/qqreq allow <类型> <user\|group> <号码> [备注]` | 添加自动同意规则 |
| `/qqreq deny <类型> <user\|group> <号码> [备注]` | 添加自动拒绝规则 |
| `/qqreq ruledel <规则id>` | 删除规则 |

请求类型使用 `friend`、`invite` 或 `groupadd`。通知包含申请人昵称、性别、年龄、QQ 等级和头像；网关无法补齐资料时只显示 QQ 号。昵称和附言均按外部输入处理，渲染前会做 CQ 码转义。

## 配置

配置模板为 [pluginsettings.template.json](pluginsettings.template.json)：

- `QqApproval.ApprovalRequiredPrivilege`：审批和规则管理权限。
- `QqApproval.RequestTypes.<类型>.Enabled`：是否接入该请求类型。
- `QqApproval.RequestTypes.<类型>.RequiredPrivilege`：订阅该类型通知所需权限。
- `QqApproval.PendingTtl`：推送菜单及回调数据有效期，默认 24 小时；过期后可通过 `/qqreq list` 重新操作。
- `QqApproval.RejectReason`：拒绝群请求时返回给申请人的理由。

插件依赖 Host 的 PostgreSQL 连接。网关通过 `ReportPlatformRequest` 向 Core 上报请求，审批决定再经消息总线返回 QQ 网关执行，因此修改该链路时必须同时验证 Core 与 QQ Gateway。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build QqApproval/OhMyBot.Plugins.QqApproval.csproj
dotnet test tests/OhMyBot.Plugins.QqApproval.Tests/OhMyBot.Plugins.QqApproval.Tests.csproj
```
