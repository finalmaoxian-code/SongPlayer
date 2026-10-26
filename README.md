# SongPlayer

A focused desktop music player for local libraries, built with WPF on .NET.

The interface is deliberately quiet: a narrow sidebar for navigation, a single page
surface for content, and a persistent player bar that never competes with the music.
Clicking the artwork in the player bar opens an immersive *Now Playing* view with the
cover art and the lyric sheet, and closing it returns you to the page you came from.

## Requirements

- .NET 10 SDK (or the runtime matching the `TargetFramework` in `SongPlayer.csproj`)
- Windows 10 version 1809 or later
- For online playback only: Node.js and the sibling `SongPlayer.SourceLab` project, started
  with `node bridge.cjs` (it listens on `127.0.0.1:39271`). Local files need nothing extra.

The build pins `RuntimeIdentifier=win-x64` because LibVLC ships one native build per
architecture. Leave the solution platform on **Any CPU** — the identifier already produces an
x64 process, so no platform switching is required.

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
│  ├─ Shell/BilibiliViewModel.cs    BV id input, bridge resolve, retry on an expired link
│  ├─ Pages/LibraryPageViewModel.cs Import, persistence, track list, selection
│  └─ LyricLineViewModel.cs         A lyric row with its own highlight flag
├─ Models/                  Track, PlayerPage, LyricLine, TimeSpanFormatting — plain data, no WPF
├─ Services/
│  ├─ Artwork/              CoverImageLoader: downloads and decodes artwork off the UI thread
│  ├─ Audio/                IAudioPlayer + LibVlcAudioPlayer (LibVLC engine, files and streams)
│  ├─ Bilibili/             Bridge client, stream URL + metadata resolution, duration probe
│  ├─ Library/              Scanner, tag reader, JSON store, facade over all three
│  ├─ Storage/              IFolderPicker + the Win32 folder dialog behind it
│  └─ Lyrics/               ILyricProvider, TrackLyricProvider, CaptionStore, sample provider
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
- **One DataContext, four child view models.** The window is bound to `MainWindowViewModel`,
  which composes `PlayerBar`, `NowPlaying`, `Library` and `Bilibili`; views bind through them
  (`{Binding PlayerBar.TrackTitle}`). No child references another — the shell owns every edge
  (position and current track into the overlay, a library selection into the player bar, an
  online resolve into the player bar, a failed stream back into a retry), so the graph stays
  acyclic and the position has exactly one source.
- **Services are injected, not constructed by UI code.** `App.xaml.cs` is the only place that
  names concrete types: it builds the scanner/tag-reader/store facade, an `IAudioPlayer`, an
  `IBilibiliSource`, an `ILyricProvider` and a folder picker, then hands them to the shell. No
  DI container — a handful of objects with one lifetime do not justify one. Swapping the audio
  engine (WPF `MediaPlayer` → LibVLC) really was a single line there.
- **Online audio is a track, not a second player.** A Bilibili stream is a `Track` with
  `StreamUri` set instead of `FilePath`, so the seek bar, the time labels, the volume and the
  lyric clock are the same ones local files use. `StreamKey` is what survives (the signed CDN
  URL expires within hours and is never written to disk), and a stream that fails to load is
  re-resolved once from that key before the error is shown.
- **Borrowed metadata is labelled as borrowed.** A video contributes a title, an uploader, a
  thumbnail, a duration and sometimes a subtitle track, but an uploader is not an artist, a video
  frame is not an album cover and a subtitle is not a lyric. The player bar prints
  `Bilibili video · <uploader>`, the overlay swaps the album chips for that source chip, the line
  sheet is headed `SUBTITLES` rather than `LYRICS` with a note saying where the timing came from,
  and the empty state names Bilibili's login restriction instead of blaming the video. All of it
  is derived from `Track.IsStream`, so it cannot drift out of sync with the track it describes.
- **Artwork is decoded, never bound to a URL.** `CoverImageLoader` fetches the thumbnail with a
  browser user agent and a `Referrer`, decodes it off the UI thread and hands back a frozen
  `BitmapImage`; the two artwork frames bind to that. WPF would otherwise fetch an `http` URI on
  the UI thread, and a broken cover would be able to interrupt playback instead of falling back
  to the glyph.
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
`.mp3`/`.flac`/`.wav`/`.m4a`/`.aac`/`.wma`/`.ogg`/`.opus`, reads title/artist/duration/album/
track number through TagLibSharp, de-duplicates by path and writes the index to
`%LOCALAPPDATA%\SongPlayer` so the Local Music page is populated the next time you start the
app. Clicking (or arrow-keying to) a row loads it and starts playing it.

Playback is real: `LibVlcAudioPlayer` wraps LibVLC 3.0 (through `LibVLCSharp`, shipped as
native binaries by `VideoLAN.LibVLC.Windows`), reports the playhead four times a second, and
turns engine failures (missing file, unsupported format, rejected play, expired stream) into a
visible message in the player bar instead of an exception or a silent no-op. Seeking is
coalesced to ten requests a second so dragging the bar does not stutter the audio.

Online playback works: paste a Bilibili BV id into the Local Music toolbar and SongPlayer asks
the local Node bridge (`127.0.0.1:39271`, from the sibling `SongPlayer.SourceLab` project) for
the DASH audio URL, reads the real duration out of the stream's `sidx` box, and plays it through
the same engine and the same transport. The video's title, uploader and thumbnail come back with
it: the player bar and the Now Playing overlay show the real cover, and the metadata is labelled
as a video's rather than a song's. Any subtitle track the bridge could read is archived under
`%LOCALAPPDATA%\SongPlayer\captions` and replayed through the same karaoke list — Bilibili only
publishes most subtitle tracks to a signed-in session, so that list is usually empty and the
overlay says why. The bridge has to be running; the page says so when it is not, and local files
keep working regardless. The signed URL is never persisted.

There is no queue yet: a track ends where it ends, and the left transport button restarts the
current track rather than stepping back through history. `.lrc` parsing, embedded cover art for
local files, search over the library, playlists, saving online tracks, and a queue with
next/shuffle/repeat are the next milestones. Controls that are not yet implemented are visibly
disabled rather than silently inert.
