# Repository Guidelines

## Project Structure
- `FuckDalamudCN.sln` is the main solution.
- `FastDalamudCN/` is the plugin project (core logic under `Controllers/`, `Network/`, `Windows/`, and `Utils/`).
- `FastDalamudCN/Assets/translations.json` is the shipped localization data.
- `FuckDalamudCN.Translator/` is a console helper that regenerates `translations.json`.
- `screenshot/` contains UI screenshots used in docs.

## Build, Test, and Development Commands
- `dotnet build FuckDalamudCN.sln` builds all projects.
- `dotnet build --configuration Debug FastDalamudCN/FastDalamudCN.csproj` builds the plugin for local testing.
- `dotnet build --configuration Release FastDalamudCN/FastDalamudCN.csproj` runs the Dalamud packager and produces a zip under `FastDalamudCN/bin/Release/FuckDalamudCN/`.
- `dotnet run --project FuckDalamudCN.Translator` runs the translation generator (update `ApiKey` in `FuckDalamudCN.Translator/Program.cs` before running).

## Coding Style and Naming Conventions
- C# with file-scoped namespaces, `Nullable` enabled, and implicit usings.
- Indentation: 4 spaces; braces on the next line.
- Naming: PascalCase for types and public members, camelCase for locals and parameters, `_` prefix for private fields. Keep filenames aligned to the primary type.

## Testing Guidelines
- There is no test project in the solution today, and CI only builds.
- If you add tests, create a separate `*.Tests` project, add it to `FuckDalamudCN.sln`, and run `dotnet test`.

## Commit and Pull Request Guidelines
- Recent commits use `type: subject` (for example, `chore: bump version to 2.0.0.6` or `refactor: update repository store lookups`).
- Keep subjects short and present tense.
- PRs should include a summary of behavior changes, commands run, linked issues if any, and screenshots for UI changes (store them under `screenshot/`).

## Configuration Notes
- `Directory.Build.props` sets `DALAMUD_HOME` to `%AppData%\\XIVLauncherCN\\addon\\Hooks\\dev\\`. Override it locally if your Dalamud dev path differs.
