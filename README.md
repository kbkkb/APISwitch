[English](README.md) | [简体中文](README_zh.md)

# APISwitch

A lightweight desktop management tool and local protocol routing gateway for 3rd-party API providers and Google Antigravity official subscriptions (Windows WPF + .NET 10, portable and green).

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://github.com/kbkkb/APISwitch)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/kbkkb/APISwitch)](https://github.com/kbkkb/APISwitch/releases)

---

## Supported Clients & Tools

APISwitch provides deep native configuration management and local protocol translation for leading AI coding assistants and clients:

| Client / Tool | Management Target | Native Config & Storage Location |
|---|---|---|
| **Google Antigravity** | Google account subscriptions, session snapshots, quota monitoring | `%APPDATA%\Antigravity IDE\User\globalStorage\state.vscdb` |
| **OpenAI Codex** | API providers, local protocol bridging & seamless model hot-switching | `~/.codex/config.toml` + `~/.codex/auth.json` |
| **Anthropic Claude Code CLI** | CLI environment variables for API providers | `~/.claude/settings.json` (`ANTHROPIC_*` in `env`) |
| **Claude Desktop** | Custom model mapping & routing gateway takeover | `%APPDATA%\Claude\claude_desktop_config.json` |
| **OpenCode** | Provider & model pool configuration with one-click activation | `~/.config/opencode/opencode.json` |
| **Pi** | Minimal Coding Agent multi-model configuration & authentication | `~/.pi/agent/models.json` + `auth.json` |

---

## Key Features

### 1. Unified Multi-Tool Management & Zero-Intrusion Switching
- **Antigravity Quick Account Switching**: Save credential snapshots and switch Google accounts with one click. Automatically captures and synchronizes the latest credentials before switching (preventing Refresh Token rotation expiration). Optionally auto-closes and smoothly restarts the IDE.
- **Cross-Client Provider Sharing**: One-click copy of provider configurations across Claude CLI, Claude Desktop, Codex, OpenCode, and Pi with intelligent field mapping (`Auth Token` ↔ `Bearer Token` / `API Key`). Enter once, use everywhere.
- **Model Pool Management**: Dedicated model pool mechanism for OpenCode and Pi, clearly distinguishing "In Config" from "Not in Config" states, supporting on-demand activation and batch maintenance.
- **Default Model & Reasoning Effort**: Set a default model for OpenCode and Pi; configure per-model reasoning effort (reasoning effort / thinking budget) across Codex, Claude CLI, Claude Desktop, OpenCode and Pi.
- **One-Click cc-switch Migration**: Built-in migration wizard directly reads `~/.cc-switch/cc-switch.db`, allowing checkbox selection to import existing providers with automatic deduplication and overwriting.

### 2. Built-in Local Protocol Routing Gateway
- **High-Performance Local Gateway**: Built-in lightweight HTTP gateway service (listening on `127.0.0.1:15725` by default, configurable Host and Port in settings, supports LAN sharing).
- **Seamless Multi-Protocol Bridging**:
  - **Codex Protocol Bridge**: Translates standard OpenAI Chat Completions / Anthropic endpoints into Codex native Responses protocol.
  - **Claude CLI Protocol Bridge**: Automatically manages authentication headers and request format mapping.
  - **Claude Desktop Model Mapping**: Intercepts and rewrites `claude-sonnet` / `opus` / `haiku` requests to your designated upstream custom models.
- **Long-Context Protection & Prompt Stripping**: Automatically strips non-essential excessive system prompts and provides safeguards against massive 1M context overflows to prevent upstream 400 errors or unexpected costs.
- **Zero-Restart Hot Reload**: Point your client to the local gateway once. Provider switches reload instantly in the gateway with no client restarts needed.
- **Clear Router State Feedback**: Emerald when active, warning red when required but off, neutral gray when off — router status is readable at a glance.

### 3. Multi-Language Interface
- Switch between Simplified Chinese and English instantly (Settings → General & System → Interface Language), covering the main window, settings dialog, system tray, provider editors and more.
- Changes apply immediately with no restart; tray menu and toast messages update in sync.

