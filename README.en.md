# AgentCLIHub

**Unified workspace for AI coding agent CLIs**

Run your coding agents in one place and carry conversations and work between them.

[한국어](README.md) | **English**

A **Windows desktop app for running multiple Claude Code CLI, OpenAI Codex CLI, and Google Antigravity CLI sessions in tabs**.

It runs the real CLIs inside ConPTY (a pseudoconsole) and renders their output with xterm.js, like VS Code's integrated terminal. Slash commands, plan mode, approval prompts, MCP, and skills work **as they do in a normal terminal**. The app does four things:

1. Selects the CLI, working directory, and launch arguments.
2. Renders its screen.
3. Sends a turn from the bottom input box to the CLI, including attachments.
4. Lists previous conversations and lets you resume one.

## A folder means the working directory, not the conversation archive

The folder selected under **New conversation (＋ 새 대화)** is the **CLI's current working directory (CWD), or project root**. Conversations are not stored there.

| Item | Location |
|---|---|
| Selected folder | Files the CLI reads/edits; it automatically reads `CLAUDE.md` (Claude) or `AGENTS.md` (Codex). |
| Conversation history | **The CLI's own home**: `~/.claude/projects/<folder>/<session>.jsonl` or `$CODEX_HOME/sessions/<date>/rollout-*.jsonl`. |
| App settings | `%LOCALAPPDATA%\AgentCLIHub\settings.json`: open tabs, models, permissions, CLI paths, and bookmarks. |

