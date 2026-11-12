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

Run `ClaudeUsageBar.exe`. Right-click → **Start with Windows** to launch it at login.

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

Replace `C:/Users/YOU` with your home folder. Each session writes its state to `%LOCALAPPDATA%\ClaudeUsageBar\sessions\`. With several sessions open, the widget shows the one that most needs you.

## Notes

- **The widget never refreshes your login token.** Claude Code owns the refresh token, and rotating it from a second app could log Claude Code out. If the token expires, the tooltip asks you to open Claude Code once.
- The usage endpoint is undocumented and may change. The last response (usage numbers only, no credentials) is saved to `%LOCALAPPDATA%\ClaudeUsageBar\last-response.json` for troubleshooting.
- Memory use is ~50 MB, mostly the .NET runtime.

Not affiliated with Anthropic.
