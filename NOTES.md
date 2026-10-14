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
- **Updates have two paths.** A copy installed by the Velopack setup updates itself in place
  (`AppUpdater`, `MainViewModel.Updates.cs`): the banner offers "Update and restart", which
  downloads only the delta and restarts. Anything else (the old Inno installer, `dotnet run`) is
  not "installed" for Velopack, so it falls back to the notify-only `UpdateChecker` that links to
  the release page. The user always presses the button; nothing is applied silently.
- **A release is made by `scripts/release.ps1`**, not by Inno Setup (`installer/` and
  `build-installer.ps1` are the legacy path and are kept only for the old v1.0.0 installs).
  `vpk` is a pinned local tool (`.config/dotnet-tools.json`) and must match the `Velopack`
  package version in the csproj.
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
- Code signing. Without it SmartScreen warns on the first install, and `vpk` reports "files will
  not be signed" (use `--signParams` / `--azureTrustedSignFile` when there is a certificate).
- **Not verified end to end:** installing with the Velopack Setup and then updating from a real
  GitHub release. Packaging and launching the portable build were checked; the first real
  update will be the first real test, so publish a 1.1.0 and then a 1.1.1 to try it.
- Copies from the old Inno installer cannot update in place; they get the notify-only banner and
  must install the Velopack Setup once.
- `AppInfo.DefaultRepository` and `AppSettings.UpdateRepository` are `Crane4/Lintel-AI`; a
  `settings.json` saved earlier may still say `lintel-ai`, which GitHub treats the same.

## Handy commands

```powershell
dotnet run --project src/LintelAI
dotnet test
powershell -ExecutionPolicy Bypass -File scripts/release.ps1            # build only
$env:GITHUB_TOKEN = '<token, repo scope>'
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Upload    # creates a DRAFT release
```

To ship a version: bump `<Version>` in `src/LintelAI/LintelAI.csproj` (never reuse one that is
already released), run `scripts/release.ps1 -Upload`, check the draft on GitHub, then publish it.
The tag is `v<Version>`. Installed copies see it on their next start.
