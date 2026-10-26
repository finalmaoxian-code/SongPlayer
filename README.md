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
├─ App.xaml                 Theme merge + converter registration
├─ App.xaml.cs              Composition root: builds the services and the shell view model
├─ MainWindow.xaml          Window chrome + the placement of the seven composed views
├─ Views/
│  ├─ CaptionChrome.cs      Shared caption behaviour for both caption strips
│  ├─ Shell/                CaptionBarView, SidebarView, PageHeaderView, PlayerBarView
│  ├─ Pages/                HomePageView, LibraryPageView
│  └─ Overlays/             NowPlayingView (also owns the lyric scrolling)
├─ Themes/                  Design system, one dictionary per concern
│  ├─ Colors.xaml           Design tokens: colors, brushes, radii, font sizes
│  ├─ Typography.xaml       Text styles
│  ├─ Surfaces.xaml         Card and well surfaces
│  ├─ Buttons.xaml          Icon, play, artwork, secondary and caption buttons
│  ├─ Inputs.xaml           Search box
│  ├─ Navigation.xaml       Sidebar nav item
│  ├─ NowPlaying.xaml       Artwork frame, lyric rows, chips
│  └─ Library.xaml          Track table: headers, row states, cell styles
├─ ViewModels/
│  ├─ MainWindowViewModel.cs        Navigation state + mediator between the children
│  ├─ Shell/PlayerBarViewModel.cs   Track metadata, transport, position (single source)
│  ├─ Shell/NowPlayingViewModel.cs  Lyrics and the active-line index
│  ├─ Pages/LibraryPageViewModel.cs Import, persistence, track list, selection
│  └─ LyricLineViewModel.cs         A lyric row with its own highlight flag
├─ Models/                  Track, PlayerPage, LyricLine, TimeSpanFormatting — plain data, no WPF
├─ Services/
│  ├─ Audio/                IAudioPlayer + MediaPlayerAudioPlayer (real engine)
│  ├─ Library/              Scanner, tag reader, JSON store, facade over all three
│  ├─ Storage/              IFolderPicker + the Win32 folder dialog behind it
│  └─ Lyrics/               ILyricProvider + sample provider
├─ Controls/
│  └─ SlimSlider.cs         Self-drawing seek / volume bar
├─ Interop/
│  └─ WindowMaximizeHelper.cs  Keeps a maximized borderless window inside the work area
└─ Converters/
   ├─ BoolToVisibilityConverter.cs  Boolean to Visibility (and inverted) conversion
   └─ InverseBoolConverter.cs       Boolean negation, for IsEnabled bindings
```

## Architecture notes

- **MVVM, no code-behind logic.** `MainWindow.xaml.cs` only keeps the caption from
  stealing focus, hooks `WindowMaximizeHelper`, and closes the overlay on `Esc`; the caption
  buttons and the lyric scrolling live next to the views that own them
  (`Views/CaptionChrome.cs`, `Views/Overlays/NowPlayingView.xaml.cs`). All state is in the view
  models and is exposed with `CommunityToolkit.Mvvm` source generators.
- **One DataContext, three child view models.** The window is bound to `MainWindowViewModel`,
  which composes `PlayerBar`, `NowPlaying` and `Library`; views bind through them
  (`{Binding PlayerBar.TrackTitle}`). No child references another — the shell owns the two
  edges (position and current track into the overlay, a library selection into the player
  bar), so the graph stays acyclic and the position has exactly one source.
- **Services are injected, not constructed by UI code.** `App.xaml.cs` is the only place that
  names concrete types: it builds the scanner/tag-reader/store facade, an `IAudioPlayer` and an
  `ILyricProvider` and a folder picker, then hands them to the shell. No DI container — a
  handful of objects with one lifetime do not justify one. Swapping the audio engine really
  was a single line there.
- **Design tokens first.** Colors, brushes, corner radii and font sizes are declared
  once in `Themes/Colors.xaml`. Views reference tokens rather than literal values, so
  retheming means editing one file. The style dictionaries are split by concern rather than
  piled into one `Controls.xaml`.
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
- **Lyrics are data, not view state.** The overlay renders whatever `NowPlayingViewModel`
  puts in its `Lyrics` collection; each row owns an `IsActive` flag, so advancing the highlight
  touches only the two rows that change. Clicking a line seeks to it, and the view's code-behind
  scrolls the active row into view. A real `.lrc` parser can replace `SampleLyricProvider`
  without any view changes.
- **The sliders are custom-drawn.** The stock WPF `Slider` chrome is heavy and its
  `Track`/repeat-button plumbing is awkward to restyle, so `Controls/SlimSlider.cs`
  paints its own track, filled portion and hover thumb while inheriting all behaviour
  (keyboard stepping, click-to-seek, drag, data binding) from `Slider`.

## Status

Import works end to end: **Import folder** picks a directory, recursively scans it for
`.mp3`/`.wav`/`.m4a`/`.aac`/`.wma`, reads title/artist/duration/album/track number through
TagLibSharp, de-duplicates by path and writes the index to `%LOCALAPPDATA%\SongPlayer` so the
Local Music page is populated the next time you start the app. Clicking (or arrow-keying to)
a row loads it and starts playing it.

Playback is real: `MediaPlayerAudioPlayer` wraps WPF's `MediaPlayer`, reports its position
four times a second, and turns engine failures (missing file, unsupported format, seek error)
into a visible message in the player bar instead of an exception or a silent no-op. Seeking is
coalesced to ten requests a second so dragging the bar does not stutter the audio. FLAC is not
in the importable list because Windows only decodes it with the optional media feature pack.

There is no queue yet: a track ends where it ends, and the left transport button restarts the
current track rather than stepping back through history. `.lrc` parsing, cover art, search over
the library, playlists, a queue with next/shuffle/repeat are the next milestones. Controls
that are not yet implemented are visibly disabled rather than silently inert.
