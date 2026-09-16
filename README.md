# APISwitch

一站式 AI 编程工具账号 / 供应商切换器（Windows 桌面应用，WPF + .NET 10，单文件绿色 exe）。

四个完全独立的模块：

| 模块 | 管理对象 | 写入位置 |
|---|---|---|
| 🪐 谷歌反重力 | Antigravity IDE 的 Google 登录账号 | `%APPDATA%\Antigravity IDE\User\globalStorage\state.vscdb` |
| 🧪 Codex | Codex API 供应商（客户端与 CLI 共用配置） | `~/.codex/config.toml` + `~/.codex/auth.json` |
| 💬 Claude CLI | Claude Code 的 API 供应商 | `~/.claude/settings.json`（仅 `env` 中 `ANTHROPIC_*` 键） |
| 🖥️ Claude 客户端 | Claude Desktop 推理网关 | `%APPDATA%\Claude\claude_desktop_config.json`（`inferenceGateway*` 字段） |

## 特性

- **Antigravity 账号切换**：凭证快照存档，一键切换 Google 账号；切换前自动同步当前账号最新凭证（防 refresh token 轮换失效）；可自动关闭并重启 IDE
- **供应商跨端复制**：Claude CLI / Claude 客户端 / Codex 三端供应商一键互相复制，字段自动映射（Auth Token ↔ Bearer Token / API Key），一次录入三端可用
- **cc-switch 一键导入**：直接读取 `~/.cc-switch/cc-switch.db`，勾选式选择要导入的供应商，同名覆盖、官方项自动去重
- **安全写入**：所有配置文件写前自动备份 `.bak`，临时文件 + 原子替换；Claude CLI 的 `hooks`/`permissions`、Codex 的 `notify`/`[desktop]`、Claude 客户端的 `mcpServers` 等非目标配置一律原样保留
- **纯本地**：不联网、不上传，所有存档保存在 `%APPDATA%\APISwitch\`

## 工作原理

- **Antigravity**：登录态是 VS Code fork 的 globalStorage SQLite（`ItemTable`）中的 `antigravityUnifiedStateSync.oauthToken` / `userStatus`（base64 + protobuf）。切换 = 关进程后 `INSERT OR REPLACE` 写回（含 `state.vscdb.backup`）。应用内置迷你 protobuf 解析器，从凭证中解出邮箱、订阅计划与登录态用于展示。
- **Claude CLI**：重写 `settings.json` 中 `env` 的所有 `ANTHROPIC_*` 键，其余 JSON 节点原样保留。
- **Claude 客户端**：写入 Cowork 推理网关字段 `inferenceProvider` / `inferenceGatewayBaseUrl` / `inferenceGatewayApiKey` / `inferenceGatewayAuthScheme` / `inferenceModels` / `coworkEgressAllowedHosts`；「官方」档则整体移除这些字段恢复官方登录。
- **Codex**：设置根键 `model_provider` 与对应 `[model_providers.<id>]` 表（支持 `env_key` + `auth.json` 或 `experimental_bearer_token` 两种中转风格）；「官方」档移除 `model_provider` 与 `OPENAI_API_KEY`，保留 ChatGPT 登录 `tokens`。

## 下载

到 [Releases](../../releases) 下载 `APISwitch.exe`（win-x64 单文件自包含版，无需安装 .NET），双击即用。

## 从源码构建

```powershell
# 需要 .NET 10 SDK
dotnet build -c Release

# 发布单文件 exe（产物在 bin\Release\net10.0-windows\win-x64\publish\）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

自检（沙箱内验证读写逻辑，不触碰真实配置）：

```powershell
APISwitch.exe --selftest   # 结果输出到 %TEMP%\apiswitch-selftest.txt
APISwitch.exe --probe      # 只读探测当前各工具状态，输出到 %TEMP%\apiswitch-probe.txt
```

## 免责声明

- 本项目仅在本地读写配置文件，不修改、不代理任何网络流量
- 多账号使用请遵守相应服务条款，因账号轮换 / 滥用导致的风险自担
- Claude Desktop 网关字段基于社区逆向，若你的客户端版本不识别，请提 Issue

## License

[MIT](LICENSE)
