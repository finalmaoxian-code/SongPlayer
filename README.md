# SongPlayer

A focused desktop music player for local libraries, built with WPF on .NET.

The interface is deliberately quiet: a narrow sidebar for navigation, a single page
surface for content, and a persistent player bar that never competes with the music.
Clicking the artwork in the player bar opens an immersive *Now Playing* view with the
cover art and the lyric sheet, and closing it returns you to the page you came from.

## Requirements

- .NET 10 SDK (or the runtime matching the `TargetFramework` in `SongPlayer.csproj`)
- Windows 10 version 1809 or later

## Getting started

```bash
dotnet build
dotnet run
```

## Documentation

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — full architecture review: layer-by-layer
  breakdown of every file, the binding map, an implementation-completeness table, the
  rationale behind each design decision, known limitations and extension recipes.

## Project layout

```
SongPlayer/
├─ App.xaml                 Application entry point; merges themes and registers converters
├─ MainWindow.xaml          Window chrome, sidebar, page host, player bar and the
│                           full-window Now Playing overlay
├─ Themes/
│  ├─ Colors.xaml           Design tokens: colors, brushes, radii, font sizes
│  └─ Controls.xaml         Reusable control styles built on those tokens
├─ Models/
│  ├─ PlayerPage.cs         The pages the shell can display
│  ├─ Track.cs              Immutable track description
│  ├─ LyricLine.cs          One timestamped lyric line
│  └─ SampleLyrics.cs       Placeholder lyrics until .lrc parsing lands
├─ Controls/
│  └─ SlimSlider.cs         Self-drawing seek / volume bar
├─ Interop/
│  └─ WindowMaximizeHelper.cs  Keeps a maximized borderless window inside the work area
├─ ViewModels/
│  ├─ MainWindowViewModel.cs  Navigation, playback and lyric state for the shell
│  └─ LyricLineViewModel.cs   A lyric row with its own highlight flag
└─ Converters/
   └─ BoolToVisibilityConverter.cs  Boolean to Visibility (and inverted) conversion
```

## Architecture notes

- **MVVM, no code-behind logic.** `MainWindow.xaml.cs` only handles window chrome
  behaviour (minimize, maximize, restore glyph) and keeping the active lyric line
  scrolled into view. All state lives in `MainWindowViewModel` and is exposed with
  `CommunityToolkit.Mvvm` source generators.
- **Design tokens first.** Colors, brushes, corner radii and font sizes are declared
  once in `Themes/Colors.xaml`. Views reference tokens rather than literal values, so
  retheming means editing one file.
- **Pages are views inside one window.** Home and Local Music are toggled by visibility
  bound to the view model, which keeps navigation instant and avoids a
  `Frame`/`NavigationService` for what is currently a two-page shell. The sidebar items
  bind `IsChecked` two-way to `IsHomeSelected` / `IsLibrarySelected`; the setter is what
  navigates, so the highlight and the content can never disagree.
- **Now Playing is an overlay, not a page.** It is driven by a separate
  `IsNowPlayingOpen` flag rather than a `PlayerPage` member, which means it can cover the
  sidebar and player bar without becoming a navigation target, and closing it always
  reveals whatever page was underneath. `Esc` and the back button both close it.
- **Borderless windows need help when maximized.** `WindowChrome` makes WPF size a
  maximized window to the whole monitor plus the invisible resize border, which slides
  the bottom of the window under the taskbar. `Interop/WindowMaximizeHelper` answers
  `WM_GETMINMAXINFO` with the work area of whichever monitor the window is on, so the
  player bar stays reachable on every screen.
- **Lyrics are data, not view state.** The overlay renders whatever the view model puts
  in its `Lyrics` collection; each row owns an `IsActive` flag, so advancing the highlight
  touches only the two rows that change. Clicking a line seeks to it, and the code-behind
  scrolls the active row into view. A real `.lrc` parser can replace
  `Models/SampleLyrics.cs` without any view changes.
- **The sliders are custom-drawn.** The stock WPF `Slider` chrome is heavy and its
  `Track`/repeat-button plumbing is awkward to restyle, so `Controls/SlimSlider.cs`
  paints its own track, filled portion and hover thumb while inheriting all behaviour
  (keyboard stepping, click-to-seek, drag, data binding) from `Slider`.

## Status

This is an early preview. The shell, theming, player bar and the immersive Now Playing
overlay are in place; library scanning, playback, playlist editing and real
artwork/lyric loading are the next milestones. Controls that are not yet implemented are visibly disabled rather
than silently inert.
