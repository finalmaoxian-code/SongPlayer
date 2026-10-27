# SongPlayer — Architecture & Implementation Review

A complete description of how this project is put together: what every file does, how the
layers relate, how much of each feature is actually implemented, and why the non-obvious
decisions were made.

- [1. What the project is](#1-what-the-project-is)
- [2. At a glance](#2-at-a-glance)
- [3. File layering](#3-file-layering)
- [4. Layer-by-layer breakdown](#4-layer-by-layer-breakdown)
- [5. Data flow and binding map](#5-data-flow-and-binding-map)
- [6. Implementation completeness](#6-implementation-completeness)
- [7. Implementation approach — decisions and rationale](#7-implementation-approach--decisions-and-rationale)
- [8. Known limitations](#8-known-limitations)
- [9. How to extend the project](#9-how-to-extend-the-project)
- [10. Quality gates currently enforced](#10-quality-gates-currently-enforced)

---

## 1. What the project is

SongPlayer is a desktop music player for a **local** music library, built with WPF on .NET
10. The design intent is "quiet": a narrow sidebar, one content surface, and a persistent
player bar that never competes with the music. Clicking the artwork in the player bar opens
an immersive **Now Playing** view with the cover art and a lyric sheet, and closing it
returns to the page you came from.

The project is currently a **working player around a shallow library**: importing a folder
really scans the disk, reads tags through TagLibSharp, saves the index to `%LOCALAPPDATA%` and
lists the tracks with working selection; audio comes out of LibVLC for both local files and
Bilibili streams, with a live playhead, seeking, volume and cover art; lyrics are fetched
automatically from LRCLIB, matched against the track's real identity, highlighted against the
playhead and correctable by hand. What is still stubbed is the things a *library* needs rather
than a *player* needs — a queue, search over the imported rows, playlists, and persistence of
anything except the track index and the lyric cache — and nothing in the UI pretends otherwise:
unimplemented controls are visibly disabled.

---

## 2. At a glance

| Item | Value |
| --- | --- |
| UI framework | WPF, `net10.0-windows`, `UseWPF=true` |
| Language settings | `Nullable=enable`, `ImplicitUsings=enable` |
| Only NuGet dependencies | `CommunityToolkit.Mvvm` 8.4.2, `TagLibSharp` 2.3.0, `LibVLCSharp` 3.10.2, `VideoLAN.LibVLC.Windows` 3.0.24, `NAudio` 3.3.0 |
| C# source | 71 files, 12,683 lines (about a third comment lines) |
| XAML | 17 files, 2,562 lines |
| Window | 1180×760 default, 900×600 minimum, borderless with a custom caption |
| Theme | Dark only, token-driven |
| Build status | `dotnet build` → 0 warnings, 0 errors |
| Language policy | Every UI string, code comment and doc is English. The one deliberate exception is `Services/Lyrics/LyricText.cs`, whose version and noise keyword tables must contain the Chinese words real video titles use for instrumental, live and cover — plus the CJK fixtures in the tests that prove those tables work and that the Bilibili client survives the server's own Chinese messages |

---

## 3. File layering

```
SongPlayer/
│
├── App.xaml                          ─┐
├── App.xaml.cs                        │  Composition root
├── AssemblyInfo.cs                   ─┘  (builds the services, hands the shell VM to the window)
│
├── Themes/                           ─┐
│   ├── Colors.xaml                    │  LAYER 1 — Design system
│   ├── Typography.xaml                │  (no logic, only tokens + styles)
│   ├── Surfaces.xaml                  │
│   ├── Buttons.xaml                   │
│   ├── Inputs.xaml                    │
│   ├── Navigation.xaml                │
│   └── NowPlaying.xaml               ─┘
│
├── MainWindow.xaml                   ─┐
├── MainWindow.xaml.cs                 │
├── Views/                             │  LAYER 2 — View
│   ├── CaptionChrome.cs               │  (layout, templates, chrome, code-behind)
│   ├── Shell/                         │
│   │   ├── CaptionBarView.xaml        │
│   │   ├── SidebarView.xaml           │
│   │   ├── PageHeaderView.xaml        │
│   │   └── PlayerBarView.xaml         │
│   ├── Pages/                         │
│   │   ├── HomePageView.xaml          │
│   │   └── LibraryPageView.xaml       │
│   └── Overlays/                      │
│       └── NowPlayingView.xaml        │
├── Controls/                          │
│   ├── SlimSlider.cs                  │
│   └── AudioVisualizer.cs            ─┼─  LAYER 2 — View (custom-drawn controls)
├── Converters/BoolToVisibilityConverter.cs
│                                      ─┘
├── ViewModels/                       ─┐
│   ├── MainWindowViewModel.cs         │  LAYER 3 — Presentation state
│   ├── Shell/                         │  (all UI state + commands;
│   │   ├── PlayerBarViewModel.cs      │   MainWindowViewModel mediates between
│   │   ├── BilibiliViewModel.cs       │   the child view models)
│   │   └── NowPlayingViewModel.cs     │
│   └── LyricLineViewModel.cs          │
│                                      ─┘
├── Models/                           ─┐
│   ├── Track.cs                       │  LAYER 4 — Domain data
│   ├── PlayerPage.cs                  │  (immutable records / enums)
│   ├── LyricLine.cs                   │
│   ├── LyricResult.cs                 │
│   ├── SongIdentity.cs                │
│   ├── OverlayView.cs                 │
│   └── TimeSpanFormatting.cs          │
│                                      ─┘
├── Services/                         ─┐
│   ├── Audio/                         │  LAYER 5 — Services
│   │   ├── IAudioPlayer.cs            │  (interfaces + implementations;
│   │   ├── LibVlcAudioPlayer.cs       │   no view logic)
│   │   ├── AudioCaptureFactory.cs     │
│   │   ├── AudioDiagnostics.cs        │
│   │   ├── SpectrumAnalyzer.cs        │
│   │   ├── SpectrumDisplay.cs         │
│   │   ├── SpectrumFrame.cs           │
│   │   └── WasapiAudioTap.cs          │
│   ├── Artwork/                       │
│   │   └── CoverImageLoader.cs        │
│   ├── Bilibili/                      │
│   │   ├── IBilibiliSource.cs         │
│   │   ├── BilibiliClient.cs          │
│   │   ├── BilibiliWeb.cs             │
│   │   ├── WbiSigner.cs               │
│   │   ├── BilibiliSearchResult.cs    │
│   │   ├── BilibiliSourceException.cs │
│   │   ├── BilibiliCaption.cs         │
│   │   ├── Bvid.cs                    │
│   │   ├── DashSegmentProbe.cs        │
│   │   └── ResolvedMedia.cs           │
│   ├── Library/                       │
│   │   ├── IMusicLibraryService.cs    │
│   │   ├── MusicLibraryService.cs     │
│   │   ├── IAudioFileScanner.cs       │
│   │   ├── AudioFileScanner.cs        │
│   │   ├── ITrackMetadataReader.cs    │
│   │   ├── TrackMetadataReader.cs     │
│   │   ├── LibraryEntry.cs            │
│   │   └── LibraryStore.cs            │
│   └── Lyrics/                        │
│       ├── ILyricProvider.cs          │
│       ├── TrackLyricProvider.cs      │
│       ├── LrclibClient.cs            │
│       ├── LyricMatcher.cs            │
│       ├── LyricText.cs               │
│       ├── LrcParser.cs               │
│       ├── LyricCacheStore.cs         │
│       ├── SongIdentityResolver.cs    │
│       ├── CaptionStore.cs            │
│       ├── SampleLyricProvider.cs     │
│       └── DesignTimeLyricProvider.cs ┘
│
└── Interop/                          ─┐
    └── WindowMaximizeHelper.cs        │  LAYER 6 — Platform interop
                                       ─┘  (Win32, no UI knowledge)
```

**Dependency direction is strictly downward**: `Interop` knows nothing about the view,
`Models` know nothing about WPF, `Services` reference `Models` only, and `ViewModels`
reference `Models` plus the service *interfaces* they use — never a concrete implementation and
never a view. The view layer is the only place that knows all of them. `App.xaml.cs` is the
single point that names concrete services (see §7.10).

### Line counts by layer

| Layer | Files | Lines |
| --- | ---: | ---: |
| Design system (`Themes/`) | 8 | 882 |
| View (`MainWindow.*`, `Views/`, `Controls/`, `Converters/`) | 13 | 1,503 |
| Presentation state (`ViewModels/`) | 6 | 2,414 |
| Domain data (`Models/`) | 7 | 604 |
| Services (`Services/`) | 43 | 7,893 |
| Platform interop (`Interop/`) | 1 | 153 |
| Composition root (`App.xaml.cs`, `AssemblyInfo.cs`) | 2 | 116 |
| **Total (C#)** | **71** | **12,683** |

---

## 4. Layer-by-layer breakdown

### 4.1 Layer 1 — Design system (`Themes/`)

**`Themes/Colors.xaml` (89 lines)** — the single source of truth for the visual language.
Contains three kinds of resource:

- **Colors** (14): `WindowBackgroundColor #0E1014`, `Surface1Color #151821`,
  `Surface2Color #1B1F29`, `Surface3Color #232833`, `Surface4Color #2C323E`,
  `BorderColor #242936`, `AccentColor #A78BFA`, `AccentStrongColor #8B5CF6`,
  `AccentSoftColor #2C2545`, `TextPrimaryColor #F3F4F8`, `TextSecondaryColor #9BA2B2`,
  `TextTertiaryColor #6B7285`, `CaptionHoverColor #222834`, `CaptionCloseHoverColor #E5484D`.
- **Brushes** (19): one frozen brush per color, plus `DividerBrush`, `TransparentBrush`,
  `AccentOnSolidBrush #14111F` (text that sits on the accent color) and `HeroGradientBrush`
  (`#3B2E63 → #282043 → #1B1F29`).
- **Metrics**: `CardCornerRadius 14`, `SmallCornerRadius 10`, `FontSizeDisplay 26`,
  `FontSizeTitle 15`, `FontSizeBody 13`, `FontSizeCaption 12`, `AppFontFamily`
  (`"Segoe UI Variable Text, Segoe UI"`).

The surface ramp (1→4) is the whole depth system: nested containers simply go one step
lighter, so no border or shadow is needed to communicate hierarchy.

**Seven style dictionaries (686 lines, 27 styles)** — split by control kind instead of one
`Controls.xaml`. `App.xaml` merges them in dependency order (`Colors.xaml` must come first
because everything references it). Each style is built only on the tokens above, so retheming
means editing `Colors.xaml` alone:

| File | Styles |
| --- | --- |
| `Typography.xaml` (48) | `DisplayTextStyle`, `TitleTextStyle`, `BodyTextStyle`, `CaptionTextStyle`, `SectionLabelStyle` (small uppercase section headers), `GlyphTextStyle` (Segoe MDL2 Assets icon font) |
| `Surfaces.xaml` (31) | `CardStyle`, `WellStyle` (muted inner surface for thumbnails/icon wells) |
| `Buttons.xaml` (287) | `IconButtonStyle` (34×34 round ghost icon), `PlayButtonStyle` (filled accent circle), `ArtworkButtonStyle` (clickable album art with a hover ring), `SecondaryButtonStyle` (neutral pill), `CaptionButtonStyle` (glyph-only, forced to the icon font), `CaptionCloseButtonStyle` (red hover), `InlineChipButtonStyle` (small text chip in the app font — it exists separately because the caption style's icon font turns words into empty boxes) |
| `Inputs.xaml` (81) | `SearchBoxStyle` — placeholder text, magnifier glyph, accent focus border |
| `Navigation.xaml` (77) | `NavItemStyle` — a `RadioButton` with a left accent bar that appears only on the active item |
| `NowPlaying.xaml` (89) | `ArtworkFrameStyle` (rounded frame + drop shadow), `LyricLineStyle`, `LyricListStyle`, `ChipStyle` |
| `Library.xaml` (131) | `ColumnHeaderStyle`, `TrackRowStyle` (hover/selected tints, `AutomationProperties.Name` so a row is not announced as a model dump), `SearchRowStyle` (same look, named after title + uploader instead of title + artist), `TrackListStyle`, `TrackIndexGlyphStyle`, `TrackTitleCellStyle`, `TrackAlbumCellStyle`, `TrackDurationCellStyle` |

### 4.2 Layer 2 — View

The window is split into one assembly file plus seven composed views. A view never sets its
own `Grid.Row` / `Grid.Column` / `Grid.RowSpan` — the consumer decides where it goes, so the
same control can be relocated without editing it.

**`MainWindow.xaml` (97 lines)** — only the assembly: `WindowChrome` settings, a three-row
grid (caption 40 / body `*` / player bar 88), and the placement of the seven views. It also
declares a `d:DataContext` design instance; the runtime `DataContext` is injected by the
composition root (§7.10).

**`Views/Shell/`**

| File | Lines | Contents |
| --- | ---: | --- |
| `CaptionBarView.xaml(.cs)` | 57 + 28 | Brand mark, the `MaximizeButton` and the three window buttons. Visibility is inverted on `IsNowPlayingOpen`, because the overlay draws its own caption. Code-behind delegates to `CaptionChrome`. |
| `SidebarView.xaml(.cs)` | 84 + 13 | `BROWSE` section with `Home`, `Local Music` and a disabled `Playlists` entry, plus a `Preview build` footer. |
| `PageHeaderView.xaml(.cs)` | 28 + 13 | `PageTitle`, `PageSubtitle` and the search box. |
| `PlayerBarView.xaml(.cs)` | 201 + 36 | Artwork button (opens Now Playing) that paints the real cover, title, an honest source/artist line, transport, seek bar, volume. Code-behind forwards `SlimSlider.DraggingChanged` to `PlayerBar.SetSeeking`. |

**`Views/Pages/`**

| File | Lines | Contents |
| --- | ---: | --- |
| `HomePageView.xaml(.cs)` | 185 + 12 | Gradient hero banner with two CTAs, a `UniformGrid` of three quick-access cards (the first shows the live `Library.LibrarySummary`), and a compact "library is empty" card that hides itself once tracks exist. |
| `LibraryPageView.xaml(.cs)` | 362 + 168 | Three rows in the card: library actions (`LibrarySummary`, status line, Clear library, Import folder), the online row (one box for a keyword *or* a video id, `Search`, `Play BV…`, plus its own status line), and a candidate card that appears only while a search has hits. Below them the `ListBox` track table (`#`/TITLE/ALBUM/TIME) and an empty state driven by `Library.HasNoTracks`. Code-behind mirrors `PlayerBar.CurrentTrack` into the table selection, turns a table selection into `Library.PlayTrackCommand`, and turns a candidate selection into `Bilibili.PlayCandidateCommand` — both guarded so mirroring never starts playback, and the candidate row is cleared straight after the click so the same upload can be picked again. |

**`Views/Overlays/NowPlayingView.xaml(.cs)`** (632 + 113) — the `Grid.RowSpan="3"` layer:
its own caption strip (back button, `NOW PLAYING`, `OverlayMaximizeButton`), an
artwork/facts/transport card on the left (the artwork `Border` paints `PlayerBar.CoverImage`
over a stretched background so the decoded bitmap has a size to fill), and on the right a
**two-card cell** with a segmented header: the spectrum visualiser by default, and the lyric
card when `NowPlaying.IsLyricsSelected` is true. The lyric card owns four mutually exclusive
bodies — the timed list, the plain-text `ScrollViewer`, the candidate chooser and the empty
state — keyed on `ShowsLyricList`, `ShowsPlainLyrics`, `ShowsCandidateList` and
`ShowsEmptyLyricState`. None of them is keyed on `!HasLyrics`: all four occupy the same cell, so
a panel keyed only on the absence of lines would float over the spectrum. The header also
carries the offset controls (`−0.5 s`, `+0.5 s`, `Reset`, with the current value in a tooltip)
and the card's footer carries the provenance chip. Its code-behind holds the two genuinely
view-only concerns: `ActiveLyricIndex` → `BringIntoView()` scrolling (deferred to
`DispatcherPriority.Loaded` so freshly realised containers have a valid position) and
click-to-seek on a line.

**`MainWindow.xaml.cs` (61 lines)** — now only window-level concerns: `OnMouseLeftButtonDown`
(keeps the caption from stealing focus), `WindowMaximizeHelper.Attach(this)`, and
`OnPreviewKeyDown` closing the overlay on `Esc` — kept on the window because preview input
reaches it before a focused lyric row can swallow the key.

**`Views/CaptionChrome.cs` (115 lines)** — shared caption behaviour for the two caption
strips. `Attach(Button maximizeButton)` subscribes to the owning window's `StateChanged` and
paints the `E922`/`E923` glyph, so the shell caption and the overlay caption cannot disagree;
`Minimize` / `ToggleMaximize` / `Close` resolve the owning `Window` with
`Window.GetWindow(visual)`, which is what lets a `UserControl` drive window state without a
reference to `MainWindow`.

**`Controls/SlimSlider.cs` (309 lines)** — a `Slider` subclass that paints itself. Five
dependency properties (`TrackBrush`, `FillBrush`, `ThumbBrush`, `TrackThickness` = 4.0,
`ThumbDiameter` = 11.0). It inherits *all* behaviour from `Slider` (binding, keyboard
stepping, value coercion, `ValueChanged`) and only replaces rendering (`OnRender`) and mouse
handling (click-to-seek + drag). `Value` gets its metadata re-declared in the static
constructor with `AffectsRender | BindsTwoWayByDefault`: the inherited `Slider.Value` metadata
does not invalidate the visual, which is invisible on the stock control (its `Track` and `Thumb`
parts repaint themselves) but left the hand-drawn fill frozen at its last paint — the playhead
looked stuck until the mouse moved over the bar (§7.6). The thumb dot appears only on hover or while dragging, so
the resting player bar stays flat.

**`Controls/AudioVisualizer.cs` (549 lines)** — the spectrum bars, drawn by hand on a
`FrameworkElement`. `BandCount` = 32 bars mirrored around a centre seam, so roughly 16 audible
bands are visible per side; the seam carries a soft accent glow whose opacity follows the
overall pulse. Five details matter:

- **It owns the capture, not the view model.** A `WasapiAudioTap` needs a device stream and a
  background thread; only the view knows whether the visualiser is on screen. Handing the tap to
  `NowPlayingViewModel` would tie a WASAPI stream's lifetime to a view model that outlives the
  overlay (the overlay is a permanently parented `Grid` sibling whose `Visibility` flips), so the
  stream would stay open while the card is hidden.
- **Four signals feed one coordinator.** `Loaded`, `Unloaded`, `IsVisibleChanged` and
  `IsPlaying` all call the same `Reconcile()`, which derives one rule: capture while loaded *and*
  visible *and* playing, animate while loaded and visible, release the device otherwise. The
  overlay and the lyrics card are collapsed rather than removed, so `IsVisibleChanged` — not a
  visual-parent change — is what reports the card change; `Loaded`/`Unloaded` still matter because
  unload has to release the device and a later load has to be willing to create a new one.
- **One clock at a time, measuring real time.** `CompositionTarget.Rendering` while playing (at
  most one subscription, guarded by a flag), a 33 ms `DispatcherTimer` for the paused idle wave,
  neither when hidden or unloaded. Both feed `AdvanceClock()`, which derives `dt` from a `Stopwatch`
  rather than assuming a frame rate — invalidating without advancing is what once froze the idle
  wave.
- **Freshness, not silence, decides what is drawn.** The tap publishes an immutable
  `SpectrumFrame` stamped with a monotonic timestamp; the control treats a frame as fresh only
  while playing, capturing, and younger than `SpectrumDisplay.FreshWindowSeconds` (450 ms). A
  stale frame falls away at `FallPerSecond` and the idle wave takes over, so a dead capture can
  never leave the bars frozen at their last values.
- **It has no failure path into the UI.** Unsupported Windows builds (< 10.0.19041) and device
  refusals surface as one `Faulted` event, which the control only renders as a dim caption under
  the idle wave; playback is never touched.

**`Converters/BoolToVisibilityConverter.cs` (38 lines)** — one converter with an `Invert`
flag, registered twice in `App.xaml` as `BoolToVisibility` and `InverseBoolToVisibility`.
Avoids a `Style`/`DataTrigger` boilerplate for every "show this when X" case.

### 4.3 Layer 3 — Presentation state (`ViewModels/`)

Six view models. The shell composes the player children, the library page and the Bilibili
input, and is the only type that knows more than one of them; the children never reference each
other or their parent (§7.11). All use
`CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`), so no
`INotifyPropertyChanged` boilerplate is written by hand.

**`MainWindowViewModel.cs` (410 lines)** — navigation state plus composition; the mediator.

- **Own state**: `CurrentPage`, `SearchText`, `IsNowPlayingOpen`.
- **Derived state**: `IsHomeSelected` / `IsLibrarySelected` (settable, see §7.3), `PageTitle`,
  `PageSubtitle`.
- **Commands**: `ShowLibrary`, `OpenNowPlaying` (gated by `CanOpenNowPlaying`),
  `CloseNowPlaying`, `SeekToLyric(LyricLineViewModel?)`, `ImportFolder` (the Home page CTA; it
  awaits the library command and then navigates, so the scan logic is never duplicated).
- **Composition**: exposes `PlayerBar`, `NowPlaying`, `Library` and `Bilibili` as properties,
  so the views bind *through* them (`{Binding PlayerBar.TrackTitle}`) while the window keeps one
  `DataContext`.
- **Mediation**: two edges, both one-liners. `OnPlayerBarPropertyChanged` forwards
  `PositionSeconds` → `NowPlaying.UpdateActiveLyric(...)` and `CurrentTrack` →
  `NowPlaying.LoadFor(...)` plus a re-evaluation of `OpenNowPlayingCommand`;
  `OnLibraryTrackActivated` turns a library selection into `PlayerBar.LoadTrack(track)` and a
  navigation to the library page. A third edge is recovery: `OnPlayerBarPlaybackFailed`
  re-resolves an expired Bilibili link once and replays it. Neither child knows the other
  exists.

**`ViewModels/Shell/PlayerBarViewModel.cs` (569 lines)** — transport plus track metadata, and
the single place the playback position is published from.

- **Input state**: `CurrentTrack` (`Track.Sample` by default), `IsPlaying`, `PositionSeconds`,
  `Volume` (70), `StatusMessage`, `CoverImage`.
- **Derived state**: `HasTrack`, `TrackTitle`, `TrackArtist`, `TrackAlbum`,
  `TrackAlbumPosition`, `ArtworkGlyph`, `DurationSeconds`, `DurationText`, `PositionText`,
  `PlayPauseGlyph`, `PlayPauseTooltip`, `HasStatusMessage`, `IsMuted`, `VolumeGlyph`,
  `MuteTooltip`, `HasCover` / `HasNoCover`, `CoverUri`, `IsOnline`, `SourceLabel`,
  `HasSourceLabel`, `HasAlbumTags`.
- **Commands**: `TogglePlayback` (gated by `HasTrack`), `Restart` (same gate), `ToggleMute`.
- **Engine contract**: subscribes to `PositionChanged` / `PlaybackEnded` / `PlaybackFailed` in
  the constructor. `_isApplyingEnginePosition` marks the assignments that came *from* the
  engine so `OnPositionSecondsChanged` does not echo them back as a seek;
  `SetSeeking(bool)` (called by the view on `SlimSlider.DraggingChanged`) makes the pointer
  win during a drag and issues one seek on release. A failed play becomes `StatusMessage`
  instead of an exception.
- **Entry points**: `LoadTrack(track)` (stop, rewind, swap the record) and `PlayTrack(track)`
  (the former plus `Play`), used by the shell when a library row is chosen. The position is
  reset *before* the record swap so the seek slider never spans the old and new duration.
- `TrackAlbum` / `TrackAlbumPosition` read the real tags now, so the overlay says
  "Track 04" only when the file actually carries a track number.
- **Cover art is downloaded, not bound.** `OnCurrentTrackChanged` starts
  `CoverImageLoader.LoadAsync(Track.CoverUri)` through a `CancellationTokenSource` that is
  cancelled on the next track, and the result is dropped when `ReferenceEquals(track,
  CurrentTrack)` no longer holds — a fast library scroll therefore never paints an old cover
  next to a new title. The loader returns `null` on any failure: a missing thumbnail must not
  reach the transport.
- **Online tracks are labelled honestly.** `IsOnline`, `SourceLabel` ("Bilibili video"),
  `HasSourceLabel` and `HasAlbumTags` exist because a video title is not a song title, the
  uploader is not the artist and a video has no album; the bar prints `Bilibili video ·
  <uploader>` and the overlay swaps the album chips for the source chip instead of printing
  "No album" for a field that could never be there.
- `PlaybackFailed` (an event carrying the failed track) exists on top of `StatusMessage`: the
  message is what the bar displays, the event is what the shell needs in order to retry.

**`BilibiliViewModel.cs` (355 lines)** — the online source input.

- **State**: `Bvid`, `StatusMessage`, `IsBusy`, `HasError`; `TrackResolved` event.
- **Commands**: `Play` (resolve → archive the subtitles → pick the duration → raise).
  `ResolvedMedia.Duration` from the metadata endpoint wins; `DashSegmentProbe` is the fallback for
  a video whose metadata did not carry a length.
- It turns `ResolvedMedia` into a real `Track`: the video title becomes `Title`, the uploader
  becomes `Artist`, `CoverUri` rides along for the artwork loader, and `Album` stays `null` on
  purpose — a video has no album and inventing one would be a lie the UI then repeats.
- **Refresh**: `TryRefreshAsync(Track)` re-resolves a stream whose signed URL expired, reading
  the video id back out of `Track.StreamKey`.
- It builds a normal `Track` with `StreamKey` / `StreamUri` set, so online playback is not a
  second player — it is a track the existing transport already understands (§7.12).

**`ViewModels/Shell/NowPlayingViewModel.cs` (638 lines)** — the right-hand card of the overlay, and
the only place lyric lookup state is turned into something the view can render.

- `Lyrics` (an `ObservableCollection<LyricLineViewModel>`), `Candidates`
  (`ObservableCollection<LyricCandidateViewModel>`), `HasLyrics`, `ActiveLyricIndex`,
  `PlainText` / `HasPlainText`, `HasPresentableContent`.
- `ActiveView` (an `OverlayView`) selects the card: `IsVisualizerSelected` / `IsLyricsSelected`
  feed the segmented header, and four visibility gates pick the body — `ShowsLyricList`,
  `ShowsPlainLyrics`, `ShowsCandidateList`, `ShowsEmptyLyricState`. The header items are
  `RadioButton`s in one `GroupName`, styled by `OverlayTabStyle`; their `IsChecked` is
  `Mode=OneWay` off the view model, so the control never holds a second copy of the selection
  (§7.14).
- **The lyrics chip is always clickable.** `CanShowLyrics` is hard-coded `true`: a track that has
  no lyrics still has something worth showing (the reason), and a disabled chip would hide the
  retry button behind it.
- **Auto-switch without stealing the view.** `LoadFor(Track)` cancels the previous request, bumps
  `_loadVersion`, starts the lookup and returns immediately — playback never waits. A completed
  lookup switches to the lyric card only while `_autoSwitchToLyrics` is still set; both
  `ShowVisualizer` and `ShowLyrics` clear it, so a user who chose the visualiser while the
  request ran does not get overruled by a late answer. Every write after an `await` is guarded by
  `version == _loadVersion`, which is what stops A's slow response from painting over B.
- `UpdateActiveLyric(TimeSpan)` picks the last line whose `Time` is ≤ `position - LyricOffset`
  and flips only the rows that change; `PlaybackPositionFor(line)` is its exact inverse, so the
  highlight and the click-to-seek cannot disagree.
- **Offset is the user's, not the file's.** `LyricOffsetMilliseconds` (±30 s, 0.5 s steps,
  resettable) is persisted per track through the provider, and the convention is documented on
  `LyricOffset`: `lyricTime = playbackTime - userOffset`.
- It holds **no** transport state: the position is pushed in by the shell mediator, so the
  overlay and the player bar always show the same instant.
- **The wording is derived from the result, not fixed.** `SectionLabel` reads `LYRICS · LRCLIB`,
  `LYRICS · VIDEO CAPTIONS` or `LYRICS · SAMPLE` from `LyricLookupResult.Source` — an online
  track with real lyrics no longer says `SUBTITLES`. `EmptyTitle` / `EmptyHint` switch on
  `LyricKind`, so "instrumental", "no lyrics found", "pick the right lyrics", "could not reach
  the lyric database" and "looking for lyrics…" are five different panels rather than one blank
  sheet.

**`ViewModels/Pages/LibraryPageViewModel.cs` (352 lines)** — the user-facing half of the
import pipeline, and the only view model that talks to `IMusicLibraryService`.

- **State**: `Tracks` (`ObservableCollection<Track>`), `IsBusy`, `StatusMessage`.
- **Derived**: `HasTracks`, `HasNoTracks`, `LibrarySummary` (`"12 tracks · 2 albums"`, counting
  only non-blank album tags), `HasSummary`.
- **Commands**: `ImportFolder` (pick a folder → `ScanAsync` → merge de-duplicated by
  `FilePath` → `SaveAsync` → status line), `ClearLibrary` (forgets the index, never deletes
  audio files, gated by `HasTracks`), `PlayTrack(Track?)` (raises `TrackActivated`).
- **Restore**: `RestoreAsync()` is an explicit method rather than constructor work, because a
  constructor that awaits disk I/O cannot be built by the XAML designer; `App` calls it after
  the window is shown, and failures degrade to a status message instead of an exception.
- It never touches `PlayerBar`: activation is an event the shell subscribes to, which keeps
  row selection and playback decoupled.

**`LyricLineViewModel.cs` (89 lines)** — two small row types. `LyricLineViewModel` is one lyric
line: immutable `Time`/`Text` plus a mutable `IsActive`. Putting the highlight flag on the row
means a playback tick notifies only the two rows that change, instead of invalidating the whole
list. `LyricCandidateViewModel` is one row of the chooser: a `LyricCandidate`, a `Detail` string
that joins album, length and record type into one line, and a `SelectCommand` that hands the record
id back to the view model — the view never learns what a record id means. `Detail` is one property
rather than three `Run` bindings for a reason recorded in §7.17.

### 4.4 Layer 4 — Domain data (`Models/`)

| File | Contents |
| --- | --- |
| `Track.cs` (104) | `sealed record Track(string Title, string Artist, TimeSpan Duration, string? FilePath = null, string? Album = null, int? TrackNumber = null, string? StreamKey = null, string? StreamUri = null, string? CoverUri = null, string? SearchClue = null)` with `IsPlayable` (a local path *or* a stream), `IsStream`, `HasCover`, `DurationText`, `AlbumText` (an em dash when the file has no album tag), plus `Track.Empty` and `Track.Sample` so the player bar is reviewable before anything is imported. `StreamKey` is durable identity for de-duplication; `StreamUri` is a temporary signed URL and is never persisted; `CoverUri` is an `http(s)` thumbnail address and is only ever a *source* for the loader, never an `ImageSource` in a binding; `SearchClue` is the keyword the listener typed before picking a video — a lookup hint, never a title or an artist |
| `PlayerPage.cs` (15) | `enum PlayerPage { Home, Library }` — the overlay is intentionally *not* a member |
| `LyricLine.cs` (12) | `sealed record LyricLine(TimeSpan Time, string Text)` |
| `LyricResult.cs` (225) | `LyricKind` (`Synced`, `Plain`, `Instrumental`, `NotFound`, `NeedsSelection`, `Failed`), `LyricSourceKind`, `LyricCandidate` and `LyricLookupResult` with its six factories. A lookup can say *why* it found nothing, which a bare list never could |
| `SongIdentity.cs` (202) | `SongVersion`, `IdentitySource`, `SongIdentity` (raw title, cleaned title, artist, album, duration, version, source, search clue, and the alternate names in `TitlesToTry`) and `SongVersionRules` — the one place "is this the same version of the song?" is answered |
| `OverlayView.cs` (18) | `enum OverlayView { Visualizer, Lyrics }` — which card the overlay's right-hand cell shows. `Visualizer` is the default because it is meaningful for every track, while a lyric sheet has to be fetched first |
| `TimeSpanFormatting.cs` (28) | `ToClock(TimeSpan)` → `m:ss` / `h:mm:ss`; one home for the clock format instead of a private helper per view model |

These are pure data: no WPF types, no notification, no behaviour — with one deliberate nuance,
`SongVersionRules`, which is a two-line policy rather than a service so that the matcher and the
resolver cannot drift apart. The sample lyrics that used to live here are now
`Services/Lyrics/SampleLyricProvider.cs` — they are a data *source*, not a domain type.

### 4.5 Layer 5 — Services (`Services/`)

**`Library/` (369 lines)** — the import pipeline, split by responsibility so each piece can be
replaced or faked on its own. `IMusicLibraryService` is the only library type a view model may
depend on.

| File | Lines | Responsibility |
| --- | ---: | --- |
| `IMusicLibraryService.cs` | 31 | Contract: `ScanAsync` / `LoadAsync` / `SaveAsync` |
| `MusicLibraryService.cs` | 79 | Orchestration only: walks the scanner's output, asks the reader for tags, delegates persistence |
| `IAudioFileScanner.cs` / `AudioFileScanner.cs` | 24 + 50 | Extension whitelist (mp3, flac, wav, m4a, aac, wma, ogg, opus — the set LibVLC decodes) and `Directory.EnumerateFiles` |
| `ITrackMetadataReader.cs` / `TrackMetadataReader.cs` | 19 + 81 | TagLibSharp title/artist/duration/album/track number with an explicit exception whitelist, so one corrupt file cannot abort a scan |
| `LibraryEntry.cs` | 118 | One persisted row: a local path, or an online stream key plus the metadata cached beside it. `FromTrack`/`ToTrack` convert to and from `Track`; `Key` is the de-duplication identity |
| `LibraryStore.cs` | 143 | `library.json` read/write with a temp-file-then-move atomic write. Reads both the current object layout and the older `string[]` one, so an existing library keeps working |

**`Storage/` (54 lines)** — `IFolderPicker.PickFolder(string title)` and `FolderPicker`,
which implements it with `Microsoft.Win32.OpenFolderDialog`. The interface exists so
`LibraryPageViewModel` never references `Microsoft.Win32`: a hard-coded dialog would make the
scan flow untestable and would drag a UI type into a view model.

**`Lyrics/` (3,232 lines)** — the automatic lyric pipeline: one small interface, one orchestrator,
and four focused helpers behind it. Nothing here knows about WPF, and every network call is
`async` all the way down.

| File | Lines | Responsibility |
| --- | ---: | --- |
| `ILyricProvider.cs` | 72 | `GetLyricsAsync(Track, LyricLookupOptions, CancellationToken)` → `LyricLookupResult`, plus the read/write pair for the per-track offset, plus `ICaptionArchive` so the subtitle path is testable without touching `%LOCALAPPDATA%` |
| `TrackLyricProvider.cs` | 763 | The orchestrator the shell injects: cache → manual choice → captions → LRCLIB exact → LRCLIB search → match → remember |
| `LrclibClient.cs` | 446 | HTTP and DTOs only: `GET /api/get`, `GET /api/search`, `GET /api/get/{id}`; one long-lived `HttpClient`; 404 is a miss, 503 is an outage, 429 keeps its `Retry-After`; a duration outside the service's accepted 1–3600 s is left out of the query instead of being sent (it answers 400 and would turn a long video into a hard failure) |
| `LyricMatcher.cs` | 399 | Scores candidates and decides auto-accept vs. ask-the-user; all thresholds live here (§7.15) |
| `LyricText.cs` | 515 | Title cleaning, version detection, artist hints, similarity — the only file allowed to carry non-English keyword tables |
| `LrcParser.cs` | 278 | `[mm:ss.xxx]` parsing: multiple stamps per line, BOM/CRLF, `[offset:]`, out-of-order lines, same-time pairs, malformed input ignored line by line |
| `LyricCacheStore.cs` | 303 | `%LOCALAPPDATA%\SongPlayer\lyrics\<key>.json`: lyric body, match metadata, offset and manual record id; atomic replace; corrupt or stale schema deleted, never fatal |
| `SongIdentityResolver.cs` | 211 | `Track` → `SongIdentity`: real tags for files, cleaned video title for streams, never the uploader as the artist |
| `CaptionStore.cs` | 158 | `%LOCALAPPDATA%\SongPlayer\captions\<key>.json` — one file per online track, kept as its own source kind |
| `SampleLyricProvider.cs` | 41 | The 16 placeholder lines, now reachable only through `ReferenceEquals(track, Track.Sample)` |
| `DesignTimeLyricProvider.cs` | 46 | Preview-only provider so the XAML designer and the parameterless view model ctor have something to render |

Five decisions here are worth the ink.

**The interface is one method and one result record.** The old `IReadOnlyList<LyricLine>` could not
say "this is instrumental", "the network failed" or "pick one of these five", so every caller
would have re-derived the difference from an empty array. `LyricLookupResult` carries a
`LyricKind` (`Synced`, `Plain`, `Instrumental`, `NotFound`, `NeedsSelection`, `Failed`), the lines
*or* the plain text, the source kind, the matched record metadata and the candidate list. Loading
is deliberately absent: it is view-model state, not a lookup outcome.

**Lookup order, cheapest first.** Cache → stored manual choice → archived video captions →
exact LRCLIB query (only when the identity is trusted and the title is known) → LRCLIB search over
at most 10 candidates → matcher. Each step that can answer stops the pipeline, so a cached song
costs no request at all and a local file with complete tags usually costs one.

**Matching is a policy, not a guarantee.** `LyricMatcher` never returns the first hit. A candidate
that scores below `TitleAcceptThreshold` (0.62) or conflicts on version is rejected outright, and
the winner is only auto-accepted when it clears `AutoAcceptScore` (78) *and* beats the runner-up
by `AutoAcceptMargin` (8). See §7.15 for the full table.

**Transport trouble is never cached; "no such song" is, briefly.** A `LrclibException` becomes
`Failed` and writes nothing, because caching an outage would turn a five-minute outage into "this
song has no lyrics" for days. A genuine miss is cached with `Negative = true` for
`NegativeLifetime` (3 days) so reopening the overlay does not re-ask. Positive entries live
`PositiveLifetime` (30 days).

**Cache keys are identities, never titles.** `LyricCacheStore.KeyFor` hashes `stream:<StreamKey>`,
`file:<normalized path>` or `track:<title>|<artist>`, so the same song title from two artists, two
versions of one song and a local file with an identical name cannot collide. Writes go to
`<key>.json.tmp` and are then moved over the real file, so a crash mid-write leaves the previous
entry intact; a corrupt file is deleted and treated as a miss.

**`Audio/` (2,325 lines)** — the playback half (`IAudioPlayer` + `LibVlcAudioPlayer`) plus the
capture half (`WasapiAudioTap`, `SpectrumAnalyzer`, `SpectrumDisplay`, `SpectrumFrame`,
`AudioCaptureFactory`, `AudioDiagnostics`).

`IAudioPlayer` (`IsPlaying`, `Volume`, `Play`, `Pause`, `Stop`, `Seek`, plus
`PositionChanged` / `PlaybackEnded` / `PlaybackFailed`) and
`LibVlcAudioPlayer`, the real engine: `LibVLCSharp`'s `MediaPlayer` wrapped so no view model
ever sees an engine type. Four details carry the design:

- **The engine owns the playhead.** A 250 ms `DispatcherTimer` samples `MediaPlayer.Time` and
  publishes it; `PlayerBarViewModel` marks those assignments so they are never echoed back as
  seeks. LibVLC's own `TimeChanged` event is deliberately *not* used: it fires on a native
  thread, and routing it to the UI would force every subscriber to marshal. Polling keeps the
  contract single-threaded and identical to what the previous engine offered.
- **Seeks are coalesced to 10/s.** A `SlimSlider` drag produces a seek per pixel and each one
  makes LibVLC flush its buffer; the engine parks the requested position in `_pendingPosition`
  and applies it on a 100 ms timer. `IsSeekable == false` (a stream LibVLC cannot index) turns
  a seek into a no-op instead of an exception.
- **Failures are values, not exceptions.** A missing `FilePath`, a file that vanished from
  disk, a rejected `Play` and LibVLC's `EncounteredError` all raise `PlaybackFailed` with a
  sentence; `PlayerBarViewModel` turns it into `StatusMessage`, which the player bar shows in
  place of the artist line. The shell has no toast layer, and the bar is where the user just
  clicked.
- **One engine for files and streams.** `Track.FilePath` and `Track.StreamUri` both become a
  media source; the only difference is that a stream additionally gets
  `:http-referrer=https://www.bilibili.com/`, a browser user agent and
  `:network-caching=1500`. The native callbacks (`EndReached`, `EncounteredError`) arrive on
  LibVLC's thread and are marshalled back through the captured `Dispatcher`.

Both LibVLC objects have UI-thread affinity and are created in `App.OnStartup`, which is also
why `App.OnExit` disposes them — an open media keeps the audio output device alive (§7.10).

`WasapiAudioTap` (929 lines) captures **what the machine is playing**, not what SongPlayer hands
the engine, which is why the bars follow the audio even though LibVLC never exposes its decoded
PCM. It is built on NAudio 3.3's `WasapiRecorderBuilder.WithProcessLoopback(pid,
ProcessLoopbackMode.IncludeTargetProcessTree)`, so it records this process only and a video on
another window does not leak into the spectrum. Five constraints shaped it:

- **`BuildAsync`, not `Build`.** Process loopback capture is activated asynchronously; `Build()`
  throws `InvalidOperationException`. The tap therefore awaits `_factory.CreateAsync(...)` on a
  background pump and never blocks the UI thread on device negotiation.
- **Desired state is separate from actual state.** `Start()`/`Stop()` only publish
  `_desired`/`_disposed` and kick a pump; `State` reports `Stopped`/`Starting`/`Running`/
  `Stopping`/`Disposed`. Because `Stop()` writes the target before any recorder exists, a stop
  that arrives during `BuildAsync` is honoured instead of dropped — the old
  `if (_recorder is null || !IsRunning) return;` guard was the bug that made the visualiser
  unrecoverable after a hide/show.
- **One pump serialises everything.** A single draining loop (re-pulsed while it runs) is the only
  place that starts, retires and disposes recorders, so `Start`/`Stop`/`Dispose`/`RecordingStopped`
  never interleave. `Stop`/`Dispose` never block inside the state lock, and `Faulted` is raised
  outside it.
- **A session per start, never a reused recorder.** `StopRecording()` only *requests* a stop in
  NAudio 3.3 — the recorder stays non-stoppable until its capture thread publishes
  `RecordingStopped`, and joining it from inside that callback deadlocks. So the tap builds a new
  recorder for each activation and disposes the old one from the pump. Every session carries an
  id and a retired flag: a stale `DataAvailable`/`RecordingStopped` cannot write into the new
  session's analyser or overwrite its state.
- **The device format wins.** The recorder reports its own `WaveFormat` (typically 32-bit float,
  44.1 or 48 kHz, stereo), and the tap builds a fresh `SpectrumAnalyzer` from *that* rather than
  from an assumed rate. Analysis, decay and `BuildFrame()` all run on the capture thread; the UI
  only ever reads the published `SpectrumFrame`.
- **Failures retry, then stop retrying.** A refused or broken capture arms one delayed retry
  (`_retryDelay`, default 1 s) up to `_maxAttempts` (default 4); once the budget is spent the tap
  waits for a caller that still wants capture *and* a cooldown (`ExhaustedRetryDelay`, 10 s)
  before re-arming. No per-frame retries, no unbounded auto-restart, and a dead tap never reaches
  the player.

`SpectrumAnalyzer` (365 lines) turns buffers into 32 band levels. A 2048-point Hann-windowed FFT
per buffer, log-spaced bands from 40 Hz to 9 kHz, each band taking the **loudest bin** rather than
the mean (a mean smears a pure tone into every band at once). Three numerical details were tuned
against a real capture rather than guessed:

- **Per-band noise floor**, adapted asymmetrically: it drops to any quieter reading immediately
  but climbs at most `FloorRiseDbPerSecond`, because a single shared floor let the quietest
  treble band pin the whole display to the bottom.
- **Decay before analyse.** Peaks fall by elapsed time *first*, then the new measurement is
  merged, otherwise each frame subtracted the peak it had just found and the bars never rose.
- **Neighbour bleed.** Each band also takes 35 % of its louder neighbour. Without it a pure tone
  lights three bars out of thirty-two and the render looks broken; real music already bleeds, so
  the effect is invisible on it.

`BuildFrame()` is the only way out of the analyser: it copies the 32 levels into an immutable
`SpectrumFrame` (103 lines) stamped with `Stopwatch.GetTimestamp()` and a monotonic `Sequence`.
`BandLevels` still exists but is documented as the live array — reading it off the capture thread
is a race, which is exactly what the old visualiser did.

`SpectrumDisplay` (117 lines) is the UI-side half, touched only from the dispatcher: it holds the
levels actually drawn, decays them by `FallPerSecond` when `Advance(frame, fresh, dt)` is told the
frame is stale, and advances `IdlePhase` while the peak sits below `IdleThreshold`. It never
reaches back into the analyser, so the capture thread stays the single writer. `MaxDtSeconds`
(0.1) clamps the delta so restoring a minimized window does not snap the wave forward.

`AudioCaptureFactory` (108 lines) is the seam that makes the lifecycle testable: `IAudioCaptureRecorder`
(`WaveFormat`, `DataAvailable`, `RecordingStopped`, `StartRecording`, `StopRecording`, `Dispose`)
and `IAudioCaptureFactory.CreateAsync(processId, bufferMilliseconds)`. The production
implementation wraps `WasapiRecorderBuilder`; the tests inject a scripted recorder that can hold
`CreateAsync` open, refuse a build, hold `Dispose`, or replay a retired callback.

`AudioDiagnostics` (191 lines) is the opt-in trace used to prove which of the three failure modes
is happening. It is off unless `SONGPLAYER_DIAGNOSTICS=1` or a file named `SongPlayer.diagnostics`
sits beside the executable, and then appends to `audio-diagnostics.log`: loaded/unloaded/visibility/
playing changes, the desired and actual capture state with the session generation, build and
start/stop transitions, exceptions, and one `summary` line per second with capture frames/s, render
frames/s and the age of the last frame. Nothing is written per buffer or per rendered frame.

**`Artwork/` (99 lines)** — `CoverImageLoader.LoadAsync(string? uri, CancellationToken)` returns
a frozen `BitmapImage` or `null`. Three details: the request carries a browser user agent and a
`Referrer`, because a bare `HttpClient` is the one caller Bilibili's CDN sometimes refuses; the
bitmap is created with `CacheOption.OnLoad` (so the stream is closed before the file is used) and
`Freeze()`d (so it can cross from the download thread to the UI); and every failure — bad scheme,
timeout, non-success status, undecodable bytes — collapses to `null`, because artwork is
decoration and must never be able to stop playback. The class lives in `Services/Artwork` rather
than `Services/Media` because a `SongPlayer.Services.Media` namespace shadows LibVLCSharp's
`Media` type inside the audio engine (a `CS0118` that only shows up at the call site).

**`Bilibili/` (1,578 lines)** — the online source, kept behind one interface so nothing else in
the app knows Bilibili exists. The API is called from this process; there is no helper server to
start, and no JavaScript runtime to ship.

| File | Lines | Responsibility |
| --- | ---: | --- |
| `IBilibiliSource.cs` | 44 | `SearchAsync(keyword, limit)` / `ResolveAsync(bvid)` |
| `BilibiliClient.cs` | 560 | Signs each request, calls `/x/web-interface/wbi/view`, `/x/player/wbi/playurl`, `/x/web-interface/wbi/search/type` and `/x/player/wbi/v2`, maps every failure mode to a sentence, never persists the URL |
| `BilibiliWeb.cs` | 256 | One `HttpClient` with the browser user agent and referrer Bilibili requires, plus the shared status-to-sentence mapping |
| `WbiSigner.cs` | 216 | The `w_rid` signature: key fetch and cache, the shuffle table, parameter ordering, MD5 |
| `BilibiliSearchResult.cs` | 55 | `(Bvid, Title, Uploader, Duration, CoverUri, Description)` — a candidate, deliberately not a `Track` |
| `Bvid.cs` | 75 | The id rule in one place: 12 characters, `BV` prefix, alphanumerics; plus `FromStreamKey("bilibili:BV…")` |
| `DashSegmentProbe.cs` | 293 | Reads the real duration out of the stream's `sidx` box (with an `mvhd` fallback) |
| `BilibiliSourceException.cs` | 30 | Carries a message written for the user |
| `BilibiliCaption.cs` | 13 | `(Seconds, Text)` — a timed subtitle line |
| `ResolvedMedia.cs` | 39 | `(Bvid, AudioUri, Title, Uploader, CoverUri, Description, Duration, Captions)` |

Every call is signed. Bilibili's web endpoints reject an unsigned request, so `WbiSigner` fetches
the key pair from `/x/web-interface/nav` (which answers `code: -101` for an anonymous caller and
hands the keys out anyway), shuffles `img_key + sub_key` through a fixed 64-entry table, keeps the
first 32 characters of the result, and appends `wts` plus an MD5 of the sorted query with that
mixin. Three consequences shaped the code: the key pair is cached for an hour and fetched once
even under concurrency, because eight parallel calls must not each ask for keys; the escaping rule
is "re-escape exactly once", because a keyword escaped twice produces a different `w_rid` than the
query string that is actually sent; and a failed fetch is forgotten by the *waiting* caller rather
than by the fetch itself, because a faulted task is assigned to the cache before any continuation
of it runs. The algorithm is public and documented; the client is written from that documentation
rather than copied from a library, which is also why fixed vectors live in `WbiSignerTests`.

The duration prefers the metadata endpoint and falls back to a 16 KB ranged request: a
Bilibili segment is a fragmented MP4 whose `mvhd` declares `duration = 0`, so the probe has to
read `sidx`, and the video-info endpoint is simply more reliable when it answers. And the
metadata is *best effort*: a missing title falls back to the BV id, `Uploader` to `"Bilibili"`
and every caption failure to an empty list — only a missing audio URL is treated as a resolve
failure, because that is the only field playback cannot live without.

Search exists because the endpoint that finds videos is **not** an answer machine. A keyword query
returns twenty uploads for a song, mixing the official video with 4K remasters, hour-long
compilations and videos that merely mention the words, so the app cannot pick one and call it the
song. `SearchAsync` therefore returns candidates and the page shows them; the listener chooses,
which is the only step that can honestly decide. Two consequences shaped the code: the client
strips the `<em class="keyword">` highlight markup and converts `"4:30"` to seconds before the rows
reach the view model, and `BilibiliSearchResult` is a record of its own rather than a `Track`,
because a candidate has no audio URL and most of them will never be played.

Both calls go through one `BilibiliWeb.GetJsonAsync` helper rather than `GetFromJsonAsync`. The
convenience overload throws on any non-success status *and discards the body*, and the body is
where Bilibili explains itself — a rate limit reached the user as "could not reach Bilibili",
which sends them off to check a connection that was working fine. The helper also reads the
envelope's own `code` field (`-404`, `-403`, `-101`), because Bilibili reports most failures with
HTTP 200.

### 4.6 Layer 6 — Platform interop (`Interop/`)

**`WindowMaximizeHelper.cs` (153 lines)** — answers `WM_GETMINMAXINFO` (0x0024) with the work
area of the monitor the window is currently on, resolved per message via
`MonitorFromWindow` + `GetMonitorInfo`. This is what keeps the player bar on screen when the
borderless window is maximized (see §7.5).

---

## 5. Data flow and binding map

```
   User gesture               ViewModel chain                            View
   ────────────               ───────────────                            ────
   click nav item ──► MainWindowViewModel.IsHomeSelected = true ──► CurrentPage
                          (setter navigates)  │        └─► PageTitle / PageSubtitle
                                              └─► OnCurrentPageChanged raises derived props

   click artwork ──► OpenNowPlayingCommand ──► IsNowPlayingOpen ──► overlay Visibility
                        (CanExecute ← PlayerBar.HasTrack)          caption+body Visibility (inverted)

   click play ──► PlayerBar.TogglePlaybackCommand ──► PlayerBar.IsPlaying ──► PlayPauseGlyph / Tooltip
                                          └─► IAudioPlayer.Play/Pause

   click Import ──► Library.ImportFolderCommand ──► IFolderPicker ──► IMusicLibraryService
                     (IsBusy gate)                      PickFolder        ScanAsync
                                                                          ├─► Tracks (merged, deduped by path)
                                                                          ├─► LibraryStore.SaveAsync
                                                                          └─► StatusMessage / LibrarySummary

   pick a row ──► Library.PlayTrackCommand ──► TrackActivated ──► MainWindowViewModel
                     (ListBox selection)            (event)            ├─► PlayerBar.LoadTrack(track)
                                                                       └─► CurrentPage = Library

   resolve a BV ──► Bilibili.PlayCommand ──► TrackResolved ──► PlayerBar.PlayTrack(track)
                                        └──► TrackPersisted ──► Library.AddOrUpdateAsync
                                                                    └─► LibraryStore.SaveAsync

   clear ──► Library.ClearLibraryCommand ──► IConfirmationPrompt.Confirm
                                                 ├─ No  ──► StatusMessage = "Library left unchanged."
                                                 └─ Yes ──► Tracks.Clear() ──► LibraryStore.SaveAsync([])

   type a keyword ──► Bilibili.SearchCommand ──► IBilibiliSource.SearchAsync
                        (IsSearching gate)        └─► "GET /search?keyword=&limit="
                                                     └─► SearchResults (ranked, unfiltered)
                                                          └─► SearchSummary ──► candidate card
                                                               (visible only while HasSearchResults)

   pick a candidate ──► (code-behind) Bilibili.PlayCandidateCommand
                          ├─► clue = SearchText      (kept as Track.SearchClue)
                          ├─► SearchText = candidate.Bvid  ("Play BV…" lights up)
                          └─► PlayAsync(clue) ──► see below

   paste BV id ──► Bilibili.PlayCommand ──► IBilibiliSource
                     (IsBusy gate)            ├─► WbiSigner (keys cached 1 h, one fetch)
                                              ├─► ResolveAsync ("/resolve?bvid=" → audio URL + metadata)
                                              ├─► CaptionStore.SaveAsync (skip when the list is empty)
                                              └─► duration: metadata endpoint, else DashSegmentProbe
                                                     └─► Track (real title/uploader/cover,
                                                                StreamKey "bilibili:BV...", StreamUri)
                                                          └─► TrackResolved ──► MainWindowViewModel
                                                                                ├─► PlayerBar.PlayTrack
                                                                                └─► CurrentPage = Library

   choose a card ──► NowPlaying.ShowLyricsCommand / ShowVisualizerCommand ──► ActiveView
                        (chip always enabled; clears the                    ├─► IsLyricsSelected
                         auto-switch flag)                                  ├─► IsVisualizerSelected
                                                                            ├─► ShowsLyricList
                                                                            ├─► ShowsPlainLyrics
                                                                            ├─► ShowsCandidateList
                                                                            └─► ShowsEmptyLyricState

   play ──► PlayerBar.IsPlaying ──► AudioVisualizer.IsPlaying ──► WasapiAudioTap.Start/Stop
       (gated by Loaded / Unloaded / IsVisibleChanged too)   │  (desired-state pump, one session)
                                                            └─► recorder DataAvailable
                                                                  └─► SpectrumAnalyzer.Process/Decay
                                                                        └─► SpectrumFrame (immutable,
                                                                            monotonic timestamp)
                                                                              └─► SpectrumDisplay
                                                                                  (freshness + decay)
                                                                                    └─► OnRender

   track loaded ──► PlayerBar.CurrentTrack ──► OnCurrentTrackChanged
                          ├─► CoverImageLoader.LoadAsync(Track.CoverUri) ──► CoverImage ──► ImageBrush
                          │     (cancelled on the next track; result dropped if the track moved on)
                          └─► NowPlaying.LoadFor(track) ──► (cancel previous, bump version)
                                                                └─► TrackLyricProvider.GetLyricsAsync
                                                                      ├─► SongIdentityResolver (tags or
                                                                      │     cleaned video title; never the
                                                                      │     uploader as the artist)
                                                                      ├─► LyricCacheStore (hit / manual choice
                                                                      │     / offset, otherwise continue)
                                                                      ├─► CaptionStore (archived subtitles,
                                                                      │     online tracks only)
                                                                      ├─► ILrclibClient (exact, then search ≤10)
                                                                      ├─► LyricMatcher (score, margin, version)
                                                                      └─► LrcParser ──► LyricLookupResult
                                                                            └─► Apply() ──► Lyrics /
                                                                                PlainText / Candidates /
                                                                                EmptyTitle ──► the card

   engine clock ──► IAudioPlayer.PositionChanged ──► PlayerBar.PositionSeconds (guarded)
                                                          (or: drag seek, same property)
   drag seek ──► PlayerBar.PositionSeconds ──┬─► IAudioPlayer.Seek (drag release only)
                                             ├─► PositionText ──► time label
                                             ├─► seek slider Value (TwoWay)
                                             └─► MainWindowViewModel.OnPlayerBarPropertyChanged
                                                   └─► NowPlaying.UpdateActiveLyric
                                                         ├─► LyricLineViewModel.IsActive ──► DataTrigger
                                                         └─► ActiveLyricIndex ──► code-behind BringIntoView

   engine failure ──► IAudioPlayer.PlaybackFailed ──► PlayerBar.StatusMessage ──► red line in the bar
                                                 └─► PlayerBar.PlaybackFailed ──► MainWindowViewModel
                                                       (stream only, once)   └─► Bilibili.TryRefreshAsync
                                                                                   └─► PlayerBar.PlayTrack

   click lyric row ──► (NowPlayingView code-behind) MainWindowViewModel.SeekToLyricCommand(line)
                          └─► PlayerBar.PositionSeconds = line.Time + LyricOffset (see above)

   correct a match ──► Candidates[i].SelectCommand ──► NowPlaying.ApplyCandidateAsync
                          └─► GetLyricsAsync(options with RecordId) ──► stored as ManualRecordId

   shift the offset ──► OffsetEarlierCommand / OffsetLaterCommand / ResetOffsetCommand
                          ├─► LyricOffsetMilliseconds (clamped to ±30 s, 0.5 s steps)
                          ├─► UpdateActiveLyric(current position)  (re-highlight at once)
                          └─► SetOffsetMillisecondsAsync ──► LyricCacheStore (persisted per key)
```

Notable binding decisions:

- The window keeps **one** `DataContext` (the shell view model); every child view binds
  *through* it (`{Binding PlayerBar.TrackTitle}`, `{Binding NowPlaying.Lyrics}`). Nested view
  models without nested `DataContext`s keep the routing visible in the XAML — a typo shows up
  in the binding path instead of silently resolving against the wrong object.
- `PositionSeconds` is the **single source of truth** for the playback position. The slider,
  both time labels, the karaoke highlight and the engine seek all derive from it. It has two
  writers and they never overlap: the engine's `PositionChanged` while it plays, and the
  user's drag. `_isApplyingEnginePosition` marks engine writes so they do not loop back as
  seeks, and `_isSeeking` (fed by `SlimSlider.DraggingChanged`) makes the pointer win while
  the bar is held; the single seek happens on release.
- `Value="{Binding PlayerBar.PositionSeconds, Mode=TwoWay}"` on **both** seek sliders.
  One-way binding was the original choice and it was a bug: dragging moved the painted fill but
  never the view model, so the time label and the lyric highlight desynchronised.
- `IsChecked="{Binding IsHomeSelected, Mode=TwoWay}"` with **no** `Command` on nav items, so
  there is exactly one code path that navigates.
- The overlay's transport controls bind to the *same* `PlayerBar` properties as the player
  bar, so the two never disagree — the overlay simply covers the bar.
- **Provenance is a binding, not a caption.** `SectionLabel`, `SourceNotice`, `EmptyTitle`,
  `EmptyHint`, `SourceLabel` and `HasAlbumTags` are all derived from `Track.IsStream`, so every
  string that could misrepresent an online video ("album", "lyrics") flips with the track
  instead of being duplicated in two XAML files.
- No binding ever points at a remote URL. `CoverUri` is data; the `ImageBrush` binds to
  `CoverImage`, a decoded `BitmapImage`. WPF would happily fetch an `http` URI on the UI thread
  and stall the bar while it did.

---

## 6. Implementation completeness

Legend: ✅ done · 🟡 visual only / stubbed · ⛔ not started

| Feature | State | Where | Notes |
| --- | --- | --- | --- |
| Application bootstrap, theme merge, converter registration | ✅ | `App.xaml` | |
| Composition root: services built and injected | ✅ | `App.xaml.cs` | Hand-wired, no container (§7.10) |
| Borderless window, custom caption, drag, minimize/maximize/close | ✅ | `Views/Shell/CaptionBarView`, `Views/CaptionChrome.cs` | Glyph swaps on `StateChanged`, for both captions |
| Maximized window stays inside the work area | ✅ | `Interop/WindowMaximizeHelper.cs` | Multi-monitor and DPI safe |
| Design tokens + 20 reusable styles | ✅ | `Themes/` (7 dictionaries) | Only three literal colors remain in the view, all translucent white overlays (`#40FFFFFF`, `#26FFFFFF`, `#1AFFFFFF`) that exist purely for depth on the artwork |
| Sidebar navigation with active indicator | ✅ | `Views/Shell/SidebarView.xaml` | `Playlists` entry disabled on purpose |
| Home page | ✅ | `Views/Pages/HomePageView.xaml` | Both CTAs import and navigate; the track card shows the live library summary and hides its empty state once tracks exist |
| Local Music page | ✅ | `Views/Pages/LibraryPageView.xaml` | Real track table (`#`/TITLE/ALBUM/TIME) with hover and selected states, empty state, and selection starts playback |
| Player bar | ✅ | `Views/Shell/PlayerBarView.xaml` | Real cover art, metadata with an honest source/artist line, transport, seek, volume |
| Now Playing overlay (open/close, Esc, covers the shell) | ✅ | `Views/Overlays/NowPlayingView` | |
| Lyric sheet with karaoke highlight | ✅ | `ViewModels/Shell/NowPlayingViewModel.cs`, `Themes/NowPlaying.xaml` | Highlight logic is real, driven by `PositionSeconds`; the header reads `LYRICS · LRCLIB`, `LYRICS · VIDEO CAPTIONS` or `LYRICS · SAMPLE` from the actual result source |
| Plain (untimed) lyrics | ✅ | `NowPlayingViewModel.PlainText`, `LrcParser` | Shown in a scroll view with no per-line seeking; a synced payload that parses to nothing falls back to the plain text of the same record |
| Per-track lyric offset | ✅ | `NowPlayingViewModel.LyricOffsetMilliseconds`, `LyricCacheStore` | ±30 s in 0.5 s steps plus `Reset`, persisted per track key, `lyricTime = playbackTime - userOffset` |
| Correct or replace an automatic match | ✅ | `NowPlayingViewModel.RetryCommand`, `Candidates` → `SelectCommand` | A low-confidence or close-second match becomes a chooser (name, artist, album, duration, synced vs. plain); the chosen record id is stored and preferred on the next load |
| Spectrum visualiser following the audio | ✅ | `Controls/AudioVisualizer.cs`, `Services/Audio/WasapiAudioTap.cs` | Real analysis of the process's own output through WASAPI loopback, not a fake animation (§7.14) |
| Switch the right-hand card between visualiser and lines | ✅ | `NowPlayingViewModel.ActiveView`, `OverlayTabStyle` | Default is the visualiser; the `LYRICS` chip is always enabled, and a late lookup does not steal a card the user already chose |
| Click a lyric line to seek | ✅ | `Views/Overlays/NowPlayingView.xaml.cs` + `MainWindowViewModel` | |
| Custom-drawn slider (seek + volume) | ✅ | `Controls/SlimSlider.cs` | Click, drag, hover thumb |
| Play / pause | ✅ | `PlayerBarViewModel.TogglePlayback` → `LibVlcAudioPlayer` | Glyph follows `IsPlaying`, which follows the engine |
| Playback position advancing | ✅ | `Services/Audio/LibVlcAudioPlayer.cs` | 250 ms poll of `MediaPlayer.Time` into `PositionSeconds`; the lyric highlight and both labels follow it |
| Search filtering over the local library | ⛔ | `MainWindowViewModel.SearchText` | The header box is bound but nothing consumes the text; online search lives on the Local Music page instead |
| Library scanning / import folder | ✅ | `ViewModels/Pages/LibraryPageViewModel.cs` + `Services/Library/` | Folder picker → recursive scan → tag read → dedupe → `library.json`; both "Import folder" buttons are live |
| Audio decoding & output | ✅ | `Services/Audio/LibVlcAudioPlayer.cs` | LibVLC 3.0.24 through `LibVLCSharp`; mp3/flac/wav/m4a/aac/wma/ogg/opus, plus HTTP streams |
| Online source: Bilibili BV id to audio | ✅ | `ViewModels/Shell/BilibiliViewModel.cs` + `Services/Bilibili/` | Calls Bilibili's public web API from this process; the signed URL is resolved per play request and never stored |
| Search Bilibili by song or artist, then pick an upload | ✅ | `BilibiliViewModel.SearchCommand` + `/x/web-interface/wbi/search/type` | The endpoint returns a ranked mix of the official video, remasters, compilations and unrelated hits, so the app shows candidates instead of playing the first one (§4.5) |
| Expired stream URL re-resolved once | ✅ | `MainWindowViewModel.OnPlayerBarPlaybackFailed` | A CDN link that rejects the request is re-resolved and replayed once per play request, then reported |
| Tag reading | ✅ | `Services/Library/TrackMetadataReader.cs` | Title/artist/duration/album/track number feed both the table and the player bar |
| Cover art | ✅ (online) / 🟡 (local) | `Services/Artwork/CoverImageLoader.cs` | A Bilibili thumbnail is downloaded and painted in both artwork frames; embedded tag pictures for local files are not read yet, and those keep the glyph |
| Video metadata → UI | ✅ | `Services/Bilibili/BilibiliClient.cs`, `BilibiliViewModel` | Title, uploader, thumbnail and duration all come from the video-info endpoint; album and track number stay empty because a video has neither |
| Subtitle track → lyric pipeline | ✅ | `Services/Lyrics/{TrackLyricProvider,CaptionStore}.cs` | Archived per `StreamKey` and offered as its own source kind (`LYRICS · VIDEO CAPTIONS`) when LRCLIB has nothing; anonymous resolves usually return none, and the empty state says so |
| Automatic lyrics for local and online tracks | ✅ | `Services/Lyrics/`, `NowPlayingViewModel.LoadFor` | Identity → cache → LRCLIB exact → LRCLIB search → matcher, started on every track change and never blocking playback (§7.15) |
| `.lrc` parsing | ✅ | `Services/Lyrics/LrcParser.cs` | `[mm:ss]`, `.x`/`.xx`/`.xxx`, multiple stamps per line, `[offset:]`, BOM/CRLF, out-of-order lines and same-time pairs; malformed lines are dropped, never the session |
| Playlists | ⛔ | — | Nav entry disabled |
| Restart track, mute, volume | ✅ | `PlayerBarViewModel` | No queue exists, so the left transport button restarts the current track; mute is derived from `Volume` rather than stored |
| Favourites, recently played, shuffle, repeat, next | ⛔ | — | Buttons disabled; `next` says why in its tooltip |
| Settings / persistence | ⛔ | — | Nothing is written to disk |

Rough measure of "how much is real": **the whole loop a listener touches** — import, browse,
play, seek, pause, volume and online playback — is complete and verified. What is still missing
is what a *library* needs rather than what a *player* needs: a queue, search, `.lrc` lyrics,
artwork and anything that survives an online link expiring (§8).

---

## 7. Implementation approach — decisions and rationale

### 7.1 Design tokens before controls

Colors, brushes, radii and font sizes are declared once in `Themes/Colors.xaml`; every style
dictionary under `Themes/` references them, and the views reference the styles. **Why:** the
brief was "improve the UI", and a token layer makes subsequent redesign a one-file edit
instead of a find-and-replace across 1,000 lines of markup. It also made it possible to prove
the theme is consistent: a script diffs used vs. defined `StaticResource` keys and currently
reports zero missing *and* zero unused resources.

### 7.2 Pages are visibility-toggled views, not a `Frame`

Home and Local Music are two sibling subtrees in the same window, shown by
`Visibility="{Binding IsLibrarySelected, Converter={StaticResource BoolToVisibility}}"`.
**Why:** for a two-page shell, `Frame` + `NavigationService` would add journal semantics,
page-lifecycle bugs and a navigation stack nobody asked for. Toggling visibility keeps
navigation instantaneous and state-preserving.

### 7.3 Navigation is driven by the *setter*, not a command

The nav items are `RadioButton`s bound `IsChecked="{Binding IsHomeSelected, Mode=TwoWay}"`
with **no** `Command`; `IsHomeSelected`'s setter assigns `CurrentPage`. **Why:** the original
design combined a `Command` with a one-way `IsChecked`. Any activation path that changed
`IsChecked` without executing the command (UI Automation's `SelectionItemPattern`, for
example) left the sidebar highlighting "Local Music" while the content still showed Home —
observed and reproduced. A settable property makes the highlight and the content the same
piece of state, so they cannot diverge.

### 7.4 Now Playing is an overlay, not a page

It is driven by a dedicated `IsNowPlayingOpen` flag rather than a `PlayerPage` member, and
renders as a `Grid.RowSpan="3"` layer above the shell. **Why:** the requirement was that it
covers the *entire* application (sidebar, header, player bar). Modelling it as a page would
have meant either hiding the shell piecemeal or adding a second navigation axis — and closing
it would need a history stack to know where to return. A boolean gives "return to whatever
was underneath" for free, and `Esc`/back both simply set it to `false`.

### 7.5 Borderless windows need `WM_GETMINMAXINFO`

`WindowChrome` sizes a maximized window to the monitor **plus** the invisible resize border.
Measured on this machine: `GetWindowRect` = `(-7,-7) 1934×1094` against a work area of
`(0,0) 1920×1032`, which put the bottom 62px of the window — most of the 88px player bar —
under the taskbar. `Interop/WindowMaximizeHelper.cs` answers `WM_GETMINMAXINFO` with the work
area of the monitor the window is on. **Why not the alternatives:** setting
`MaxHeight`/`Margin` in a `StateChanged` handler only works for one monitor and one DPI;
clamping to `SystemParameters.WorkArea` breaks on multi-monitor setups and when the taskbar
is not on the primary screen. The message hook resolves the monitor per message, so it
survives monitor changes, taskbar auto-hide and DPI transitions.

### 7.6 The sliders are custom-drawn

`Controls/SlimSlider.cs` subclasses `Slider` and overrides `OnRender` to paint the track, the
filled portion and a hover thumb. **Why:** the stock `Slider` template is heavy (two
`RepeatButton`s, a `Thumb`, a `Track`) and restyling it is notoriously fiddly — a hand-rolled
`Track` template was tried first and produced a `Track.DecreaseRepeatButton` with
`ActualWidth == 0`, i.e. the accent fill never rendered. Subclassing keeps *all* of `Slider`'s
behaviour (binding, keyboard stepping, value coercion) and replaces only pixels, which is a
far smaller surface than a full template.

The one thing a hand-drawn `Slider` must not forget is the invalidation the stock control gets
for free. `ValueProperty` is re-declared in the static constructor with
`AffectsRender | BindsTwoWayByDefault`. **Why:** `Value` is inherited from `Slider`, whose own
metadata does not invalidate the visual — the stock control does not need it, because its
`Track`, `Thumb` and `RepeatButton` parts are re-laid-out by bindings when the value changes.
A `SlimSlider` has an empty `ControlTemplate` and paints everything in `OnRender`, so without
`AffectsRender` the fill only repainted when something else asked for a new frame: resizing,
hovering, dragging. The symptom was reported as "the progress bar and the sound are stuck until
I move the mouse over them", and it was entirely a missing invalidation — the position, the
labels and the audio were correct the whole time. `BindsTwoWayByDefault` has to be repeated
because `OverrideMetadata` replaces the flags rather than merging them.

### 7.7 Lyrics are data, not view state

The overlay renders whatever `NowPlayingViewModel` puts in `Lyrics`. Each row owns an
`IsActive` flag, and `UpdateActiveLyric` walks the list once and flips only the changed rows.
**Why:** a playback tick can fire many times per second; re-creating or re-rendering the whole
list would be wasteful and would break scrolling. The view model never parses text, never talks
HTTP and never decides whether a match is good enough — it consumes a finished
`LyricLookupResult` and turns it into rows, a plain-text block, a candidate list or one of five
empty states. Everything upstream is a service that can be faked in a unit test, and everything
downstream is a binding.

### 7.8 The shell ships with sample data

`PlayerBarViewModel.CurrentTrack` defaults to `Track.Sample`, and the shell mediator calls
`NowPlaying.LoadFor(...)` with it once, in its constructor. **Why:** the entire player bar and
Now Playing view are only reviewable against real content. Shipping a sample track keeps the
UI verifiable and screenshot-able while the audio engine is absent; `Track.Empty` remains the
"nothing loaded" state that disables the transport.

### 7.9 Unimplemented controls are visibly disabled

Import used to sit in this list. It left the day the pipeline became real: both
"Import folder" buttons now call `Library.ImportFolderCommand` and disable themselves only
while `IsBusy`.

Playlists, shuffle, repeat, next/previous, favourite and the "more" button are either
`IsEnabled="False"` or `CanExecute = nameof(HasTrack)` / `nameof(CanOpenNowPlaying)`. **Why:**
an inert control that *looks* live is worse than an obviously disabled one — it turns a
missing feature into a bug report.

### 7.10 The composition root is hand-written

`App.xaml.cs` builds `AudioFileScanner`, `TrackMetadataReader`, `LibraryStore`,
`MusicLibraryService`, `FolderPicker`, `ConfirmationPrompt`, `BilibiliClient`,
`LibVlcAudioPlayer` and `SampleLyricProvider`, then hands the services, `IBilibiliSource` and `IAudioPlayer` to
`MainWindowViewModel` and wraps that in `MainWindow`.
It then `await`s `Library.RestoreAsync()` *after* `Show()`, so a slow disk never delays the
first frame. `App.xaml` no
longer declares `StartupUri`, so the window is created after the services exist, and
`MainWindow.xaml` carries only a `d:DataContext` design instance. **Why not DI:** the object
graph is eight types deep and has no scopes, lifetimes or configurability to speak of; a
container would add a package, a registration file and a runtime indirection floor to save
about ten lines of explicit `new`. And because every construction site names a concrete type,
swapping the audio engine really was one line in one place — which is the actual reason for
having a composition root here.

`App` also keeps the engine as a field and disposes it in `OnExit`: LibVLC keeps an audio
output device and a native plugin host open until they are released, so the object graph's
owner is the only place that can dispose them in the right order — a view-model `Dispose` would
run after the window is gone but before the message loop stops.

Two deliberate consequences: the view models never call `new` on a service (they take
interfaces), and the parameterless `MainWindowViewModel()` overload still exists but only for
the XAML designer — at runtime `App` always uses the injected constructor.

### 7.11 The shell mediates; the children never talk to each other

`MainWindowViewModel` subscribes to `PlayerBar.PropertyChanged` and forwards two things to
`NowPlaying`: `PositionSeconds` → `UpdateActiveLyric`, `CurrentTrack` → `LoadFor`. Neither
child holds a reference to the other or to the shell. **Why:** `PositionSeconds` is the one
piece of state the two halves genuinely share (the bar produces it, the overlay consumes it),
and letting one child subscribe to the other would make the pair order-dependent — creating
`NowPlaying` before `PlayerBar` would silently hold a different position source, and closing
the overlay would have to unhook a subscription it does not own. A single edge, owned by the
mediator that already owns both lifetimes, keeps the graph acyclic and disposable.
`CommunityToolkit.Mvvm`'s `WeakReferenceMessenger` was the alternative; it was rejected
because for exactly one message type it trades a greppable method for a hidden publish site.

### 7.12 Online audio is a source, not a second player

An online track is an ordinary `Track` with `StreamUri` set instead of `FilePath`; nothing
above `IAudioPlayer` knows where the bytes come from. **Why:** the obvious alternative — a
separate online engine with its own timer, its own `IsPlaying` and its own position, which is
what most copy-paste integrations look like — puts two writers on `PositionSeconds` and two
owners on the play button. The whole point of the `_isApplyingEnginePosition` / `IsSeeking`
guards (§5) is that exactly one thing owns the playhead; a second engine would have forced a
second set of guards in a second class.

Two consequences are deliberate. The duration is read from the stream's `sidx` box *before*
playing, because the seek slider derives its range from `Track.Duration` and a fragmented MP4
declares none in `mvhd`; and `StreamKey` (`bilibili:BV...`) is the durable identity while
`StreamUri` is thrown away, because a signed CDN URL expires within hours and persisting it
would produce a library of dead rows. Expiry is handled where it surfaces: a failed play of a
stream re-resolves the key once and replays, and a second failure is reported as text.

### 7.13 Borrowed metadata is labelled as borrowed

A Bilibili video supplies a title, an uploader, a thumbnail, a duration and sometimes a
subtitle track. None of those are the four fields a music player displays, and the difference is
not cosmetic: the "artist" of an official MV is the label that uploaded it, the "album" does not
exist, the "cover" is a video frame, and a subtitle is a transcript of whatever the video says —
an intro, a spoken word, a machine transcription. **Why the UI says so:** filling the same
`Track` record is unavoidable (the transport only understands tracks), but *presenting* those
values under the local-file labels would be a quiet lie, and the user is the one who would act on
it. So the model carries the true shape (`Artist` = uploader, `Album` = `null`, `HasCover`,
`IsStream`) and the presentation layer derives its wording from the source: `SourceLabel`
("Bilibili video"), the `SUBTITLES` / `LYRICS` header, the provenance footer, and the subtitle
empty state that names the login restriction instead of blaming the video. Every one of those is
a derived property on a view model, so the honest version cannot drift out of sync with the
dishonest one.

### 7.14 A custom-drawn control that animates must own its clock

The visualiser looks like a styling exercise and is not. Three separate mechanisms have to line
up, and each one failed on its own during development:

- **The data is captured, not invented.** WASAPI process loopback gives real spectra, which is
  why the bars react to a bass drop. A timer-driven pseudo-random walk would have been a tenth of
  the code and would have looked *approximately* right in a screenshot — and completely wrong to
  anyone actually listening, which is the only review that matters for a visualiser.
- **The render loop is tied to playback, not to the control's lifetime.** `CompositionTarget.
  Rendering` is subscribed while `IsPlaying` and the visualiser is visible, and unsubscribed
  otherwise; a paused overlay falls back to a 33 ms `DispatcherTimer` that advances the idle wave
  and costs nothing while hidden. The alternative (an always-running `DispatcherTimer` that calls
  `InvalidateVisual`) redraws a 32-bar spectrum forever in the background.
- **A clock that only invalidates is not a clock.** The first version of this control subscribed
  `CompositionTarget.Rendering` and called `InvalidateVisual()` alone. That is enough while new
  audio frames arrive, because each frame changes the drawn values — it is not enough when the
  capture goes quiet, and the fallback wave then froze outright. The clock now advances an
  animation phase from a real `Stopwatch` delta, clamped so a resumed window does not jump (§4.2).
- **Fresh data is a timestamp, not a flag.** `Analyzer.IsSilent` cannot distinguish "the music is
  quiet" from "the callback stopped"; a dead capture looked exactly like a silent passage and the
  bars kept their last heights forever. The tap publishes an immutable `SpectrumFrame` carrying a
  monotonic timestamp, and the display layer decays anything older than 450 ms back into the idle
  wave (§4.2).
- **Selection lives in the view model, in exactly one place.** The card switch was first built
  with `ToggleButton`s, whose `IsChecked` flipped locally when a click did not reach the command,
  leaving both chips lit while the card did not change. A `Button` with a bool `Tag` plus a
  string-valued `Trigger` failed too: a string `"True"` never equals a boxed `bool`, so the
  selected chip never lit. The version that holds is a `RadioButton` per chip in one `GroupName`,
  styled by `OverlayTabStyle` off `IsChecked="{Binding …, Mode=OneWay}"` — WPF's own checked state
  is then a *projection* of `ActiveView`, and the only way to change the card is the command that
  changes `ActiveView` (§4.3).

---

### 7.15 Lyrics arrive by themselves, and a guess is allowed to be wrong

Every track change starts a lookup and returns to the caller immediately. The chain is
`SongIdentity` → cache → stored manual choice → archived captions → LRCLIB `/api/get` (only when
the identity is trustworthy) → LRCLIB `/api/search` (at most 10 candidates) → `LyricMatcher`.

**Identity first, and video titles are not trusted.** A local file is described by its real tags;
when the performer is blank the placeholder `Unknown Artist` is dropped rather than sent, because
the API treats `artist_name` as a filter. An online track is described by its cleaned video title
plus the uploader as a *display* field only — the uploader is never passed as the artist. The
user's original search keyword rides along in `Track.SearchClue` and is the one piece of evidence
a clickbait title cannot supply. When the title reads like a video title — `LyricText` changes it
by removing display noise, or it carries brackets, separators or exclamation marks — and the
keyword names something materially different, the keyword's title becomes the song title and its
left-hand side the artist hint (`IdentitySource.SearchClue`). When the title already names the
song, the keyword does not overwrite it: an upload plainly titled `Creep` keeps its title, because
there the keyword names the artist rather than the song. Either way the clue is never a confirmed
identity, and `Source` records which reading was used. Bracket contents are not stripped wholesale
either: `Live`, `Cover`, an instrumental marker, `Remix` are extracted into `SongVersion`, because
they predict that the timeline will not line up.

Noise removal is boundary-aware. A word is only cut when it stands alone or fills a bracket: the
bare-substring form deleted the `mv` inside `MVP`, the `official` inside `Officialdom` and the `hd`
inside `HDMI`, silently renaming real songs. Short words and Latin words therefore require a
boundary on both sides, while longer CJK words may still match loosely, because a Chinese title
runs them together with the song name.

**A video title is read several ways, and all readings are tried.** One string can name the song,
the performer, the recording session and the video at once, and a database record matches at most
one of those readings. So `SongIdentity` carries `TitlesToTry`: a quoted name first, because
`performer<guillemets>song<guillemets>` is the uploader being explicit — a title that wraps the
song name in guillemets and then describes the recording session is a video about that song, and
nothing else in that string belongs in a query; then the title with the quote removed; then both
sides of a separator. `LyricMatcher` scores a
record against every name and keeps the best. `TrackLyricProvider` then searches for the leading
reading with the artist appended and, only while nothing is confident, for up to three alternates
*without* the artist — the performer was read off the same title the alternate came from, so
pairing them would just repeat the guess the alternate exists to work around. Searching the whole
video title is what produced a hard "no entry" for a song the database does hold twenty times.

The two sides of `Artist - Song` are never promoted to artist. Real uploads use both directions
(`Radiohead - Creep` and `Creep - Radiohead`), so guessing from the punctuation would invent an
artist and then let the artist penalty reject the correct record. The one reliable artist signal is
the `performer《song》` order, and that one is used.

**Scoring is a stated policy, not a guarantee.** The weights and gates are constants in
`LyricMatcher` precisely so they can be read and tested:

| Signal | Weight | Rule |
| --- | ---: | --- |
| Title similarity | 62 | Bigram Dice over cleaned text; below `TitleAcceptThreshold` (0.62) the candidate is rejected outright |
| Artist agreement | 24 | Applied only when the track's artist is trusted; ≥0.7 adds proportionally, <0.35 subtracts 0.75× |
| Artist unknown | 24 × 0.45 | A missing artist is information, so it neither proves nor disproves a match |
| Duration | 10 | Within 4 s full credit, scaled to 25 s, then −5: a Bilibili length contains intro and outro |
| Album | 4 | Auxiliary, only at ≥0.8 similarity |
| Version conflict | reject | `SongVersionRules.IsConflict` — a live or cover take is never laid over a studio timeline |
| Instrumental for a sung track | −20 | Kept visible as a candidate but never auto-accepted |

Acceptance needs score ≥ 78 **and** a margin ≥ 8 over the runner-up **and** no version
compromise. Anything else becomes `NeedsSelection`: the overlay lists up to five candidates with
name, artist, album, duration and synced-vs-plain, and each row has a `Use` button that reloads
that exact record id and remembers it. This is the correction path for a wrong or uncertain
automatic match; `Change lyrics` forgets the stored choice and looks again.

"Runner-up" means a **different** song by a different performer. The database holds the same
recording several times at slightly different lengths (two rows of one song by one performer
differing by five seconds both scored 96), and asking which of two identical answers is meant is a question
with no wrong reply. Rows with the same name, performer and version therefore count as one
candidate, the length closest to the track's wins, and the chooser lists each song once. Without
that rule a perfectly good match was reported as "several records fit" and shown as a dead end.

**Failure is a state, not an exception.** 404, 503, 429, a timeout, a network error and an
unparseable payload all become `Failed` with a message, are never cached, and never touch
playback. Only a genuine "the database has no such song" is cached, and only for three days.

**The offset fixes lead/lag, not a different arrangement.** `lyricTime = playbackTime -
userOffset`, so `+0.5 s` shows a line stamped 10 s at playback 10.5 s and clicking that line
seeks to 10.5 s. A fixed offset cannot repair a varispeed upload, a re-arrangement or a cut
video; for those the honest answers are picking another record or reading the plain text.

### 7.16 A lookup publishes on the thread that owns the view model

`NowPlayingViewModel.LoadAsync` captures `Dispatcher.FromThread(Thread.CurrentThread)` before its
first `await` and routes every publish step — the stored offset, `Apply`, the failure state and
`IsLoading = false` — through `RunOnOwnerAsync`, which calls the action inline when it is already on
that dispatcher and otherwise `await`s `Dispatcher.InvokeAsync`.

This is not defensive decoration. `ObservableCollection<T>` throws
`"This type of CollectionView does not support changes to its SourceCollection from a thread
different from the Dispatcher thread"` the moment a lyrics row is added from a thread that does not
own the collection view, and a lyric lookup is awaited across arbitrary thread-pool threads: the
HTTP continuation, the file read in `LyricCacheStore`, the `ConfigureAwait(false)` inside
`TrackLyricProvider`. Relying on the continuation happening to resume on the caller's context works
until the day it does not, and the failure mode is an exception on a background thread — the worst
kind to diagnose. Capturing the dispatcher makes "UI state changes on the UI thread" a property of
the method instead of a coincidence of scheduling, and it keeps the version check
(`version == _loadVersion`) inside the marshalled block, so a stale result is still dropped on
arrival rather than merely cancelled.

`NowPlayingViewModelThreadingTests` pins both halves down: a result completed on a pool thread still
adds its rows on the dispatcher (`Lyrics.CollectionChanged` is observed and `CheckAccess()` asserted
at that moment), and a result for the previous track is still dropped after a track change. Removing
the marshalling makes the first test fail, which is the point of having it.

### 7.17 A `Run` binding is two-way unless told otherwise

`Run.Text` binds two-way by default, unlike every other text property in WPF. Binding it to a
read-only property therefore does not render an empty run: it throws

```
System.Windows.Markup.XamlParseException: TwoWay or OneWayToSource binding cannot work on the
read-only property 'Album' of type 'SongPlayer.ViewModels.LyricCandidateViewModel'
```

from inside `FrameworkTemplate.LoadTemplateXaml`, i.e. during a *layout pass* triggered by
`Measure`. Nothing catches it, so the process terminates — the overlay did exactly that the first
time a lyric chooser appeared on screen, and the Windows Application log carried the stack trace
through `MS.Win32.HwndSubclass.SubclassWndProc` to `SongPlayer.App.Main`.

Two lessons are kept in the code:

- The chooser's detail line is a single view-model property (`LyricCandidateViewModel.Detail`)
  bound to `TextBlock.Text`, not a row of `<Run>` elements. `Text` is one-way by default, so the
  trap cannot come back while someone is editing the template.
- A binding-trace assertion cannot catch this class of bug at all: the listener sees no warning
  because the failure is an exception, not a message. The only reliable gate is to lay the state
  out for real, which is why `NowPlayingViewBindingTests` realises the candidate list with actual
  item containers. Reverting the `Detail` binding makes that test fail with the exact exception
  above, so the regression is pinned rather than described.

## 8. Known limitations

- **The visualiser needs Windows 10 2004 or newer.** Process loopback capture was added in
  10.0.19041; on older builds (or when the device refuses the stream) the card falls back to its
  idle wave and prints the reason in a dim line under it, because a disabled-looking player is
  worse than a quiet one — but a silently broken one is worse than both.
- **A minimized window keeps capturing.** WPF keeps `IsVisible == true` for a minimized window, so
  the visualiser's "loaded and visible" rule still holds and the tap stays open while the app is
  in the taskbar. Idle CPU stays at the capture cost only, and no frames are drawn.
- **Playback is one track at a time.** There is no queue: finishing a track stops at its end,
  and the left transport button restarts the current track instead of going back in history
  (§4.5). Shuffle, repeat and next need a queue before they can mean anything.
- **Online playback depends on a third party.** Bilibili audio comes from its public web API,
  called directly from this process; a change on their side can break it, and the failure is
  reported as a sentence rather than a stack trace. Local playback is unaffected.
- **An unsigned or unknown request is refused plainly.** Bilibili answers burst searches with
  `412`/`429`, and its envelope reports most other problems with HTTP 200 and a `code` field.
  Both are mapped to what the user can act on ("rate-limiting requests right now", "that video
  does not exist", "asked for a login"), and waiting a minute is the only fix for the first, so
  that message carries no button. Subtitles additionally require a signed-in session; the caption
  step is therefore best effort and never fails a play.
- **Search results are strangers.** A keyword that matches a song returns the official video next
  to remasters, hour-long compilations and videos that merely mention the words. Nothing filters
  by duration, view count or uploader type: the list is the endpoint's ranking, and choosing is
  the listener's job (§4.5).
- **Saved streams need one extra network round trip.** An online track is remembered by its
  stream key, so it reappears after a restart with its cached title, uploader, length and
  thumbnail — but a restored row carries no URL until it is selected, and selecting it resolves
  the key again before loading. If the resolve fails, that row cannot be played at all.
  Subtitle lines and fetched lyrics are archived separately in
  `%LOCALAPPDATA%\SongPlayer\captions` and `%LOCALAPPDATA%\SongPlayer\lyrics`.
- **An unindexable stream has no seek range.** `sidx` gives the length for a normal Bilibili
  segment; a source that omits it shows `00:00` and a slider the engine refuses to move.
- **The library is real but shallow.** Import, persistence and the track table work, yet there
  is no search over it, no sort, no per-track removal (only `Clear library`, which now asks for
  confirmation but still has no undo), no progress report for a very large folder, and no
  duplicate detection beyond the file path and the stream key. The header's search box filters
  nothing — online search is a different box on the Local Music page, and it queries Bilibili,
  not the imported rows.
- **Local artwork is still a glyph.** Embedded tag pictures are not read; a local file shows the
  `ArtworkGlyph` frame. Only online tracks get a real image, because only they carry a URL.
- **Subtitles are usually invisible.** Most Bilibili subtitle tracks are served only to a
  signed-in session, so an online track usually has none. They are kept as a fallback source
  under `LRCLIB` rather than as the primary one, because a transcript of a video is not the same
  thing as the song's lyrics.
- **A wrong lyric match is possible, and the fix is manual.** Titles are cleaned and scored, but
  a cover, a live take or an edited upload can still resemble the studio release closely enough
  to win. When the score or the margin is not good enough the app asks instead of guessing; when
  it guesses wrong, `Change lyrics` forgets the record and the chooser lists the alternatives.
- **The offset cannot repair a different edit.** A fixed shift fixes lead or lag only. A
  varispeed upload, a re-arranged version or a video with a long spoken intro will drift, and no
  amount of offset fixes that — the answer is another record or the plain text.
- **A Bilibili length is not the song length.** Video length includes intros and outros, so it
  is used as supporting evidence with a tolerance, never as an exact filter; a record whose
  duration disagrees can still be the right one.
- **LRCLIB is a third party.** A local file with clean tags usually resolves on the first exact
  query; a video title may need the search endpoint and a manual pick. Nothing is cached as "no
  lyrics" when the request failed, so a retry is always meaningful.
- **Persistence is index-and-lyrics only.** The track index, each track's lyric offset and its
  manual lyric choice survive a restart; volume, window state, the current page and the current
  track do not.
- **Dark theme only.** The token layer makes a light theme possible but no switch exists;
  `App.xaml` hard-codes `ThemeMode="Dark"`.
- **The Home page has no view model of its own.** It renders static copy plus the library
  summary and the two CTAs, all reached through the shell.
- **Accessibility is partial.** `AutomationProperties.Name` is set on nav items and the
  artwork/back buttons; cards, chips and the icon-only transport buttons still rely on
  tooltips.

---

## 9. How to extend the project

The seams are deliberate; each of the following is a local change.

**Add a page**
1. Add a member to `Models/PlayerPage.cs`.
2. Add a settable `IsXSelected` property in `MainWindowViewModel` (mirroring
   `IsHomeSelected`) and extend the `PageTitle` / `PageSubtitle` switches.
3. Add a `Controls/`-style `UserControl` under `Views/Pages/`, bind its root
   `Visibility` to `IsXSelected`, register it in `MainWindow.xaml` with `Grid.Row="1"`, and
   add a `RadioButton` to `Views/Shell/SidebarView.xaml`.

**Play a whole album (add a queue)**
The engine plays one file at a time and reports `PlaybackEnded`. A queue is a new
`PlayerQueue` service owning an ordered list plus a cursor, constructed in `App.xaml.cs` and
injected into `PlayerBarViewModel`; `OnEnginePlaybackEnded` then asks it for the next track
and calls `PlayTrack`, and it becomes the `CanExecute` gate for `next`/`previous`/`shuffle`/
`repeat`. Nothing in the views changes except the disabled flags on those four buttons.

**Add a second online source**
`IBilibiliSource` is shaped for Bilibili's own endpoints. For a second platform, add a sibling
interface (or generalise this one to `IMusicSource.ResolveAsync(string id)` plus
`SearchAsync(keyword)`), implement it next to `BilibiliClient`, and construct it in
`App.xaml.cs`; reuse `DashSegmentProbe` if the source serves a fragmented MP4, or let the platform
metadata supply the duration. `BilibiliViewModel` is the template for the input: a `Search`
command that fills a candidate collection and a `Play` command that resolves and raises
`TrackResolved` with an ordinary `Track`. Keep the two entry points separate — a candidate is not
a track, and resolving is what turns one into the other.

**Add or fix a Bilibili endpoint**
`BilibiliClient` has one private method per endpoint, each built as
`await _signer.SignAsync("bvid=…")` followed by `BilibiliWeb.GetJsonAsync`, so a new endpoint is
one signing line plus one parse method, and its failures inherit the shared status-to-sentence
mapping. Keep the signature and the request inseparable: the `w_rid` covers the exact query string
that is sent, so a parameter added after signing is a silent `-403`. `BilibiliWeb.JsonOptions` is
case-insensitive on purpose — the payloads mix `base_url` and `baseUrl` — and any new field is read
through the tolerant `ReadString`/`ReadLong` helpers rather than a typed DTO, because an unreadable
field must degrade one value instead of failing the whole resolve.

**Swap the audio engine**
`IAudioPlayer` is the only contract the view models know, and `MediaPlayerAudioPlayer` (WPF
`MediaPlayer`) was replaced by `LibVlcAudioPlayer` exactly this way: one new class, one line in
`App.xaml.cs`, no view and no view-model change. Any replacement only has to report
`PositionChanged` with the engine's own playhead and never echo a position back as a seek.

**Add a second visualiser style**
`AudioVisualizer.OnRender` is the only place that knows the shape. `SpectrumDisplay` exposes the
32 band levels, a seam pulse, the idle phase and `ShowIdle`; a bars/radial/waveform variant reads
the same values and replaces the drawing. Keep the tap ownership and the
`Reconcile()` gating (`Loaded` / `Unloaded` / `IsVisibleChanged` / `IsPlaying`) intact — that is
what keeps a hidden card free — and do not read `SpectrumAnalyzer` directly: the capture thread
mutates it, and `SpectrumFrame` is the snapshot the UI is allowed to touch.

**Change how lyrics are matched**
`LyricMatcher` holds every threshold as a named constant and returns `IsConfident` plus the
runner-up, so a tweak is one number and one test. Two rules are worth knowing before editing it:
a record must match a name in `SongIdentity.TitlesToTry`, and rows of the same song by the same
performer count as one candidate rather than as an ambiguity (§7.15). If a different database is wanted, implement
`ILrclibClient` (three methods) and construct it in `App.xaml.cs`; nothing above it knows about
HTTP. If a different *identity* is wanted, `ISongIdentityResolver` is the single place a `Track`
becomes a `SongIdentity`.

**Add another lyric source**
`LyricSourceKind` names the sources, and `TrackLyricProvider.TryCaptionsAsync` is the model for a
new one: answer before the network is touched, and return a `LyricLookupResult` that carries its
own `Source`. Do not merge two sources into one list — the header label is derived from the
source, and a transcript is not the same claim as a lyric sheet (§4.3).

**Give the lyric cache a new schema**
`LyricCacheStore.SchemaVersion` is compared on load, and a mismatch deletes the entry instead of
misreading it. Bump it and old files are forgotten safely; there is no migration to write. It is
at 2 today, bumped once (from 1) when the way a video title is read as a song name was fixed:
entries written before that recorded `no lyrics` for tracks the corrected rules can find, and a
remembered miss would have hidden the fix for three days.

**Load real artwork for local files**
The online path is the template: `PlayerBarViewModel.OnCurrentTrackChanged` already starts a
cancellable load and publishes a frozen `BitmapImage` as `CoverImage`, and both artwork frames
bind to it. Add a second loader that reads the `TagLib` picture frame from `Track.FilePath` and
assign the same property; the glyph stays as the `HasNoCover` state and nothing in the views
changes. `ArtworkFrameStyle` and the overlay's `Viewbox` already handle sizing.

**Filter and sort the library**
`LibraryPageViewModel` owns the only `Tracks` collection. Put the filter and the sort keys in
the view model (an `ICollectionView`, or a derived collection raised next to `LibrarySummary`)
rather than in the view, so "no results" is a property such as `HasNoMatches` instead of a
`DataTrigger` per state.

Note the box on that page already means something else: `BilibiliViewModel.SearchText` queries
Bilibili and is not a filter over `Tracks`. Wire the header's `MainWindowViewModel.SearchText`
to the local filter and let the two boxes say what they do, rather than merging them — one asks
a remote service for strangers, the other narrows rows the app already owns.

**Remove a single track**
De-duplication lives in `LibraryEntry.Key` (a path for a local file, a stream key for an online
one); removing a row is that operation reversed plus a `SaveAsync`. Never delete the audio file —
`Clear library` documents that the index is the only thing SongPlayer deletes. Ask
`IConfirmationPrompt` first if the action is not obviously reversible.

---

## 10. Quality gates currently enforced

These were checked and are currently clean; they are worth keeping clean:

| Gate | How | Current result |
| --- | --- | --- |
| Builds without warnings | `dotnet build` | 0 warnings, 0 errors |
| English only | `grep -rlP '[\x{4e00}-\x{9fff}]' --include=*.xaml --include=*.cs --include=*.md .` | every hit is deliberate: the CJK keyword tables in `Services/Lyrics/LyricText.cs` (noise and version words a Chinese title uses, plus the video-title fixtures that cover them in the lyric and Bilibili tests, including real server messages such as `账号未登录`). No UI string, comment or doc outside those tables and fixtures is non-English |
| No missing style keys | diff used vs. defined `StaticResource` keys | 0 missing |
| No dead style keys | same diff, reversed | 0 unused |
| Binding paths resolve | every `{Binding X}` in `Views/` checked against the view model it inherits | checked manually, not by the compiler — bindings fail at runtime in WPF |
| Native dependency present | `bin/Debug/net10.0-windows/win-x64/libvlc/win-x64/libvlc.dll` after a build | `RuntimeIdentifier=win-x64` alone pins the process to x64 (verified PE machine 0x8664), so the solution keeps its single `Any CPU` platform and Visual Studio stays launchable |
| Comments on non-obvious code | comment-line ratio per file | 34% overall; every public type and command documented |
| Runtime smoke test | launch, navigate every page, open/close the overlay, maximize, seek | passes at 1180×760 and 1920×1032 |
| Custom-drawn controls repaint on their own | drive the app, screenshot the same pixel row over several seconds, compare the accent fill width | `SlimSlider` fill grows monotonically while playing (§7.6) |
| Pipeline tests | `dotnet test SongPlayer.Tests/SongPlayer.Tests.csproj` | 116 passed, 0 failed — LRCLIB HTTP status mapping against a stub handler (including a duration the service would reject), matching thresholds and the duplicate-record rule, LRC edge cases, identity resolution (quoted names, alternate readings, no invented artist), cache isolation, and the view model's stale-result and offset rules (§7.15); the real `NowPlayingView` hosted against the real shell view model with the WPF binding trace asserted quiet, and a lyric result completed on a pool thread publishing on the dispatcher (§7.16); the Bilibili client's endpoint parsing, envelope-code mapping and hostile-subtitle refusal, plus fixed signature vectors for `WbiSigner` (§4.5). The earlier capture-lifecycle suite was removed with the project it lived in and is not part of this count |

---

*Generated from the state of the repository at the time of writing. Line counts are exact for
that revision.*
