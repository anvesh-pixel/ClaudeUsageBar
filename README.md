# Claude Usage Bar

A tiny Windows 11 taskbar widget that shows your Claude **5-hour** and **weekly** plan usage next to the clock, plus a status light for Claude Code.

```
✳ 5h 49%  ┃  W 26%   ^  🔊  12:27
```

- 5h and weekly usage. Values turn orange at 80% and red at 95%.
- The divider shows Claude Code's state: **gray** idle · **coral (pulsing)** working · **amber** needs your input · **green** output ready (click the widget to clear it).
- Hover for reset times. Right-click for usage bars, update interval (30 s – 30 min), show-remaining mode, Start with Windows.
- Follows light/dark taskbar, hides behind fullscreen apps.
- One ~36 KB exe, no installer, no runtime to install (uses the .NET Framework built into Windows).

## Requirements

- Windows 10/11
- [Claude Code](https://claude.com/claude-code) logged in with a Claude account (Pro/Max). The widget reads Claude Code's login from `~/.claude/.credentials.json` (or `%CLAUDE_CONFIG_DIR%`) and calls the same usage endpoint as `/usage`.

## Build

```bat
build.cmd
```

This compiles `ClaudeUsageBar.cs` with `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. You don't need the Visual Studio or .NET SDK.

Run `ClaudeUsageBar.exe`. Right-click → **Start with Windows** to launch it at login. This creates a per-user Task Scheduler logon task, which needs no admin rights.

## Claude Code status light (optional)

Copy `hooks/usage-bar-status.mjs` to `~/.claude/hooks/` (needs Node.js) and add this to `~/.claude/settings.json`, merging with any hooks you already have:

```json
{
  "hooks": {
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" working", "timeout": 5 }] }],
    "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" working", "timeout": 5 }] }],
    "PreToolUse": [{ "matcher": "AskUserQuestion|ExitPlanMode", "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" input", "timeout": 5 }] }],
    "Notification": [{ "matcher": "permission_prompt|elicitation_dialog", "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" input", "timeout": 5 }] }],
    "Stop": [{ "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" done", "timeout": 5 }] }],
    "SessionEnd": [{ "hooks": [{ "type": "command", "command": "node \"C:/Users/YOU/.claude/hooks/usage-bar-status.mjs\" end", "timeout": 5 }] }]
  }
}
```

Replace `C:/Users/YOU` with your home folder. Each session writes its state to `%USERPROFILE%\.claude-usage-bar\sessions\`. With several sessions open, the widget shows the one that most needs you.

## Notes

- **Login renewal is handled by Claude Code itself.** When the token expires, the widget runs `claude -p "/usage"` in the background. That's a local command: no model call, no usage, and it doesn't start a 5-hour window. Running it makes Claude Code refresh its own token, with its own lock, so the two apps never clash. If the endpoint still fails, the widget shows the numbers parsed from that output. It only asks you to act if Claude Code is actually logged out.
- The usage endpoint is undocumented and may change. The last response (usage numbers only, no credentials) is saved to `%USERPROFILE%\.claude-usage-bar\last-response.json`, next to `widget.log`, for troubleshooting.
- Memory use is ~50 MB, mostly the .NET runtime.
- Data lives in `%USERPROFILE%\.claude-usage-bar`, not AppData. The Claude desktop app is a packaged (MSIX) app, and anything launched from it has its AppData and registry writes redirected into the package. That would split files between the widget and Claude Code's hooks.

Not affiliated with Anthropic.
