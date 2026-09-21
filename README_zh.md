[English](README.md) | [简体中文](README_zh.md)

# APISwitch

第三方 API 和谷歌官方订阅切换与管理工具，内置本地协议路由网关（Windows 桌面端，WPF + .NET 10，轻量高效，绿色便携）。

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://github.com/kbkkb/APISwitch)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/kbkkb/APISwitch)](https://github.com/kbkkb/APISwitch/releases)

---

## 支持的应用与客户端

APISwitch 针对主流 AI 编程助手与客户端提供深度的原生配置管理与本地协议转译：

| 应用 / 客户端 | 管理对象 | 核心配置文件 / 本地存储位置 |
|---|---|---|
| **Google Antigravity** | Google 官方账号订阅、登录态快照、配额监控 | `%APPDATA%\Antigravity IDE\User\globalStorage\state.vscdb` |
| **OpenAI Codex** | API 供应商管理、本地协议桥接与模型热切 | `~/.codex/config.toml` + `~/.codex/auth.json` |
| **Anthropic Claude Code CLI** | 终端命令行 API 供应商环境参数 | `~/.claude/settings.json`（`env` 中 `ANTHROPIC_*`） |
| **Claude Desktop** | 桌面客户端自定义模型映射与路由接管 | `%APPDATA%\Claude\claude_desktop_config.json` |
| **OpenCode** | 供应商与模型池配置、一键入库激活 | `~/.config/opencode/opencode.json` |
| **Pi** | Minimal Coding Agent 模型配置与鉴权管理 | `~/.pi/agent/models.json` + `auth.json` |

---

## 核心特性

### 1. 多工具统一管理与零侵入切换
- **Antigravity 账号快捷切换**：凭证快照存档，一键切换 Google 账号；切换前自动捕获并同步当前账号最新凭证（防 Refresh Token 轮换失效）；支持切换后自动关闭并平滑重启 IDE。
- **跨端供应商智能互通**：支持在 Claude CLI、Claude Desktop、Codex、OpenCode、Pi 之间一键互相复制供应商配置，智能完成字段自动映射（Auth Token ↔ Bearer Token / API Key），一次录入，全端复用。
- **模型配置池化管理**：针对 OpenCode 与 Pi 提供模型配置池机制，清晰标记「已加入配置 / 未加入配置」状态，支持按需激活与批量维护。
- **默认模型与思考强度**：OpenCode、Pi 支持设置默认模型；Codex、Claude CLI、Claude Desktop、OpenCode、Pi 五端均支持为每个模型单独配置思考强度（reasoning effort / thinking budget）。
- **历史数据一键迁移**：内置 cc-switch 数据库迁移向导，直接读取 `~/.cc-switch/cc-switch.db`，支持勾选式导入已有供应商，自动去重与覆盖更新。

### 2. 内置本地协议路由网关（Local Protocol Router）
- **高性能本地代理**：内置轻量级 HTTP 网关服务（默认监听 `127.0.0.1:15725`，支持在设置中自定义监听主机地址与端口，支持局域网多设备共享）。
- **多协议无缝转译**：
  - Codex 协议桥接：将第三方 Chat Completions / Anthropic 端点智能转译为 Codex 原生 Responses 协议；
  - Claude CLI 协议桥接：自动处理鉴权 Header 与请求结构映射；
  - Claude Desktop 模型映射：拦截并重写 `claude-sonnet` / `opus` / `haiku` 等请求至目标第三方大模型。
- **长文本防护与上下文优化**：自动剥离非必要超长 system prompts，提供巨量上下文截断防护，防止第三方端点报 400 错误或费用异常溢出。
- **零重启热切生效**：客户端只需一次配置指向本地网关，切换供应商时网关即时热重载，无需反复重启客户端。
- **清晰的路由状态反馈**：开启=翠绿高亮、需开启但未开=警示红、关闭=中性灰，路由状态一眼可辨。

### 3. 多语言界面
- 支持简体中文 / English 一键切换（设置 → 常规与系统 → 界面语言），覆盖主界面、设置弹窗、系统托盘、供应商编辑等全部窗口。
- 切换即时生效，无需重启应用；托盘菜单与提示Toast 同步更新。

### 4. 数据备份与多设备漫游
- **一键全量导出**：将所有工具的供应商配置与 Antigravity 账号数据一键打包导出为标准 JSON 备份文件。
- **灵活导入恢复**：支持「合并追加」（推荐：保留已有配置，追加新供应商或更新同名项）与「完全覆盖」双模式导入，方便在多台开发机之间无缝漫游。
- **便捷本地管理**：设置中支持一键打开本地物理存储目录，数据完全透明自主可控。

