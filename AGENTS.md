# Repository Guidelines

## Project Structure & Module Organization

SongPlayer is a Windows desktop application built with WPF and .NET 10. `App.xaml.cs` is the composition root; `MainWindow.xaml` assembles the shell. Keep UI in `Views/` and its styles in `Themes/`, presentation state in `ViewModels/`, plain data in `Models/`, and reusable integrations in `Services/` (grouped by domain). `Controls/`, `Converters/`, and `Interop/` hold focused UI and Windows helpers. Architecture decisions and extension guidance are documented in `docs/ARCHITECTURE.md`.

## Build, Test, and Development Commands

- `dotnet build` — restore and build the solution for its configured `win-x64` runtime.
- `dotnet run` — build and launch the WPF application on Windows.
- `dotnet test SongPlayer.Tests/SongPlayer.Tests.csproj` — run the test suite.

Online Bilibili playback calls Bilibili's public web API directly from the app (`Services/Bilibili/`); local playback needs no network. There is no helper process, bridge or Node.js dependency to start. Lyrics come from LRCLIB (`Services/Lyrics/`).

## Coding Style & Naming Conventions

Follow the existing C# style: four-space indentation, nullable reference types enabled, PascalCase for types and public members, and `_camelCase` for private fields. Use descriptive domain names and keep services behind interfaces where view models consume them. Use CommunityToolkit.Mvvm patterns already present for observable state and commands. Keep XAML styles in the appropriate `Themes/` dictionary and prefer existing design tokens over literal colors or dimensions.

## Testing Guidelines

Build after changes with `dotnet build`, then run `dotnet test SongPlayer.Tests/SongPlayer.Tests.csproj`. Add new tests to the existing `SongPlayer.Tests` project and cover service and view-model behavior without requiring the live Bilibili API, the LRCLIB API or a sound card — inject a fake `HttpMessageHandler`, `ILrclibClient` or `IBilibiliSource` instead. Controls and themes are exercised by hosting the real view on an STA thread and asserting the WPF binding trace stays quiet. For UI changes, run the app on Windows and verify the affected page, bindings, and narrow-window layout.

## Commit & Pull Request Guidelines

Recent commits generally use `type(scope): summary`, such as `feat(audio): ...`, `fix(build): ...`, `refactor(shell): ...`, and `docs: ...`. Use that format with a concise, imperative summary. Pull requests should explain the user-visible change, note relevant build or manual checks, link an issue when applicable, and include screenshots for visual changes. Call out Windows-only or network-dependent verification where relevant.

## Architecture & Configuration

Keep dependency construction in `App.xaml.cs`; pass service interfaces into view models rather than constructing integrations in UI code. Do not persist expiring signed stream URLs or credentials. Library data is stored under the user's local application data directory; keep machine-specific build output and secrets out of commits.