### 4. Data Backup & Multi-Device Roaming
- **One-Click Full Export**: Bundle all tool provider configurations and Antigravity account data into a standardized JSON backup file.
- **Flexible Restore Modes**: Supports both "Merge & Append" (recommended: keeps existing configs while adding new or updating matching items) and "Full Overwrite" modes for seamless roaming across multiple machines.
- **Direct Local Management**: One click to open the local physical storage directory for full data ownership and transparency.

---

## How It Works

- **Google Antigravity**: Login sessions are stored in VS Code's globalStorage SQLite database (`ItemTable`) under `antigravityUnifiedStateSync.oauthToken` and `userStatus` (base64-encoded Protobuf). APISwitch includes a lightweight Protobuf parser to safely extract email, subscription tier, and quota information. Account switching executes safe transactional writes with automatic `.backup` creation.
- **Claude Code CLI**: Precisely rewrites `ANTHROPIC_*` environment variables in `~/.claude/settings.json`, strictly preserving all other configuration sections such as `hooks` and `permissions`.
- **Claude Desktop**: Manages Cowork inference gateway parameters (`inferenceProvider`, `inferenceGatewayBaseUrl`, `inferenceGatewayApiKey`, etc.). Switching back to "Official" cleanly removes these fields to restore native official login.
- **OpenAI Codex**: Configures `model_provider` and `[model_providers.<id>]` in `~/.codex/config.toml`, synchronizing authentication with `auth.json`. Supports direct upstream connections or routing through the local gateway for protocol adaptation.
- **OpenCode & Pi**: Strictly adheres to their official specifications to read and write `opencode.json` and `models.json` / `auth.json`, ensuring immediate recognition by CLI and runtime environments.
- **Safe Atomic Writes**: All file modifications follow a "write temporary file → verify integrity → atomic replace" workflow, generating automatic `.bak` backups to prevent corruption from unexpected interruptions.

---

## Download & Installation

Visit the [Releases](../../releases) page to download the latest version:
- `APISwitch-v{version}-win-x64.zip`: portable edition, unzip and run (.NET 10 runtime required)
- `APISwitch-Setup-v{version}.exe`: Windows installer

---

## Build from Source

APISwitch is built with .NET 10 and WPF.

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Quick Build
```powershell
# Build Release executable
dotnet build -c Release

# Output binary: bin\Release\net10.0-windows\APISwitch.exe
```

### One-Click Packaging
```powershell
# Build portable ZIP and Windows installer (reads version from csproj automatically)
.\scripts\package.ps1

# Output directory: Release_Package\
```

### Built-in Self-Test & Diagnostic Tools
```powershell
# Sandbox self-test for read/write logic (does not touch live configurations)
.\APISwitch.exe --selftest   # Diagnostic report output to %TEMP%\apiswitch-selftest.txt

# Read-only probe of current client configurations
.\APISwitch.exe --probe      # Output to %TEMP%\apiswitch-probe.txt
```

---

## Privacy & Security

1. **Local-First & Offline**: All configs, keys, and session snapshots are saved exclusively on your local machine (`%APPDATA%\APISwitch\`). No credentials or private API keys are ever collected or transmitted.
2. **Isolated Local Gateway**: The local routing gateway binds to `127.0.0.1` loopback by default. All request adaptations and context optimizations happen entirely on your machine without external relays.
3. **Backup Protection**: Every configuration change is safeguarded by integrity checks and automatic backups to keep your development environment safe.

---

## Roadmap

- [x] **Multi-Language Support (i18n)**: Simplified Chinese / English interface switching, applied instantly without restart.
- [ ] **Antigravity Quota Activation**: Activate account quota cycles via official Google streaming handshake (in development, not yet released).
- [ ] **Token Usage & Analytics**: Real-time Token consumption monitoring and call history analysis powered by the local gateway.
- [ ] **More Agent Platforms**: Continuous expansion to emerging AI coding assistants, terminal agents, and developer platforms.

---

## Feedback & Contribution

If you encounter issues or have suggestions for new clients and protocol adaptations, feel free to open a [GitHub Issue](../../issues)!

---

## Acknowledgements

- [cc-switch](https://github.com/farion99/cc-switch): For pioneering exploration and inspiration in multi-tool provider switching.
- **Antigravity Tool**: For early insights and explorations in Antigravity account and credential management.
- [LINUX DO](https://linux.do/) Community: For active discussions, creative ideas, and technical feedback.

---

## License

This project is licensed under the MIT License.
