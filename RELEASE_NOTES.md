[English](RELEASE_NOTES.md) | [简体中文](RELEASE_NOTES_zh.md)

# APISwitch Release Notes

## APISwitch v0.3.6 Release Notes

### [Fixes · Antigravity 2.x (Hub) Account Switching & Credential Sync]
• **Bidirectional `gemini:antigravity` Sync**: Fully adapted to Antigravity 2.x Hub session storage—automatically captures fresh logins and rotated tokens made inside the Antigravity IDE from Windows Credential Manager (`gemini:antigravity`) and `app_storage.json`, while writing back the exact native schema (`id_token_email`, `expires_in`, preserved `writer_id`, `agy.onboarding.lastLoginUsername`, and all `state.vscdb` copies).
• **`auth/aicode` Scope & Pre-Switch Token Verification**: Added `https://www.googleapis.com/auth/aicode` to Google OAuth scopes to eliminate `ACCESS_TOKEN_SCOPE_INSUFFICIENT` warnings. Account switching now verifies and refreshes the target token before writing credentials (persisting rotated refresh tokens and breaking background write recursion), aborting with a clear re-login prompt instead of launching into `UNAVAILABLE (code 503)`.
• **Clean Process Teardown & Isolation**: `StopIdeAsync` now cleans up lingering Antigravity `language_server` child processes before writing credentials; deferred post-restart quota polling avoids startup `get_auth_status` contention; `AgCliService` backs up and restores `gemini:antigravity` when activating non-active accounts.

### [Performance & Quota Savings · Pool-Aware Skip & Deterministic Prompt]
• **Smart Per-Pool Skip**: Added `EvaluatePoolActivationNeed` so already-counting `Gemini` or `Claude/GPT` quota pools are skipped automatically; accounts whose supported pools are all active bypass `agy.exe` invocation with zero cooldown delay.
• **Free / Plus Weekly-Cycle Protection**: Non-Pro accounts (`Starter` / `Google AI Plus`) strictly anchor activation checks on the 7-day weekly cycle (including full-cycle locking when remaining quota still reads `100%`), preventing the auxiliary `3p-5h` bucket from triggering duplicate weekly quota consumption every 5 hours.
• **Faster Pipeline & Minimal Output**: Removed redundant per-account CLI model probe subprocesses, parallelized `retrieveUserQuotaSummary` and `fetchAvailableModels` calls, prioritized `-low` effort candidates, added `--disable-slash-commands`, and tightened the activation prompt from `"hi"` to `"只回复1"` to minimize output/thinking token cost and latency.

### [Security · Anti-Ban Polling & Dynamic Client Version Alignment]
• **Dynamic IDE Version Header**: Reads the installed `Antigravity.exe` file version dynamically (`AgPaths.GetInstalledIdeVersion`) instead of sending a hardcoded legacy version string.
• **Active-First Background Polling & Natural Jitter**: Silent background polling now focuses on the current active account while throttling idle accounts to 15-minute (foreground) / 30-minute (tray/minimized) intervals with staggered jitter; sequential CLI activations retain a natural 2–4s cooldown between accounts.

### [Improvements · Floating Widget Zero-Drift Position Memory]
• Persists immutable screen edge anchors (`FloatingWidgetAnchorX/Y/Right/Bottom`), ensuring the Antigravity Floating Widget reopens in the exact remembered spot across cold restarts and capsule expand/collapse transitions.

---

## APISwitch v0.3.5 Release Notes

