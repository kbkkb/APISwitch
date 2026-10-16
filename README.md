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
| **Google Antigravity** | Google account subscriptions (smart distinction for PRO vs EXP), session snapshots, quota monitoring | `%APPDATA%\Antigravity IDE\User\globalStorage\state.vscdb` |
| **OpenAI Codex** | API providers, local protocol bridging & seamless model hot-switching, usage tracking | `~/.codex/config.toml` + `~/.codex/auth.json` |
| **Anthropic Claude Code CLI** | CLI environment variables for API providers, session usage analytics | `~/.claude/settings.json` (`ANTHROPIC_*` in `env`) |
| **Claude Desktop** | Custom model mapping & routing gateway takeover, usage monitoring | `%APPDATA%\Claude\claude_desktop_config.json` |
| **OpenCode** | Provider & model pool configuration with one-click activation, session usage analytics | `~/.config/opencode/opencode.json` + `opencode.db` |
| **Pi** | Minimal Coding Agent multi-model configuration, authentication & usage analytics | `~/.pi/agent/models.json` + `auth.json` + `sessions/` |
| **StepCode / zcode / dsh** | Terminal and specialized coding agent local session scanning & token metrics | `~/.stepcode/`, `~/.zcode/`, `~/.dsh/` logs |
| **Gemini CLI** | Google Gemini command-line interface session scanning and token monitoring | `%TEMP%\gemini_chat_*.json` |

---

## Key Features

### 1. Unified Multi-Tool Management & Zero-Intrusion Switching
- **Antigravity Quick Account Switching & Gemini 4 Quota Monitoring**: Save credential snapshots and switch Google accounts with one click. Automatically captures and synchronizes latest credentials before switching (preventing Refresh Token rotation expiration). **Full support for real-time Gemini 4 (Argon) 5H & weekly quota monitoring with a triple-column quota panel**; gracefully distinguishes Paid Subscriptions (PRO, dedicated Gemini 4 pool) from Promotional Quotas (EXP) with badge highlights. Optionally auto-closes and smoothly restarts the IDE.
- **Cross-Client Provider Sharing**: One-click copy of provider configurations across Claude CLI, Claude Desktop, Codex, OpenCode, and Pi with intelligent field mapping (`Auth Token` ↔ `Bearer Token` / `API Key`). Enter once, use everywhere.
- **Model Pool Management**: Dedicated model pool mechanism for OpenCode and Pi, clearly distinguishing "In Config" from "Not in Config" states, supporting on-demand activation and batch maintenance.
- **Default Model & Reasoning Effort**: Set a default model for OpenCode and Pi; configure per-model reasoning effort (reasoning effort / thinking budget) across Codex, Claude CLI, Claude Desktop, OpenCode and Pi.
- **Codex Model Auto-Fetch**: On save, APISwitch requests the provider's model list (`GET /models`) and writes it into the Codex model catalog, so every model the provider offers becomes selectable in Codex's `/model` switcher — no manual entry required.
- **One-Click cc-switch Migration**: Built-in migration wizard directly reads `~/.cc-switch/cc-switch.db`, allowing checkbox selection to import existing providers with automatic deduplication and overwriting.
- **Safe Client Restart**: When restarting MSIX-packaged Electron clients such as Codex and Claude, APISwitch closes them gracefully first, then verifies that the new process actually appeared — retrying once and reporting the real outcome if it did not, instead of claiming success while the client never came back.

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

### 4. Usage Monitoring & Analytics (Enhanced in v0.3.2)
- **Token Usage Dashboard**: A dedicated monitoring window summarizing total / input / output tokens and request counts over any selected time range.
- **Long Range Quick Options & Contribution Heatmap Grid (v0.3.2 New)**:
  - Quick range selector includes `Last 1 Day`, `Last 1 Week`, `Last 30 Days`, **`6 Months` (180 days)**, and **`1 Year` (365 days)**;
  - For extended time spans, automatically switches to a GitHub / Claude-style **Contribution Heatmap Grid**: 7 rows (Monday to Sunday) across weeks, rendered with 5-tier logarithmic indigo shading to prevent single high-volume spikes from wiping out regular activity;
  - Adaptive default mode (bar chart for short ranges $\le 31$ days, heatmap grid for long ranges $> 31$ days) with an instant **Bar Chart / Heatmap manual toggle button** in the top-right header and dynamic legends;
  - Hover tooltip displays precise day totals, input/output breakdown, and weekday; click any cell to drill down into that day's statistics across all cards and detail lists.
- **Broad Multi-Client Support**: Seamlessly scans local session files from Codex, Claude CLI, Claude Desktop, OpenCode, Pi, **Gemini CLI**, **StepCode**, **zcode**, and **dsh** with incremental delta scanning.
- **Deep Performance Metrics**: Real-time tracking of **Cache Hit Rate** (`hits / (fresh input + hits + writes)`), **Output Speed** (tokens/s), and **Time to First Token (TTFT)** across summary cards, recent calls, and provider/model groupings.
- **Cross-Provider Model Aggregation**: Normalizes and combines differing naming conventions for the same model across providers for unified analytics.
- **Historical Backfill & 400-Day Extended Retention**: Data is safely stored in SQLite with retention and scanning extended to 400 days (over 1 year of full coverage); includes automated backup and one-click historical re-scanning.
- **Dial-Style Date/Time Picker**: Custom calendar panel plus a clock dial for hour and minute with dual-ring 24-hour navigation.

### 5. Data Backup & Multi-Device Roaming
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
- [x] **Antigravity Quota Activation**: Activate account quota cycles via the official Google streaming handshake. Supports single and batch activation (cancelable at any time), reports latency and failure reasons, and counts down to the reset time when a 429 quota-exhausted response is hit.
- [x] **Token Usage & Analytics (v0.3.0)**: Dedicated usage monitoring window with daily trend chart, provider / client / model breakdowns, cross-provider model aggregation and local session scanning.
- [x] **Usage Performance Metrics & Backfill (v0.3.1)**: Cache hit rate, output speed and time to first token across summary cards, recent calls and per-model / per-provider drill-downs, plus one-click backfill of historical data.
- [x] **More Agent Platforms & Heatmap Grid (v0.3.2)**: Extended usage monitoring to Gemini CLI, StepCode, zcode, and dsh; refined Google Pro subscription tier vs promotional quota detection; introduced long-range Contribution Heatmap Grid and 400-day data retention.
- [ ] **Automated Routing & Health Check**: Support automated load balancing, model routing rules, and upstream health-checked failover.

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
