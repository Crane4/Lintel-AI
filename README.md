# Lintel AI

A desktop app that reviews a codebase with a local [Ollama](https://ollama.com) model.
It reads every source file in a folder, asks the model what is wrong with it, scores each
file out of 100, and lets you chat with the model about any single file. Nothing leaves
your machine.

Built with .NET 8 and [Avalonia UI](https://avaloniaui.net).

## Running it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Ollama with
at least one model pulled (`ollama pull qwen2.5-coder:7b`, for example).

```powershell
dotnet run --project src/LintelAI
```

Tests:

```powershell
dotnet test
```

A single self-contained executable (no .NET install needed on the target machine):

```powershell
dotnet publish src/LintelAI -p:PublishProfile=win-x64
# -> artifacts/win-x64/LintelAI.exe
```

An installer (Start menu entry, optional desktop shortcut, uninstaller; per-user, no admin
rights needed). It requires [Inno Setup 6](https://jrsoftware.org/isdl.php):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
# -> artifacts/installer/LintelAI-Setup-<version>.exe
```

The version comes from `<Version>` in `src/LintelAI/LintelAI.csproj`. Uninstalling leaves
your settings, history and chats in `%AppData%\LintelAI` untouched.

For another platform pass a runtime identifier instead, for example
`dotnet publish src/LintelAI -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true`.
Only Windows has been tested.

## What it does

- **Scans a folder** for source, config and docs files. `node_modules`, `.git`, `bin`, `obj`,
  `dist`, `build`, lockfiles, minified bundles and binaries are skipped automatically.
- **Respects `.AIIgnore`** at the project root. The syntax is the same as `.gitignore`
  (`*.log`, `legacy/`, `docs/**/*.md`, `!keep.json`, …). `.env` files and key files
  (`.pem`, `.key`, `.pfx`) are always skipped, whatever the rules say.
- **Reviews each file** for memory leaks, infinite loops, dead code, useless logic, bad
  practices and security problems. Line numbers are sent with the code, so every finding
  points at the real line and shows a few lines of context, syntax-coloured in the same
  palette as VS Code (Dark+ / Light+).
- **Classifies every finding** as a *vulnerability* (something exploitable), a *bug*
  (misbehaves at runtime) or a *code smell* (works, but hard to maintain). If a small model
  forgets the field, the kind is inferred from the finding's type.
- **Knows the rest of the project.** Each file is reviewed together with the list of the
  other files and the names it declares that other files use, so exported functions are not
  reported as "never called". Any such claim that slips through is dropped before it reaches
  you.
- **Handles long files** by reviewing them in up to four overlapping parts with real line
  numbers, and says so when part of a file is still left unread.
- **Pins findings to the right line.** When a small model is a few lines off, the finding is
  moved onto the nearby line that actually contains the name it mentions.
- **Scores deterministically.** The score is computed from the findings
  (high −20, medium −10, low −5, floor 0) instead of trusting the model to do arithmetic.
- **Streams results in** file by file; you can read, filter, search and sort while it runs,
  and stop it at any time.
- **Chats about a file.** The conversation keeps its history per file, survives a restart,
  and renders code blocks with a copy button.
- **Keeps history** of the last 30 completed runs, including every finding. Open an old run
  to read it again, or compare its scores file by file against the current one. The last
  run for a folder comes back automatically when you restart.
- **Filters** by severity, by kind (vulnerabilities, bugs, smells) and by name.
- **Exports** the current results as JSON or Markdown.
- **Re-runs a single file** without redoing the whole project, and opens it in your editor
  or file manager from the detail view.

## Keyboard

| Shortcut | Action |
| --- | --- |
| `F5` | Analyze the project |
| `Ctrl+O` | Choose a folder |
| `Ctrl+F` | Search files |
| `Ctrl+H` | History |
| `Ctrl+E` | Export |
| `Ctrl+,` | Settings |
| `Esc` | Close a dialog |

## Updates

On start the app asks GitHub for the latest release of this repository
(`Crane4/lintel-ai`, changeable through `UpdateRepository` in `settings.json` for forks)
and compares its tag with the version it was built as (the `<Version>` in the csproj). If a
newer one exists, a notice appears with a **View release** button that opens the release page
in your browser. Nothing is downloaded or installed by the app. **Skip this version** silences
that release only, and **Don't show again** turns the check off until you re-enable it in
Settings.

- The request only reads a version number; no information about your files or machine is sent.
- It can be turned off in Settings, which also has a **Check now** button.
- To ship an update: bump `<Version>` in `src/LintelAI/LintelAI.csproj`,
  then publish a GitHub Release whose tag is the same number (`v1.0.1`).

## Settings

Open the gear icon to change the Ollama address, how many characters of each file are sent
to the model, and the per-file time limit. Settings, history and chats live in
`%AppData%\LintelAI`. If the address points at another machine, the sidebar warns
you that file contents will travel over the network.

## Layout

```
src/LintelAI/
  Models/        data types and scoring
  Services/      Ollama client, scanner, ignore rules, project index, prompts,
                 JSON repair, syntax highlighter, export
  ViewModels/    MVVM state (CommunityToolkit.Mvvm)
  Views/         Avalonia XAML
  Styles/        palette and control styles
tests/LintelAI.Tests/
```
