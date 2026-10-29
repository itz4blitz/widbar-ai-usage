# WidBar AI Usage

Windows 11 taskbar widgets for AI coding-plan usage and system sensors. [WidBar](https://apps.microsoft.com/detail/9PKLDNM83TP9) hosts them. This repo is the widget package.

Two widgets ship in one package, `Blitz.WidBarAiUsage`:

| Widget | Id | What it shows |
| --- | --- | --- |
| AI Usage | `com.blitz.aiusage` | Plan use and reset time for Claude, Codex, Grok, z.ai, Cursor, and OpenCode |
| System | `com.blitz.system` | CPU, GPUs, RAM, the `C:` disk, and fans |

Readings come from whoever is signed in on that PC. The package does not contain account names, tokens, or API keys. Optional keys typed into widget settings are stored with Windows DPAPI under `%USERPROFILE%\.config\widbar-aiusage\`.

## Requirements

- Windows 11, x64
- [WidBar](https://apps.microsoft.com/detail/9PKLDNM83TP9) from the Microsoft Store
- Developer Mode, so an unsigned package can be registered
- .NET 10 SDK
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) running its local web server on `127.0.0.1:8085`, for the System widget

WidBar is the taskbar host. Without it, the package installs and nothing draws the tiles.

## Install

```powershell
git clone https://github.com/itz4blitz/widbar-ai-usage.git
cd widbar-ai-usage
.\deploy.ps1
```

`deploy.ps1` publishes the widget and registers it for the current user. In WidBar, add **AI Usage** and **System** to the taskbar.

`setup\elevated-setup.ps1` turns on Developer Mode, installs the .NET 10 SDK if it is missing, and starts LibreHardwareMonitor at logon. Run it from an elevated PowerShell.

## Sign-in

Each provider reads the local app's own login. A missing login shows a dash until you sign in, or until you paste a key in that widget's settings.

| Provider | Where the login is read |
| --- | --- |
| Claude | Claude Code credentials |
| Codex | `%USERPROFILE%\.codex\auth.json` |
| Grok | `%USERPROFILE%\.grok\auth.json` |
| z.ai | `ZAI_API_KEY`, or a key saved in settings |
| Cursor | Cursor CLI `auth.json`, the IDE `state.vscdb`, or a session token saved in settings |
| OpenCode | OpenCode's local database or `opencode auth login` |

The disk tile follows the volume whose letter is `C:`.

## Icons

Provider and hardware marks are trademarks of their owners, used for identification. Sources and licenses are in [`package/Icons/SOURCES.md`](package/Icons/SOURCES.md).

## License

MIT. See [LICENSE](LICENSE). Widget icons keep the licenses named in `SOURCES.md`.
