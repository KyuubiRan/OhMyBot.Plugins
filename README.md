# OhMyBot.Plugins

OhMyBot 的公开运行时插件仓库。

## 项目

- [ImageConverter](ImageConverter/README.md)
- [Kuro](Kuro/README.md)
- [Mihoyo](Mihoyo/README.md)
- [PlaywrightProvider](PlaywrightProvider/README.md)
- [QqApproval](QqApproval/README.md)
- [Skland](Skland/README.md)

各插件的功能、指令、配置和开发验证方式见对应项目 README。

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
dotnet test OhMyBot.Plugins.slnx
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