### [New · Desktop Floating Widget for Antigravity]
• **Compact Always-On-Top Capsule**: Defaults to the bottom-right corner of the primary screen (independent of main window minimization or tray hiding), displaying live **5H + Weekly quotas** (`Gemini` & `Claude/GPT`), single-client **today's Antigravity token total**, and latest step generation speed (`tok/s`).
• **Expandable Detail Card & Zero-Drift Anchoring**: Click the capsule to expand upward/inward into a clean dashboard featuring:
  - Dual-column **5H + Weekly** quota progress bars and countdown status pills;
  - 3-column Antigravity daily usage card (Today tokens/requests/In-Out, Latest & Avg `tok/s`, Most recent model & TTFT);
  - Collapsible **Available Models** chip drawer and **1-click Antigravity Account Switcher** with live 5H/Weekly badges;
  - Anti-misclick UX: removed top-right `×` close button in favor of a prominent **`Collapse ▾`** pill; hiding or resetting position is cleanly handled via right-click menu, Tray menu, Settings, or the dedicated toggle button in the Antigravity toolbar. Canonical capsule anchor locking eliminates expand/collapse position drift.
• **Real-Time Low-Overhead Collection**: Watches `~/.gemini/antigravity/brain/**/transcript.jsonl` with 1.5s debounce + 15s fallback sync (`ScanAntigravityQuick`) and caches parsed `pricing_ledger_v1.json` / `conversation_summaries.db` signatures in memory for zero-stutter updates.

### [New · Smart Merged Model Picker & Anti-Close Fix]
• **Official Picker Whitelist Filtering**: Parses `agentModelSorts` and `groupedModels` from `fetchAvailableModels` to hide internal/disabled entries (`tab_*`, `chat_*`, `*-tiered`, `*-sonar`, `*-image`, `flash-lite`, `gemini-2.5-*`, `gemini-3-flash`, disabled slots), removing noisy per-model percentages.
• **Merged Thinking-Effort Chips**: Consolidates thinking variants of the same model family into a single sleek chip (e.g. `Gemini 4 Neon [Low–XHigh]`, `Claude Opus 5.5 [Low–High]`).
• **In-Place Refresh Stability**: Replaced destructive `Clear()` / `Items.Refresh()` calls with in-place `Profile.UpdateFrom()` and targeted bindings so expanding the available models drawer never stutters or unexpectedly closes on focus/timer refresh.

### [UI Refactor · Unified 6-Client Cards, Usage Monitor & Modern Tray Menu]
• **Unified Card & Hero Design Across All 6 Tabs**: Refactored Antigravity, Codex, Claude Code CLI, Claude Desktop, OpenCode, and Pi tabs with consistent `12px`/`14px` rounded cards, left active accent bars, compact icon buttons (`CardGhostBtn` / `CardDangerIconBtn`), and 2-line metadata hierarchy.
• **Usage Monitor Layout Polish & Export**: Consolidated top view tabs, search bar, quick ranges, clock-dial date pickers, and the 10-client icon strip into one crisp control bar, refreshed the 5 KPI cards and table spacing, and added **JSON / CSV** call record export.
• **Redesigned Tray Context Menu**: Custom GDI+ painted rounded header with version pill and two-column quick-switch rows featuring color-coded `G` / `C` quota capsules plus quick Floating Widget toggle.

### [Fixes & Engineering Refactor]
• **Model Attribution Self-Heal**: Fixed missing model names in `StepCode` (`message.model` + `stepcode:model_fix_v1`) and `Codex`/`Pi` (`codex:model_fix_v1` header model carry-over).
• **Pro Subscription Detection & Copy Cleanup**: Refined paid Pro entitlement detection (`gemini-4-neon` / `gemini-4-carbon`) and cleaned up legacy Argon copy across locales.
• **Modular Architecture**: Split `App.xaml.cs` and `MainWindow.xaml.cs` into cohesive partial modules (`App.SelfTest.cs`, `MainWindow.Antigravity.cs`, `MainWindow.Claude.cs`, `MainWindow.CodexAndRouter.cs`, `MainWindow.OpenCodeAndPi.cs`, `MainWindow.DragDrop.cs`, `MainWindow.FloatingWidget.cs`, `MainWindow.Tray.cs`).

---

## APISwitch v0.3.4 Release Notes