---

## 工作原理

- **Google Antigravity**：登录态存储在 VS Code 架构的 globalStorage SQLite 数据库（`ItemTable`）中，核心字段为 `antigravityUnifiedStateSync.oauthToken` 和 `userStatus`（base64 编码的 Protobuf）。APISwitch 内置轻量 Protobuf 解码器，安全解析邮箱、订阅计划与配额信息；切换时执行安全事务写入，并自动维护 `.backup` 文件。
- **Claude Code CLI**：精准修改 `~/.claude/settings.json` 中 `env` 的 `ANTHROPIC_*` 相关环境变量，对于 `hooks`、`permissions` 等其他系统及业务配置严格原样保留。
- **Claude Desktop**：针对桌面客户端的 Cowork 推理网关参数（`inferenceProvider`、`inferenceGatewayBaseUrl`、`inferenceGatewayApiKey` 等）进行配置写入与维护；切换回「官方」档位时干净移除网关参数，无缝恢复官方原生登录。
- **OpenAI Codex**：配置 `~/.codex/config.toml` 中的 `model_provider` 与 `[model_providers.<id>]` 节点，同时联动 `auth.json` 鉴权信息。支持直接连接第三方端点或经由本地网关进行协议适配。
- **OpenCode & Pi**：严格遵循各自官方规范读写 `opencode.json` 以及 `models.json` / `auth.json`，确保配置变更立即可被命令行或运行环境加载。
- **安全原子写入**：所有涉及物理文件的写操作均执行「写入临时文件 → 校验完整性 → 原子替换」流程，并在写前自动生成 `.bak` 备份文件，杜绝因进程意外中断导致配置损坏。

---

## 下载与安装

进入 [Releases](../../releases) 页面，下载最新版本：
- `APISwitch-v{版本号}-win-x64.zip`：绿色便携版，解压即用（需 .NET 10 运行时）
- `APISwitch-Setup-v{版本号}.exe`：Windows 安装包

---

## 从源码构建

本项目基于 .NET 10 与 WPF 开发。

### 环境要求
- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 快速编译
```powershell
# 编译 Release 版本
dotnet build -c Release

# 产物路径：bin\Release\net10.0-windows\APISwitch.exe
```

### 一键打包发布
```powershell
# 生成便携 ZIP 与 Windows 安装包（自动读取 csproj 版本号）
.\scripts\package.ps1

# 产物目录：Release_Package\
```

### 内置自检与探测工具
```powershell
# 沙箱模式自检读写逻辑（不会影响真实机器的配置文件）
.\APISwitch.exe --selftest   # 诊断报告输出到 %TEMP%\apiswitch-selftest.txt

# 只读探测系统当前各客户端配置状态
.\APISwitch.exe --probe      # 探测信息输出到 %TEMP%\apiswitch-probe.txt
```

---

## 隐私与安全保障

1. **纯本地离线优先**：APISwitch 默认所有配置与快照凭证均保存在用户本地设备（`%APPDATA%\APISwitch\`），绝不上报、存储或外泄任何用户凭据与私有 API Key。
2. **本地协议网关隔离**：内置的路由网关默认仅监听本机 `127.0.0.1` 环回接口，纯本地完成协议重写与长文本优化，不设立任何远程中继服务器。
3. **配置备份护航**：每一次配置切换均有安全校验与自动备份机制，随时保障主环境配置文件不受损。

---

## 后续更新计划 (Roadmap)

- [x] **多语言支持 (i18n)**：已完成简体中文 / English 界面切换，即时生效无需重启。
- [ ] **Antigravity 限额激活功能**：通过 Google 官方流式握手激活账号配额周期（研发中，暂未开放）。
- [ ] **Token 统计与用量分析**：依托本地协议网关，提供各供应商与模型的实时 Token 消耗监控与调用历史统计。
- [ ] **更多 Agent 平台支持**：持续扩展对主流及新兴 AI 编程助手、终端 Agent 平台的统一配置与协议转译管理。

---

## 反馈与贡献

如果您在使用过程中遇到任何问题，或对新客户端、新协议支持有任何想法与建议，欢迎提交 [GitHub Issues](../../issues)！

---

## 鸣谢

- [cc-switch](https://github.com/farion99/cc-switch)：感谢其在多工具供应商切换领域的探索与启发。
- **Antigravity Tool**：感谢在 Antigravity 账号与凭证管理方面的思路探索。
- [LINUX DO](https://linux.do/) 社区：感谢社区开发者们的活跃探讨、灵感碰撞与技术支持。

---

## License

本项目采用 MIT License 开源协议。
