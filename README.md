# OhMyBot.Plugins

OhMyBot 的公开运行时插件仓库。

当前包含 Kuro、Mihoyo、Skland 和 QqApproval。

## QqApproval

QQ 专用：把需要人工审批的 QQ 请求（加好友、邀请进群、入群申请）转给有权限且已订阅的用户，用编号菜单回复序号即可同意/拒绝。

- 配置 `QqApproval`：`ApprovalRequiredPrivilege` 控制审批与规则管理权限；`RequestTypes` 分别配置三类请求的
  `Enabled` 与 `RequiredPrivilege`；另有 `PendingTtl`、`RejectReason`。
- 有对应类型权限的用户在 QQ 私聊执行 `/notify`，从末尾的 `Bot消息通知` 分类进入，分别开启或关闭自己接收的好友申请、群邀请、入群申请通知。
  无目标权限的类型不会显示；旧菜单回调也会重新校验当前权限。
- 运行时命令 `/qqreq`（私聊，需审批权限）：`list` 审批、`recent` 查历史、
  `rules [on|off <类型>]` 管理自动名单开关、`allow|deny <类型> <user|group> <号码> [备注]` 加黑/白名单、
  `ruledel <规则id>` 删规则。类型取 `friend`、`invite`、`groupadd`。
- 默认接入加好友与邀请进群；入群申请默认关闭。自动黑/白名单默认关闭，开启前所有请求一律转人工。
- 通知里带申请人昵称、性别、年龄、QQ 等级和头像图（网关用 `get_stranger_info` 补齐，查不到就只显示 QQ 号）。
  昵称与附言是对方可控内容，渲染前统一做 CQ 码转义。
- 需要 Core 侧支持：网关经 `ReportPlatformRequest` 上报请求，审批决定经消息总线回到网关执行。

## 工作区要求

```text
OhMyBot/
├── OhMyBot/
└── OhMyBot.Plugins/
```

如 Core 仓库不在相邻的 `../OhMyBot`，构建时传入 `-p:OhMyBotRepositoryRoot=<path>`。

## 构建与测试

```bash
dotnet build OhMyBot.Plugins.slnx
dotnet test tests/OhMyBot.Plugins.Kuro.Tests/OhMyBot.Plugins.Kuro.Tests.csproj
dotnet test tests/OhMyBot.Plugins.Mihoyo.Tests/OhMyBot.Plugins.Mihoyo.Tests.csproj
dotnet test tests/OhMyBot.Plugins.Skland.Tests/OhMyBot.Plugins.Skland.Tests.csproj
dotnet test tests/OhMyBot.Plugins.QqApproval.Tests/OhMyBot.Plugins.QqApproval.Tests.csproj
```

输出位于 `build/<ProjectName>/`。插件 Build 后会自动部署到 Core Host 同配置输出的 `Plugins/<PluginName>/`。

部署会更新入口 DLL、PDB、模板和插件私有依赖，但只在 `pluginsettings.json` 不存在时初始化配置，不会覆盖已有配置。

## 新建插件

```bash
dotnet new install ./templates/OhMyBot.Plugin
dotnet new ohmybot-plugin -n Example \
  --PluginId com.ohmybot.example \
  --DisplayName "Example" \
  --SupportedPlatforms All
```

`SupportedPlatforms` 可选 `All`、`Telegram` 或 `QQ`。每个插件必须拥有独立测试项目，且测试项目只能引用对应插件；生成后把 `*Tests.cs.txt` 移入该插件的测试项目并改为 `.cs`。

无私有依赖时，Debug 包只包含 `Plugin.dll`、`Plugin.pdb` 和 `pluginsettings.template.json`。Core、Contracts 和插件公共 API 由 Host 提供。