### [Improvements · Quota Countdown & Refresh UX]
• **Accurate "Not started" Timer Detection**: Identified that Google's quota API returns a sliding full-cycle placeholder `resetTime` (e.g., `+5h` / `+7d`) for untouched 100% buckets. APISwitch now detects untouched buckets via `IsTimerStandby` and displays a **`Not started`** pill instead of a misleading countdown.
• **5-State Status Pills & Local Reset Tooltips**: Upgraded quota countdown labels into color-coded status pills (`Not started`, `Counting down`, `Exhausted`, `Ready to refresh`, `Not activated / No 5H limit`) with hover tooltips showing the exact expected local reset time (`Today HH:mm`, `Tomorrow HH:mm`, or `MM-dd HH:mm`).
• **Refresh Button Feedback**: Added a spin animation and duplicate-click guard (`IsRefreshingQuota`) to per-account quota refresh buttons.

### [Adaptation · Dual-Column Quota Layout]
• Adapted the Antigravity account card layout to match upstream removal of the separate Pro Argon quota pool: folded the Argon column by default behind a single frontend toggle (`Profile.ShowArgonQuotaColumn`), presenting a clean 50/50 dual-column panel (`Gemini` & `Claude & GPT`) while keeping backend quota parsing intact for quick re-enablement if needed.

### [Fixes · Bilingual Update Dialog Release Notes]
• Fixed an issue where selecting "中文" in the update dialog still displayed English release notes:
  - Updated the GitHub Actions release workflow to generate bilingual Release bodies (`<!-- lang:zh -->` and `<!-- lang:en -->` blocks).
  - Hardened `ReleaseNotesHelper.ParseBilingual` to strip language navigation links before language detection and added remote `RELEASE_NOTES_zh.md` fallback fetching in `UpdateService`.

---

## APISwitch v0.3.3 Release Notes

### [New · Native Antigravity Usage & Performance Monitoring]
• Added `AntigravityUsageSource` to scan Antigravity IDE local conversation databases (`~/.gemini/antigravity/conversations/*.pb` + `*.db`), bringing total supported clients in Usage Monitor to **10**.
• Parses `steps.metadata` (`gen_metadata`) to accurately extract real model identifiers, input/output/cached tokens, TTFT, and output speed (`tokens/s`), eliminating "Unknown Model" entries for Antigravity calls.
• Supports one-click **Backfill History** to re-index historical Antigravity sessions with accurate model names and timing metrics.

### [New · Usage Monitor UI Redesign & Client Filter Strip]
• Streamlined Usage Monitor tabs from 5 to **4 focused views** (`Providers`, `Clients`, `Models`, `Recent Calls`), preserving full per-model drill-down inside `Providers`.
• Added a quick **Client Icon Filter Strip** above the range controls with an **`All`** button (selected by default) plus dedicated icon pills for all 10 supported clients, enabling instant one-click filtering across cards, charts, and tables.
• Added a **Cache Write** sub-column and **Avg Speed (`tokens/s`) / Avg TTFT** summary in the bottom status bar and call details.

### [Fixes · Antigravity Quota Sync & Google AI Plus Tier Detection]
• Locked quota & model endpoints (`fetchAvailableModels`, `retrieveUserQuota`, `loadCodeAssist`) to `daily-cloudcode-pa.googleapis.com` and added `ReconcileQuotaWithModels`, keeping APISwitch quota percentages aligned with the Antigravity IDE.
• Fixed silent quota/tier drift caused by background refresh pausing when the window is deactivated (`MainWindow_Deactivated`), and resolved cold-start quota-fetch race conditions.
• Separated **`Google AI Plus` (`g1-plus-tier`)** from `Pro` and `Starter/Free`: Plus accounts now show an amber **`PLUS`** badge instead of being misidentified as `PRO`, while retaining independent Gemini 4 Argon quota display when available.

