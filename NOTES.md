# Project notes

Short hand-off so the context of how this app came to be survives. Not user documentation:
see `README.md` for that.

## What this is

Lintel AI is an Avalonia (.NET 8) desktop app that reviews a project folder file by file with a
local Ollama model, scores each file, classifies findings and lets you chat about one file.
It replaced an earlier Electron version (JS, `main.js` / `renderer/`), which no longer exists.

## Layout

| Path | What lives there |
| --- | --- |
| `src/LintelAI/Services` | scanning, `.AIIgnore`, prompts, Ollama client, analyzer, history, update check |
| `src/LintelAI/ViewModels` | `MainViewModel` (almost all app state), file / chat / history view models |
| `src/LintelAI/Views`, `Styles` | AXAML views, theme (`Theme.axaml`) and control styles |
| `tests/LintelAI.Tests` | xUnit tests for the logic (no UI tests) |
| `installer/`, `scripts/` | Inno Setup script and `build-installer.ps1` |

## Decisions worth remembering

- **Score is computed locally** from the findings (high -20, medium -10, low -5), never trusted
  from the model.
- **Every finding has a kind**: bug, vulnerability or smell. If a small model omits it, it is
  inferred from the finding's type.
- **Project index**: each file is reviewed together with the names it declares that other files
  use, and "never used" claims contradicted by the index are dropped.
- **Line numbers are snapped**: small models are often a few lines off, so a finding is moved to
  the nearby line that contains the identifier it names.
- **`.env`, `.pem`, `.key`, `.pfx` are always skipped**, whatever `.AIIgnore` says.
- **Update check is notify-only** (GitHub latest release vs `<Version>`); nothing is downloaded.
- **Data folder** is `%AppData%\LintelAI`; the old `AIProjectAnalyzer` folder is migrated once.
- **Syntax highlighting** is a small regex lexer (`SyntaxHighlighter`), not a parser.

## Verified vs not

Verified: build, 136 unit tests, full run and chat against a real local model
(`qwen2.5-coder:3b`), history restore, installer install / run / uninstall, update banner.

Not verified by hand: folder picker dialog, export save dialog, the Stop button mid-run,
macOS / Linux. The published exe and installer are unsigned (SmartScreen will warn).

## Not done

- Arabic / RTL interface.
- CI: build, test and publish the installer from a `v*` tag with GitHub Actions.
- Code signing; automatic updates (Velopack would be the next step).
- The repository name in `UpdateChecker.AppInfo.DefaultRepository` (`Crane4/lintel-ai`) must
  match the real GitHub repo, otherwise the update check quietly finds nothing.

## Handy commands

```powershell
dotnet run --project src/LintelAI
dotnet test
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

To ship a version: bump `<Version>` in `src/LintelAI/LintelAI.csproj`, build the installer and
publish a GitHub Release with the same number as the tag (`v1.0.1`).
