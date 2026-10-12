# Emulate - notes for Claude

- **Always push finished work to `main`** (in addition to any working branch). Every push to `main` builds a GitHub
  release via `.github/workflows/release.yml`, and the app's Settings > "Check for updates" installs the latest release.
  Work that isn't on `main` never reaches users.
- Installer direct link (always the newest release):
  https://github.com/trendlinepros-afk/Emulate/releases/latest/download/Emulate-Setup.exe
- Build check: `dotnet build src/Emulate -c Release` (WPF builds on Linux thanks to `EnableWindowsTargeting`).
- Don't break the updater (`src/Emulate/Services/UpdateService.cs`) or the release asset name `Emulate-win-x64.zip`.
  Those are how every existing install receives fixes.