### [Fixes & Optimizations]
• Refactored `ClaudeCliProcess` and `ClaudeProcess` termination logic to safely restart Claude CLI/Desktop without affecting unrelated processes.
• Improved incremental scanning and timestamp/token accuracy across `GeminiCliUsageSource`, `StepCodeUsageSource`, `ZCodeUsageSource`, and `DshUsageSource`.
• Added concise bilingual UI tooltips and localized strings for all new Usage Monitor controls and tier badges.

---

## APISwitch v0.3.2 Release Notes

### [Key Feature · Gemini 4 (Argon) Independent Quota & Limit Monitoring]
• Account cards overhauled with a Triple-Column Quota Panel (Gemini models, **Gemini 4 Argon**, and Claude & GPT models).
• Direct integration with Google Cloud Code quota endpoint (`retrieveUserQuotaSummary`) to extract and monitor the `argon` quota bucket:
  - **Gemini 4 Dedicated 5-Hour Dynamic Limit (5H)**: Displays remaining quota percentage, high-precision progress bar, and reset countdown.
  - **Gemini 4 Dedicated Weekly Base Quota**: Real-time weekly pool status, consumption progress, and reset cycle.
• **Subscription Tier & Quota Entitlement Awareness**:
  - Distinguishes official Paid Subscriptions ("PRO") from Promotional / Trial Quotas ("EXP").
  - Paid subscription accounts unlock the full, dedicated Gemini 4 Argon quota pool;
  - Promotional and trial accounts display "Shared with Gemini" or "No separate quota" with comprehensive hover tooltips explaining entitlements.
• **Historical Quota Snapshot Tracking**: Quota snapshot service records historical `gemini-argon-5h` and `gemini-argon-weekly` fluctuations over time.

### [New · Expanded Client Usage Monitoring (Gemini CLI / StepCode / zcode / dsh)]
• Added Gemini CLI local session scanning and ingestion (automatically parses `%TEMP%\gemini_chat_*.json` with high-performance transactional bulk insertion for millions of tokens).
• Added StepCode local session scanning, correcting token measurement criteria to precisely reflect true input/output token consumption.
• Added zcode and dsh agent usage monitoring; resolved dsh timestamp millisecond alignment and chronological order issues.
• Extended session scanning and SQLite retention window from 90 days to 400 days (over 1 year of full historical coverage).

### [New · Long-Range Quick Ranges & GitHub / Claude-Style Heatmap Grid]
• Added "6 Months" (180 days) and "1 Year" (365 days) quick range selection buttons.
• Introduced GitHub / Claude-style Contribution Heatmap Grid:
  - 7 rows (Monday to Sunday) across weeks, adapting dynamically to window resizing.
  - Aligned month labels along the top and "Mon / Wed / Fri" weekday indicators on the left.
  - Logarithmic 5-tier indigo intensity coloring to prevent single high-volume spikes from washing out regular daily activity.
  - Hover tooltip displaying date, weekday, total tokens, and input/output breakdown.
  - Interactive drill-down: click any cell to filter the entire window (summary cards, providers, clients, models, and recent calls) to that day; click again to deselect.
• Adaptive chart mode: automatically defaults to Bar Chart for short ranges ($\le 31$ days) and Heatmap Grid for long ranges ($> 31$ days), with manual toggle buttons in the top-right header and dynamic legends.

### [New · In-App Multilingual Update Dialog & Language Switching]
• Update dialog (`UpdateDialog`) automatically matches and renders release notes in the user's active application language (`zh-CN` / `en-US`).
• Added interactive `[中文]` / `[English]` language switch pills in the Changelog header for instant cross-language inspection.
• Offline bilingual fallback to local release notes when remote notes are omitted.

### [Architecture & Engineering]
• Modularized usage scanning: introduced the `IUsageSource` interface and `UsageSourceRegistry` for clean multi-client expansion.
• Standardized Release outputs and removed unnecessary Debug caches.

---

## APISwitch v0.3.1 Release Notes