**Pasted images and 📷 screen captures** are saved under `<folder>\.agentclihub-attachments\`. Clipboard images have no path to send through stdin, and the CLI cannot read outside its working directory without `--add-dir`. Files selected with 📎 are passed by path without writing a copy.
When **handing another CLI's conversation to Antigravity**, the app writes a handoff file under `<folder>\.agentclihub-handoff\`. Keep it while using that conversation: tab restoration and later transfers use it too.

## Interface

The top row contains New conversation, Resume, browser bookmarks, conversation tabs, and Settings. The selected CLI terminal occupies the main area. File/capture controls, the turn input and Send button, and the log are below it. Account usage (five-hour, weekly, and monthly windows, plus time to reset) remains visible at the bottom.

- **One tab = one CLI process = one folder.** Tabs can use different CLIs, labeled `CC`, `CX`, or `AG`. Status dots: green = running; red = ended; gray = starting.
- **New conversation:** Choose **Claude Code, Codex, or Antigravity**, then a folder, to start from scratch in a **new tab**. Choose the CLI each time rather than fixing one globally. The picker shows that CLI's configured model, effort, and permissions. Missing CLIs are disabled with a reason. The last selection receives focus: Enter repeats it; ↑/↓ move, 1/2 select directly, and Esc closes. Selecting the same folder still creates a new tab; use Resume to continue.
- **Resume (↻ 이어하기):** Shows history for all three CLIs, newest first, with the first message, time, and folder. A selection resumes in a **new tab** using `claude --resume <id>`, `codex resume <id>`, or `agy --conversation <id>`.
- **Settings (⚙ 설정):** Separate per-CLI model, effort, permissions, executable paths; browser bookmarks; and login options (guard Claude login, retain login, save passwords, clear saved login).
- **Usage strip:** Always visible, including in Settings and browser tabs, so remaining Claude/GPT quota is visible before picking a CLI. The active tab's account has a green border; usage turns yellow at 60% and red at 85%. Unavailable readings show `—`, with the reason on hover. Accounts for uninstalled CLIs are omitted.
- **Bookmarks:** Top-row buttons open addresses in **in-app browser tabs**. Add/remove any number of names and URLs in Settings; list order is button order. Hide retains the URL but removes the button. Browser profiles preserve login/SSO across app restarts. **Session-cookie sites** such as internal GitLab require Settings → Browser login → Keep login (enabled by default). Cookies with expiry times, such as Jira's, already persist normally.
- **Tab context menu:** Rename (also double-click), reset name, close, or close all other tabs. CLI tabs also offer **Continue with another CLI/account**, open folder, and copy path; browser tabs offer copy URL and reload. Names persist across restarts; clearing restores the automatic folder/document title.
- **Multiple accounts:** Add and authenticate accounts per CLI under Settings → Accounts. When one quota is exhausted, use the tab context menu to continue **in the same tab** with another account/CLI. Additional-account tabs include its name beside the badge, such as `CC Company`.
- **Tab ×** also terminates that tab's CLI.
- Restarting the app **restores the previously open tabs**, each through a resume operation.

### Interface language (1.7.0)

Select **한국어 / English** under Settings → Interface language (화면 언어). The choice applies immediately, saves automatically, and survives restart. Settings without a language default to Korean. It applies to menus, settings, accounts, conversation lists, transfer dialogs, and app-provided file/capture guidance. CLI output, conversations, user names, and entered values are not translated. Standard Windows dialog buttons and external websites follow their own language settings.

### Antigravity CLI (1.6.0)

New conversation → **Antigravity** → working folder launches `agy` in the built-in terminal. Existing CLI authentication is reused; if not authenticated, follow the terminal instructions. Executable discovery checks PATH for `agy.exe`, `.cmd`, `.ps1`, or `.bat`, then the official Windows path `%LOCALAPPDATA%\agy\bin\agy.exe`, then PowerShell. The `antigravity` IDE launcher is not used. Installed Git's `bin` path is added to the child environment to locate Git Bash for Windows tool execution.

- Antigravity has independent executable path, model, effort (`low/medium/high/max`), and permissions. Empty values follow CLI settings. Model/effort flags were verified with CLI 1.2.14; older versions may need updating. Since 1.6.2, models appear in a selector. Opening Settings runs the official [`agy models`](https://antigravity.google/docs/cli/headless/#select-a-model-effort-or-agent) command in the background. Select a display name; the CLI receives the ID. Reload refreshes immediately; direct input remains available. Query failures/list changes do not erase saved values.
- Permissions: CLI default, `--sandbox`, or `--dangerously-skip-permissions`. A custom PATH wrapper can apply its own options.
- Resume reads `~/.gemini/antigravity-cli/history.jsonl` and `cache/last_conversations.json`, showing only entries whose conversation `.db` or `.pb` remains. A selection opens with `--conversation <ID>`. Restart restoration uses `--continue` for the folder's latest conversation. Handoff tabs locate the session that received the handoff file.
- Since 1.7.0, **Continue with another CLI/account** supports transfers both ways with Claude/Codex. A handoff to Antigravity uses the configured model to read the file, acknowledge it, and wait for the next request.
- Login and usage are managed by the CLI (`/usage`). App-level extra accounts do not apply to Antigravity. Automatic browser guidance injection applies only to Claude/Codex.

Official guides: [installation/login](https://www.antigravity.google/docs/cli/install/) and [resume](https://www.antigravity.google/docs/cli/commands/resume/).

### Continue with another CLI/account

When an account's five-hour, weekly, or monthly limit is reached, transfer the conversation to **another account/CLI with available quota**. Tab context menu → Continue with another CLI/account → picker (first message, per-account usage, transfer method) → Enter. The tab's name, position, and folder stay; only its CLI changes.

| Transfer | Method | Prior conversation |
|---|---|---|
| Claude Code A → Claude Code B | `claude --resume <id>` using the same conversation file via B's config directory; shared `projects` | **Unchanged**, including tools and thoughts |
| Claude Code → Codex | Convert to a **new conversation** in Codex format, then `codex resume` | Requests, answers, tool calls/results as text |
| Codex → Claude Code | Convert to a **new conversation** in Claude format, then `claude --resume` | Same textual transfer |
| Codex A → Codex B | New converted conversation in B's home, then `codex resume` | Same textual transfer |
| Claude Code / Codex → Antigravity | New conversation reads the working-folder JSON handoff using `agy --prompt-interactive` | Requests, answers, and tool history as context |
| Antigravity → Claude Code / Codex | CLI text history converted to a new target-format conversation | Original handoff plus subsequent requests, answers, and tools |

**Account storage differs by CLI.**

- **Claude Code:** Each extra account gets a separate config directory: `CLAUDE_CONFIG_DIR=%LOCALAPPDATA%\AgentCLIHub\accounts\<id>\`. Run normal `claude auth login` there. When adding an account, choose claude.ai subscription (Pro/Max/Team/Enterprise), company SSO (`--sso`), or Console API billing (`--console`). Only login files (`.credentials.json`, `.claude.json`) are separate; default authentication remains unchanged. **`projects` (history/automatic memory), `file-history`, `plugins`, `skills`, etc. are shared through junctions**. At launch, `settings.json` and `CLAUDE.md` are synchronized from the default folder. User-scope MCP servers are inside `.claude.json`; their list is supplied separately using `--mcp-config`. Only initial setup writes onboarding completion and the default account's trusted folders; thereafter only Claude writes that account's `.claude.json`. Full permissions allow usage queries. Alternatively use `claude setup-token`'s one-year inference-only token through `CLAUDE_CODE_OAUTH_TOKEN` under Token. Normal authentication takes priority when present.
- **Company accounts:** Add Team/Enterprise through a claude.ai company email or SSO; add API-billed organizations through Console. Claude obeys managed `forceLoginMethod`/`forceLoginOrgUUID` policies on this PC. For example, `forceLoginMethod: console` blocks claude.ai login and setup-token.
- **Codex:** `auth.json` cannot be replaced by an environment variable, so each account has a separate **`CODEX_HOME`**, also under `%LOCALAPPDATA%\AgentCLIHub\accounts\<id>\`. On each launch, synchronize `config.toml` while retaining folders trusted only by that account, and synchronize `AGENTS.md`. Share `skills`, `plugins`, and sandbox directories (`.sandbox*`) through **junctions**. Separate sandboxes would make that account create a new sandbox, potentially changing the same Windows sandbox user's password and breaking the default account. **Never copy `auth.json`**: refresh on one side invalidates the other.

**Conversation conversion (`Transcript.cs`)** never alters the original; it always creates a **new file with a new ID**. Tool calls are rendered as text using Codex's external-agent markers (`[external_agent_tool_call: X] … [/external_agent_tool_call]`). A preamble records origin, marker meanings, and source path.

- Claude history is reconstructed backward through `parentUuid`. Rewound/discarded branches are excluded. At `/compact` boundaries, start from the summary, jumping from the retained segment's start to it. Parentless hook attachments written during resume reconnect to preceding history instead of ending traversal. Parallel-tool results are matched by call ID, because sibling results would be missed by chain traversal alone.
- Codex reads only what the model saw (`response_item`). Reasoning and remote compaction are **ciphertext bound to the issuing account**, so they are not transferred; even Codex → Codex is conversion rather than copying. Use the pre-compaction original text remaining in the file.
- Antigravity reads requests, responses, and tools from `brain/<id>/.system_generated/logs/transcript.jsonl`; its binary conversation database is not modified. If text is absent/unreadable, stop the transfer and retain the tab. For conversations started by handoff, include the original `.agentclihub-handoff` content too.
- Above 160,000 characters, preserve the opening summary and **first request**, trim the middle, and record omitted counts and the source path.

**Finding the current conversation:** Do not trust only the launch ID: `/clear` and `/resume` can change it. Choose the most recent history written since the tab started, excluding IDs held by **other tabs in the same folder**. The picker displays the first message for confirmation. Antigravity prioritizes a known session ID; handoff tabs locate the session receiving their handoff file.

**Failure preserves the tab.** Stop the old CLI only after conversion succeeds. For Claude's same-file resume, wait until the old process fully exits before starting the new one, preventing concurrent appends.

**Account login:** Settings → Accounts → Add account immediately opens a login tab (`claude auth login` in that config directory or `codex login` in that home). If browser approval displays a code, paste it into the terminal's `Paste code here`. The login page opens in an **InPrivate in-app tab**, visibly labeled, using a fresh session to prevent approval by an already signed-in claude.ai/Google account. Closing destroys that session; InPrivate/login-popup tabs are not restored after app restart.

**Login popups:** Browser `window.open` calls (Google, Microsoft, or company SSO) open as **new tabs with the opener preserved** through WebView2 `NewWindow`. Through 1.5.1, reopening only the URL lost the opener; the popup authenticated but could not return results, leaving a blank screen (claude.ai's Continue with Google is a popup). Self-closing popups close only their tab and return to the original tab. **WPF WebView2 normally closes its containing window on `window.close()`**, assuming one WebView per window; with all tabs in one main window, that would exit this app. The app removes that built-in handler per tab and rejects that close route at the main window if removal fails.
Token → Open setup-token reads the token displayed in the terminal, strips wrapped borders/control characters, and joins lines. Manual paste is offered if parsing fails. **An already signed-in browser account will approve the login**; log out or switch first.

A transferred tab restores **the same account** after restart (`Tabs[].Account`). Resume includes histories from Codex account homes and launches the selected account. New conversation offers a row per account.
The usage strip has one cell per account (`Claude`, `Claude·Company`, `GPT`, `GPT·Personal`). Claude uses that config directory's credentials without refreshing them; an idle account may show `—` until launched. Setup-token and Console accounts show `—`. If Codex history has no reading, query the same client endpoint, `chatgpt.com/backend-api/wham/usage` (**not a public API**).

### Input

| Action | Method |
|---|---|
| Send a turn | Type and press Enter or Send |
| Newline | Shift+Enter |
| Attach file | 📎 inserts the path into the text for the CLI to read |
| Paste image | Ctrl+V in the input or terminal (capture → paste) |
| Capture screen region | 📷 dims/freezes the screen; drag a region to attach the cropped image. Esc/right-click cancels. |
| Context menus | Inputs/settings: cut/copy/paste/select all/clear; terminal: copy/paste/select all/clear screen; log: copy all |
| Copy terminal text | Select then Ctrl+C; without selection it sends the normal interrupt. Ctrl+Shift+C always copies. |
| Paste into terminal | Ctrl+V |
| Operate TUI directly | Click the terminal and type; slash commands, arrows, and Esc pass through unchanged. |

## Run

```powershell
git clone https://github.com/OnTechnologyCo/AgentCLIHub.git
cd AgentCLIHub
dotnet build AgentCLIHub.sln
.\src\AgentCLIHub\bin\Debug\net9.0-windows\AgentCLIHub.exe
```

Build a distribution:

```powershell
dotnet publish src\AgentCLIHub\AgentCLIHub.csproj -c Release -r win-x64 --self-contained false
```

**Requirements:** Windows 10 1809 (build 17763) or later (ConPTY was introduced then), .NET 9 Desktop Runtime, Microsoft Edge WebView2 Runtime (usually already on Windows 11), and the `claude` or `codex` CLI you intend to use. Discovery checks process/user/system PATH and conventional install paths; otherwise specify it manually in Settings.

## File layout

| Path | Role |
|---|---|
| `AgentCLIHub.sln` | Solution |
| `src/AgentCLIHub/AgentCLIHub.csproj` | Desktop project |
| `App.xaml(.cs)` | Displays unhandled exceptions in a dialog |
| `MainWindow.xaml(.cs)` | WebView2, tabs, Windows dialogs |
| `ConPty.cs` | Pseudoconsole: pipes, resize, environment overrides |
| `CliProfile.cs` | CLI-specific discovery paths, flags, instruction files, environment variables to remove |
| `CliLocator.cs` | PATH → conventional paths → PowerShell discovery; directly invokes Node for npm wrappers |
| `CliCatalog.cs` | Sole model/effort/permission catalog and validation |
| `SessionStore.cs` | Reads history, including Codex account homes |
| `Settings.cs` | `%LOCALAPPDATA%\AgentCLIHub\settings.json` |
| `TerminalHost.cs` | One PTY per tab; paste/send, output batching, account environment, login tabs/token parsing |
| `Accounts.cs` | Account config/home preparation, synchronization/junctions, DPAPI tokens, safe deletion |
| `Transcript.cs` | Reads CLI histories, writes target formats, finds a tab's current conversation |
| `web/index.html` | Tabs, terminal, input, settings, history |
| `web/vendor/` | Locally bundled xterm.js, addon-fit.js, xterm.css for offline networks |

## Things to know

- **Settings apply to the next conversation.** Existing tabs are already running processes with their original arguments.
- **Models/effort differ by CLI.** Codex supplies `$CODEX_HOME/models_cache.json`; supported effort varies per model (for example `ultra` versus `xhigh`), so changing models changes the effort list. Claude accepts full model names beyond aliases such as `opus`/`sonnet`, even if absent from the list.
- Store independent fields (`ClaudeModel`, `ClaudeEffort`, `CodexModel`, `CodexEffort`). Sharing a field would send Claude's `opus` to `codex -m opus` and terminate it. Legacy settings with one backend/model/effort are migrated on first launch into the selected CLI's fields. The picker initially focuses that CLI, preserving an Enter-only workflow after updating.
- **Permission axes differ:** Claude has `--permission-mode`; Codex has sandbox (`-s`) and approvals (`-a`). They remain separate so displayed settings match the real flags.

- Claude and Codex both use the agent/browser bridge (`agentclihub-browser.exe` ↔ named pipe). Codex runs with a **write-restricted sandbox token**, and elevated mode may use `CodexSandboxOffline/Online`. Pipe access is expanded only to the current user, `CodexSandboxUsers`, and **the current logon SID**. Every request must also carry `AGENTCLIHUB_BROWSER_TICKET`, freshly generated for each app launch and injected only into its terminals: outside Codex sessions may share the sandbox account/SID. Wrong tickets are rejected and logged as warnings.
- Browser guidance is added through Claude's `--append-system-prompt-file` or Codex's `$CODEX_HOME/agentclihub.config.toml` `developer_instructions` plus `-p agentclihub`. This TOML basic string **must escape backslashes**. Through 1.4.0, `\P` in `C:\Program Files\…` broke Codex configuration loading.
- **A new folder triggers the CLI's trust prompt.** Answer with arrows/Enter in the terminal; the app does not decide for you. Codex 0.155 asks even when `-a`/`-s` are supplied.
- After granting folder trust, a PC without a chosen Windows sandbox can show **Set up sandbox**. Completing it overrides **that session's** permissions with Codex defaults (on-request/workspace-write), after the app's arguments. Close/reopen the tab to use configured permissions; the sandbox choice is retained. The first Codex tab on such a PC logs this explanation once.
- **Codex launched with `-p agentclihub` writes trust, sandbox choice, and `/model` to `agentclihub.config.toml`, not `config.toml`.** Through 1.5.2 the app rewrote the whole file each launch (each account-tab launch for account homes), losing these choices and repeatedly prompting on other PCs. Since 1.5.3 only `developer_instructions` is merged; other content stays. If `.sandbox/setup_marker.json` proves elevated setup but no selection exists anywhere, restore `[windows] sandbox = "elevated"` to recover a value erased by the older app.
- **Forced app termination can leave CLI children running.** Normal window closing cleans them up. If needed, terminate remaining `claude.exe`/`codex.exe` in Task Manager.
- **Codex elapsed-time updates freezing until a click (1.5.4 compatibility handling):** Reproduced with Codex 0.159.3/ConPTY: output itself stops without input, so repainting is insufficient. After 500 ms without output, send terminal FocusIn (`CSI I`) only to the visible Codex tab that requested focus reporting. Suppress during normal output, other tabs, Settings/pickers, login, after exit, or when the app is inactive. At most twice per second; retain input focus and drafts.
- **Codex executable discovery improvements (1.6.1):** With no manual path, first run PowerShell `Get-Command codex` and launch in the same profile environment. Resolve official `bin → current → releases/<version>` links to the actual `codex.exe` each time. If PATH/link folders are missing, check `CODEX_HOME` or default `~/.codex` for `packages/standalone/current` and `auto-update-version`. Do not persist an automatically resolved version path. Log selected path/discovery method, and Windows error number/message on failure. A valid manually specified path remains first priority.
- **Codex auto-discovery and repeated output fix (1.5.5):** Refresh user/system PATH even when the CLI was installed after app startup, then check official/npm locations and PowerShell `Get-Command`. npm uses `node + bin/codex.js`; other `.cmd`/`.ps1` launch through PowerShell. Empty settings paths trigger discovery each time; a fixed `releases/<version>/bin/codex.exe` stays on that old version. Leave the field empty to follow updates. Log opens as an overlay without resizing the terminal; resize events are batched for 200 ms and only the final size is sent. Codex receives `tui.alternate_screen=always` and `tui.fullscreen_transcript=true` (measured with 0.159.3), without editing user configuration. Older CLIs lacking fullscreen transcript support need updating.

## Verified behavior (measurements)

The following were actually run on the development PC; these are the original project's recorded results, not new tests performed during this translation.

- `dotnet build`: zero warnings/errors.
- PATH discovery found both `claude.exe` and `codex.exe`.
- Codex effort lists genuinely differed: `gpt-5.6-terra` through `max,ultra`; `gpt-5.5` through `xhigh`.
- Claude Code TUI rendered through ConPTY: truecolor SGR, cursor motion, OSC-8 links, and trust prompt.
- Codex TUI rendered: `╭─ >_ OpenAI Codex (v0.152.1) ─╮` banner, working folder, input prompt, synchronized output (`?2026`), and cursor-shape control.
- `term.in` answered trust and started a session. Bottom-input turns submitted as bracketed paste plus CR; the CLI answered “1+1? Number only” with `2`.
- Folder selection created tabs; history selections resumed via `--resume <id>` in a new tab; closing terminated only that CLI; startup restored tabs using `--continue`. Transcript inspection confirmed resumed context.
- Settings rendered CLI, model, effort, permissions, and discovery status.
- Bookmarks migrated old fixed fields (such as JiraUrl) to a list; add/delete/order/hide updated the top row immediately; saving used only `Bookmarks`, removing old keys.
- 📷 capture froze the combined two-monitor desktop; a 500×250 selection matched the crop, which appeared with preview in attachments.
- Internal GitLab's `_gitlab_session` session cookie survived restart, even without opening tabs. Disabling retention deleted `session.dat`.
- Context-menu Paste preserved Korean text through host clipboard access. Terminal menus correctly ignored xterm's hidden textarea. Renames persisted as `Name` in settings.json.
- Agent browser `shot` activated an offscreen tab before capture and continued processing afterward; previously it deadlocked the bridge until app restart.

**Per-conversation CLI selection:** Host checks used a harness linked to original files (33 cases); headless Edge/fake host used real mouse/keyboard input (60 cases).

- Legacy `Backend=claude_code`, `Model=opus`, `Effort=max` migrated to `ClaudeModel`/`ClaudeEffort` and removed old keys; Codex migrated to its own fields; new fields won in mixed files.
- Each launch used only its own settings: filling Claude fields did not add arguments to Codex, and vice versa.
- Picker preserved host order, last-CLI focus, arrow cycling, Enter/1/2 selection in `tab.new`; Esc/outside click sent nothing. Double-click did not immediately close it. Missing CLIs were disabled. Native browser content hid while open and returned on close.
- Settings had separate CLI cards; Codex model changes updated its effort list. Saving included per-CLI fields and `GuardClaudeLogin`, without old keys.
- Usage remained visible in Settings/browser tabs, highlighted only the current account, and changed color at 60/85%.

**Resume and restart:** Command lines generated from read-only access to actual settings/history (51 Claude, 9 Codex conversations) were accepted by installed Claude 2.1.270/Codex 0.154.0 with `--version` appended to validate arguments **without opening sessions**. Accepted forms included `claude --resume <id> --model opus --effort max --permission-mode bypassPermissions --append-system-prompt-file …`, `claude --continue …`, `codex resume <id> -p agentclihub`, `codex resume --last …` (only with folder history), and fully specified Codex model/effort/sandbox/approval flags. Version 1.3.1 incorrectly passed Claude's `opus/max` fields to resumed Codex because fields were shared. UI checks covered a 60-item history, filtering, selected IDs in `tab.resume`/`term.start`, Esc/Close, and native browser hiding.

All permission choices were passed to installed CLIs. Claude accepted all six; **Codex 0.154.0 rejected `untrusted` approvals**, exit 2 (`invalid value 'untrusted' for '--ask-for-approval'`). It was removed from choices and filtered from saved legacy values to avoid immediate tab exit.

**The `--version` test's limitation:** It checks arguments but **does not read the `-p` profile**, missing 1.4.0's `agentclihub.config.toml:10:5: missing escaped value`. Configuration-loading checks in 1.4.1 covered:

- `codex -p agentclihub mcp list`: the old file reproduced the user's error; the corrected file loaded. Python `tomllib` round-tripped guidance exactly, including `"""`, `'''`, trailing backslashes, and unmatched CR.
- `codex -p agentclihub debug prompt-input`: guidance appeared with the developer role and intact paths.

**Codex browser bridge:** A fake app linked to the actual new bridge code exposed tab `FAKE-TAB-7Q`.

- The helper ran under `codex sandbox` (elevated/unelevated): old 1.4.0 permissions produced access denied; corrected permissions passed. Token inspection identified restricted write tokens whose restricting list contained account/logon SID/Everyone rather than just groups.
- Missing/wrong tickets were rejected inside and outside the sandbox, with warnings.
- One **actual `codex exec`** read the guidance, ran `agentclihub-browser.exe tabs` inside the sandbox (175 ms), and answered `FAKE-TAB-7Q`, confirming bridge/ticket environment reached the shell.
- The trimmed helper actually shipped in 1.4.1 gave the same result.

**Account transfer (1.5.0), Claude 2.1.278 / Codex 0.155.0:**

- `CLAUDE_CODE_OAUTH_TOKEN` precedes stored login: an invalid token returned `401 OAuth access token is invalid`; status showed `authMethod: oauth_token` without mixing the default email/plan.
- **Codex → Claude:** An actual Codex history with 28 tool calls was converted and opened using `claude -p --resume <new-id>`. Questions about the first request, previous tools, and origin were answered correctly.
- **Claude → Codex:** Actual history (3 user messages, 195 tool/result pairs) converted and opened with `codex resume <new-id>` in a pseudoconsole with temporary status DB. Previous conversation rendered and waited for input; IDs absent from the DB were found by filename.
- Claude config-directory checks (1.5.1): login opened the claude.ai full-permission URL/code entry; prepared account status showed no login, while default stayed `claude.ai · max`. The default caveman plugin and junction-shared histories were visible. Console detection (`primaryApiKey`), initial-only onboarding/trust writes, untouched account `.claude.json` thereafter, and user MCP via `--mcp-config` were checked. Deletion preserved default `projects`, `plugins`, `skills`, and `file-history`. `mklink` failed with slash paths; normalizing paths fixed it.
- Login popups (1.5.2, reported blank screen after Google login): fresh-profile Edge confirmed Continue with Google opens an **opener-linked popup**. The CLI's BROWSER URL used `redirect_uri=http://localhost:<port>/callback`. A real WebView2 runtime-153 test window linked to `BrowserHost.cs` exercised 34 simulated popup checks: cookie creation, opener postMessage, self-close, NewWindow linkage, hidden-origin result delivery, original-window survival, cookie visibility in normal/InPrivate modes, and bidirectional profile isolation. Reattaching the built-in handler still met the main-window guard; the old implementation reproduced missing opener/results. The first test revealed WPF's whole-window closing behavior. The helper sent `open <url> --private` only for login tabs through a fake bridge; UI showed InPrivate and returned to the opener rather than the last tab.
- Prepared Codex account homes reported `Not logged in` (no default credential leak), loaded default settings/MCP with `mcp list`, and included guidance in `debug prompt-input`.
- Account deletion removed only junctions (`skills`, `plugins`, `.sandbox*`); **default-home file counts remained unchanged**, including sandbox secrets.
- Real history parsing recovered 196/196 parallel Claude results (chain-only parsing missed 28), started a five-compaction conversation from its summary, and continued past parentless resume-hook attachments. Checked Codex 0.155 remote compaction/omitted AGENTS/developer guidance, legacy imported history, and 0.152 TUI logs. The 160,000-character budget retained the first request and final fragment.
- Nine setup-token screen layouts covered wrapping, borders, cursor movement, OSC links, partial screens, and final screens. Tokens were not stored in plaintext. Config synchronization retained account-only trust and was idempotent.
- Headless Edge/fake-host UI checks (66) covered account badges, picker context/usage and same-file versus conversion labels, disabled unauthenticated accounts, keyboard controls/focus during usage refresh, same-tab `tab.switched`, no login-tab menu, account-aware new/resume lists, account-card add/login/password-masked token/name/two-click deletion, and zero page errors.

**Codex ignoring configured sandbox/approval (1.5.3, reported on another PC), Codex 0.155.1:**

- Not reproduced on this PC: exact app arguments (`… -s danger-full-access -a never -p agentclihub`) gave `/status` = `Full Access` for both new and resumed sessions, including histories recorded as read-only/on-request. This PC already had `[windows] sandbox = "elevated"` and trusted folders from separate Codex use.
- Granting trust with `-p agentclihub` demonstrably wrote `[projects.…]` to **`agentclihub.config.toml`**, leaving config.toml unchanged.
- Source inspection (rust-v0.155.1) confirmed trust prompts ignore `-a`/`-s` when no trust_level exists; newly trusted Windows folders without sandbox setup trigger setup; setup overwrites the session with `auto` (on-request/workspace-write); `-p` directs writes to the profile file.
- Merge harness: 27 cases covered 1.5.2 headers, appended trust/sandbox fields, top-level `/model`, `[` lines inside multiline strings, same-name table keys, restore-only-if-absent within existing `[windows]` (not for old switches/dotted syntax), and idempotence. All outputs parsed with `tomllib`. Guidance detection covered empty homes, trust-only, profile choice, legacy switch, and this PC's default home.
- Merging the real profile retained guidance verbatim and trust; actual Codex then started **without a trust prompt** and with `Full Access`.
- The first-time sandbox setup screen on a new PC was not reproduced: this PC was already configured, and resetting it would require changing user config or copying authentication, which was not done.

**Codex elapsed-time updates (1.5.4):** Actual Codex 0.159.3, original `ConPty.cs`, HTML/xterm, and headless Edge used a temporary Codex home and a delayed local Responses server. No actual account/model call; `--no-daemon` avoided a long test-home socket path.

- Previously `Working (2s)` stayed until a click jumped to `Working (15s)`; no ConPTY bytes arrived in between. It was not only a frozen xterm or locked synchronized-output mode.
- Corrected HTML updated elapsed time for over 20 seconds without another click while focus stayed in the bottom input. This is app compatibility handling, not a confirmed root cause inside Codex.
- `python -m unittest discover -s tests -v`: eight browser regression checks passed (Python Playwright + Edge required). Checked signal type/rate, draft/focus retention, suppression during normal output, active-tab distinction, login/start/exit/account switch, disabled focus reporting, hidden/inactive state, Settings/browser/pickers.

**Codex launch/resize (1.5.5):**

- `dotnet run --project tests/HostChecks/HostChecks.csproj -c Release`: checked actual discovered Codex 0.159.3, stale process PATH, deleted explicit paths, precedence, npm test-JS wrappers, generic cmd/ps1, Korean arguments/exit codes, PowerShell discovery/launch, and screen-mode arguments for new/resumed sessions.
- Python unittest: 11 passed; added log-overlay dimension preservation, batching repeated resizes once, and hiding/restoring native browser content while the log opens.
- Actual Codex/ConPTY/HTML/xterm/local test server rendered 50 lines, then handled four window resizes. The fullscreen buffer stayed; history did not duplicate in scrollback; the final response appeared once. Opening the log preserved rows/columns/content, and mouse-wheel history remained available. No real account/model API was used.

**Antigravity integration (1.6.0):**

- HostChecks: 39 passed, covering `agy.cmd` discovery/actual version, stale PATH, Git Bash, new/resume arguments, CLI-setting separation, spaces/quotes/backslashes in model names, permission/effort validation, merged history/cache, and excluding deleted histories.
- Python unittest: 14 passed, covering Antigravity selection/AG badge/send, settings/path selection without altering Claude/Codex paths, resume into Antigravity, and omitting unsupported account-transfer menus. Existing Codex wake/log-overlay/resize checks also passed.
- Actual TerminalHost/ConPTY/bundled HTML/xterm launched discovered `agy.cmd` → PowerShell → Antigravity 1.2.14; recognized existing login, accepted Korean input, retained drafts after resize, and opened help via `term.send` `/help`. The CLI itself updated 1.0.1 → 1.2.14 during testing. A test working directory was used; no model request was sent.

**Codex discovery improvements (1.6.1):**

- 46 host and 14 UI checks passed: PowerShell discovery, two-stage junction resolution, new-file selection after update, version-file fallback for missing links, and preserved Windows launch errors.
- Actual TerminalHost → PowerShell → Codex 0.159.3 → ConPTY → xterm used a separate test home/local response server to check responses, four resizes, and Log. Auto-selected a version-directory path with no repeated conversation output. No actual OpenAI model call.

**Antigravity model selection (1.6.2):**

- 53 host and 16 UI checks passed: 14 models from actual `agy models`, display-name/ID handling, refresh, query failure/cancel, ignoring stale responses, and preserving settings before save.
- Actual CLI 1.2.14 accepted queried ID `gemini-3.8-flash-high` through `--model` and displayed Gemini 3.8 Flash. No model request.

**Antigravity transfers/interface language (1.7.0):**

- 71 host and 23 UI checks passed. Two existing junction-discovery checks were skipped because this environment could not traverse Windows junctions; actual Codex executable discovery through the installed version-file fallback passed.
- Checked bidirectional Antigravity selection, `.db`/`.pb` session discovery, transcript parsing, tool-history retention, preserving original/new turns through repeated handoffs, restoration, and untouched originals.
- Actual `agy.cmd` → PowerShell → Antigravity 1.2.14 → ConPTY/xterm received a test handoff. The model read the JSON and acknowledged the project name `Amber Otter`; that actual transcript was converted back into Claude/Codex histories in a separate test folder. A newline-argument truncation in `.cmd` was found; the initial handoff prompt became one line with a regression check.
- Checked Korean↔English switching, saved-language/first-screen restoration, Korean fallback for invalid values, settings/dialog/error translation, and preservation of model drafts, input, user names, and terminal text.

### Not yet verified

- **Completing an actual Google login inside the app.** Popup behavior used simulated login windows with real WebView2; authenticating/authorizing claude.ai with a real Google account required a user account and was not attempted.
- **Actual second-account login and transfer.** No second Claude/ChatGPT account existed on this PC, so full claude.ai/SSO/Console/setup-token auto-save/account-home `codex login`, followed by a turn on account B, was unverified. Login-page display, simulated-screen token parsing, invalid-token and unauthenticated-folder launches were checked. Default credentials were not copied to test accounts because refresh could invalidate the other login.
- **An actual turn in a converted Codex conversation.** Prior history rendered and input was ready, but the PC's free Codex plan had 91% monthly usage, so no model call was made. Claude's side did complete a real turn.
- **The complete installed-app click-through.** Host and UI were tested separately.
- **Actual browser tabs (Jira/GitLab) controlled from the installed 1.4.1 Codex tab.** The new bridge was tested in a fake app; bridge, helper, guidance, and Codex sandbox were real, but the actual application window/tabs were not.
- **The full new-CLI picker workflow in the installed app**, from picker → folder dialog with CLI name → CLI tab. The development app was running within an installed-app tab; opening another window would share settings.json and restore the current conversation through `--continue`.
- **A real OpenAI-model Codex turn.** Codex TUI send/receive used a local response server, not the actual model service.
- The npm Claude wrapper (`claude.cmd` → `node cli.js`) fallback: this PC had native `claude.exe` and did not take that path.
- Real-conversation attachments (📎 or pasted images): implemented but not tried in an actual conversation.
- Checks used **UI Automation/accessibility trees and PTY byte streams**, not visual inspection. Text/state/processes matched, but a person still needs to inspect colors, spacing, and other visible appearance.

## Public release and licensing

Original code and documentation use the [MIT License](LICENSE), allowing modification, commercial use, and redistribution while retaining notices. Third-party terms remain in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [licenses/](licenses/).

**Publication status:** the source was made public on 2026-10-10 at the maintainer's request. The current Claude credential backup/storage and direct internal usage API calls remain review items requiring remediation or a verified permitted basis. Read the unresolved items in the [release review](docs/PUBLIC_RELEASE_REVIEW.md). Publication or adding a license does not certify service-term compliance or legal safety.

[Contributing](CONTRIBUTING.md) · [Security reporting](SECURITY.md) · [Legal and service terms](LEGAL.md) · [Local data and privacy](PRIVACY.md)

## 1.8.1 consistent naming and automatic upgrades

The project name is **AgentCLIHub**. Run `AgentCLIHub-Setup-1.8.1.0.exe` to upgrade;
uninstalling the previous version first is unnecessary. The application is `AgentCLIHub.exe`.

Before installation, Setup detects the installed path/version and checks settings and file locks.
Accounts, preferences, and browser storage move from `%LOCALAPPDATA%\LlmTools` to
`%LOCALAPPDATA%\AgentCLIHub`. File hashes and link targets are verified, then settings paths under
the old root are converted. Existing encrypted data and authentication files retain their bytes and keys.

The previous installation directory moves to the new location. After success, legacy executables,
browser helper files, shortcuts, and temporary migration receipts are removed. No old data directory remains.
Cancellation restores the original paths/settings; conflicting destinations and locked files stop the
upgrade without overwriting user data. The original installer AppId avoids duplicate product registration.

Existing project attachments, handoff files, and provider-owned conversation histories retain their
references. Display and installer names, source namespaces, and project directories now use `AgentCLIHub`.
New attachments and handoffs use `.agentclihub-attachments` and `.agentclihub-handoff`.
Codex uses `-p agentclihub` and `agentclihub.config.toml`; its first creation preserves trust,
sandbox, and model choices from `llmtools.config.toml`, leaving the original file intact.
Browser bridge environment variables use `AGENTCLIHUB_BROWSER_*`.
Legacy names remain only where needed to detect older installations/data, read old handoffs,
and decrypt existing credentials and cookies.

Run migration checks with `dotnet run --project tests/MigrationChecks/MigrationChecks.csproj -c Release`.
Run rename compatibility checks with `dotnet run --project tests/RenameChecks/RenameChecks.csproj -c Release`.

## 1.8.2 elevated installer fix

Fixed exit code `-532462766` when the unelevated migration helper could not create its report in
Setup's protected temporary directory. Setup now pre-creates the report and grants write access
only to that file. The helper checks report access before starting data operations and returns a
controlled error if reporting fails. Close the running app and retry with `AgentCLIHub-Setup-1.8.2.0.exe`.

Run report checks with `dotnet run --project tests/InstallerChecks/InstallerChecks.csproj -c Release`.
Installer builds also test the actual published helper with a read-only temporary directory before
packaging. These checks do not modify the user's installation or application data.

## 1.8.4 Claude account resume and history context menu

- Claude account transfers now resume the original transcript by absolute path, including when account history directories are separate or UUID lookup skips a junction.
- History includes account-specific Claude transcripts and excludes subagent transcripts. Missing selected sessions are not replaced with unrelated conversations.
- Open **Resume → right-click a conversation → Continue with another CLI/account… → select an account** to continue in a new tab. A normal click still resumes with the original account.
- Cross-CLI imports into Claude are stored in the selected account's history directory. Login credentials are never copied.

Update with `AgentCLIHub-Setup-1.8.4.0.exe` and restart the app.

## 1.8.3 agent browser tab cleanup

Agent guidance now requires closing task tabs it opened after gathering the required information
or finishing the task, then verifying cleanup with `tabs`. Existing user tabs and tabs explicitly
requested to stay open are preserved. The `open` response includes the tab's `closeCommand`.
Fixed `close` removing the native browser while leaving its tab visible in the UI.
Update with `AgentCLIHub-Setup-1.8.3.0.exe` and restart the app to apply the guidance to CLI launches.