### [New · Usage Performance Metrics]
Added three key performance metrics to the Usage Monitor:
• Cache Hit Rate: Summary card displays hit proportion over the selected range (`hits / (fresh input + hits + writes)`), with hit and write token totals in the sub-label.
• Output Speed: Output tokens / duration (tokens/s).
• Time to First Token (TTFT): Elapsed time from request dispatch to first generated content chunk.
• Metrics visible across 4 areas: summary cards, each call row in Recent Calls, and per-model / per-provider drill-down rows with color coding (Purple for cache, Red for speed, Amber for TTFT).
• Recent Calls table expanded from 7 to 10 columns (Time, Client, Provider, Model, In, Out, Total, Cache, Speed, TTFT).
• Selecting a day drills down performance metrics together with token totals.

### [New · Historical Data Backfill]
• Resolves zero-value metrics for existing users upgrading to newer versions.
• Added "Backfill history" button in the bottom-right of the Usage Monitor (and `--backfill-usage` CLI flag).
• Workflow: Auto-backup `usage.db` (timestamped) → clear events table → invalidate scan offsets → full rescan of all local session logs.
• Reports written records, records with cache data, and records with timing data upon completion.

### [New · Codex Model Auto-Fetch]
• Optionally queries the provider for available models (`GET /models`) upon saving and registers them into Codex's model catalog.
• Provider models immediately become selectable in Codex's `/model` picker.
• Graceful error handling with actionable tips for Base URL and API Key checks.

### [New · Cancelable Batch Quota Activation]
• Added a "Cancel Batch" button during batch Antigravity account quota activation.
• Existing successful activations are preserved upon cancellation.

### [New · Safe Client Restart]
• Solved issues where Electron / MSIX-packaged clients (Codex, Claude) failed to restart cleanly.
• Graceful window closure → verify exit → process tree termination if lingering → verified detection of new instance startup before reporting success.

### [Optimizations & Fixes]
• Router gateway metrics: local proxy now captures first-token latency, duration stopwatch, and both OpenAI/Anthropic prompt cache formats.
• Standardized window minimization: clicking "-" or taskbar button minimizes to taskbar; tray minimization is reserved for the close button.
• Fixed CLI window popping up during background `agy.exe` activation via hidden window flags and stdin redirection.
• Fixed quota countdown timer to reference live quota status instead of stale reset times.
• Fixed Claude CLI duration bleed across session files by resetting stopwatch timestamps per file.
• Prevented unbounded memory growth in Codex turn dictionaries with a 2-hour threshold and 5000-entry limit.
• Thread-safe locking in `UsageScanner.ScanAll()` across multiple UI triggers.

---

## APISwitch v0.3.0 Release Notes

### [Major New Feature · Usage Monitor]
• Summary cards: Total / Input / Output tokens and request counts over any selected range.
• Daily trend chart: Stacked bar chart with empty-bar placeholders for inactive days.
• Click-to-drilldown: Selecting a bar highlights the column and switches all window tables and cards to that day.
• Provider drill-down: Expand providers to inspect per-model consumption.
• Client breakdown: Statistics across Codex, Claude CLI, Claude Desktop, OpenCode, and Pi.
• Virtualized list for Recent Calls: Smooth scrolling over hundreds of records.
• Cross-provider model aggregation: Merges identical models across different providers with casing/suffix normalization.
• Persistent SQLite storage surviving application restarts.

### [New · Local Session Scanning]
• Scans offline activity from Codex (`~/.codex/sessions`), Claude Code (`~/.claude/projects`), Pi (`~/.pi/agent/sessions`), and OpenCode (`opencode.db`).
• Incremental byte-offset tracking avoids duplicate counts.

### [New · Dial-Style Date/Time Picker]
• Custom calendar panel with Monday start and month pagination.
• Dual-ring 24-hour clock dial and 60-minute selection dial with drag-and-click support.
