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

The project is currently a **complete, polished shell**: every visual and interaction
surface exists and behaves correctly, but the parts that need a real audio/tagging backend
(library scanning, playback, `.lrc` parsing, cover art) are deliberately stubbed. Nothing
in the UI pretends to work — unimplemented controls are visibly disabled.

---

## 2. At a glance

| Item | Value |
| --- | --- |
| UI framework | WPF, `net10.0-windows`, `UseWPF=true` |
| Language settings | `Nullable=enable`, `ImplicitUsings=enable` |
| Only NuGet dependencies | `CommunityToolkit.Mvvm` 8.4.2, `TagLibSharp` 2.3.0 |
| C# source | 32 files, 2,043 lines (34% comment lines) |
| XAML | 16 files, 1,712 lines |
| Window | 1180×760 default, 900×600 minimum, borderless with a custom caption |
| Theme | Dark only, token-driven |
| Build status | `dotnet build` → 0 warnings, 0 errors |
| Language policy | Every UI string, code comment and doc is English |

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
├── Controls/SlimSlider.cs             │
├── Converters/BoolToVisibilityConverter.cs
│                                      ─┘
├── ViewModels/                       ─┐
│   ├── MainWindowViewModel.cs         │  LAYER 3 — Presentation state
│   ├── Shell/                         │  (all UI state + commands;
│   │   ├── PlayerBarViewModel.cs      │   MainWindowViewModel mediates between
│   │   └── NowPlayingViewModel.cs     │   the two child view models)
│   └── LyricLineViewModel.cs          │
│                                      ─┘
├── Models/                           ─┐
│   ├── Track.cs                       │  LAYER 4 — Domain data
│   ├── PlayerPage.cs                  │  (immutable records / enums)
│   └── LyricLine.cs                   │
│                                      ─┘
├── Services/                         ─┐
│   ├── Audio/                         │  LAYER 5 — Services
│   │   ├── IAudioPlayer.cs            │  (interfaces + implementations;
│   │   └── SilentAudioPlayer.cs       │   no view, no WPF types)
│   ├── Library/                       │
│   │   ├── IMusicLibraryService.cs    │
│   │   ├── MusicLibraryService.cs     │
│   │   ├── IAudioFileScanner.cs       │
│   │   ├── AudioFileScanner.cs        │
│   │   ├── ITrackMetadataReader.cs    │
│   │   ├── TrackMetadataReader.cs     │
│   │   └── LibraryStore.cs            │
│   └── Lyrics/                        │
│       ├── ILyricProvider.cs          │
│       └── SampleLyricProvider.cs    ─┘
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
| Design system (`Themes/`) | 7 | 661 |
| View (`MainWindow.*`, `Views/`, `Controls/`, `Converters/`) | 19 | 1,744 |
| Presentation state (`ViewModels/`) | 4 | 551 |
| Domain data (`Models/`) | 3 | 65 |
| Services (`Services/`) | 11 | 498 |
| Platform interop (`Interop/`) | 1 | 153 |
| Composition root (`App.*`, `AssemblyInfo.cs`) | 3 | 83 |
| **Total** | **48** | **3,755** |

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

**Six style dictionaries (572 lines, 20 styles)** — split by control kind instead of one
`Controls.xaml`. `App.xaml` merges them in dependency order (`Colors.xaml` must come first
because everything references it). Each style is built only on the tokens above, so retheming
means editing `Colors.xaml` alone:

| File | Styles |
| --- | --- |
| `Typography.xaml` (48) | `DisplayTextStyle`, `TitleTextStyle`, `BodyTextStyle`, `CaptionTextStyle`, `SectionLabelStyle` (small uppercase section headers), `GlyphTextStyle` (Segoe MDL2 Assets icon font) |
| `Surfaces.xaml` (31) | `CardStyle`, `WellStyle` (muted inner surface for thumbnails/icon wells) |
| `Buttons.xaml` (246) | `IconButtonStyle` (34×34 round ghost icon), `PlayButtonStyle` (filled accent circle), `ArtworkButtonStyle` (clickable album art with a hover ring), `SecondaryButtonStyle` (neutral pill), `CaptionButtonStyle`, `CaptionCloseButtonStyle` (red hover) |
| `Inputs.xaml` (81) | `SearchBoxStyle` — placeholder text, magnifier glyph, accent focus border |
| `Navigation.xaml` (77) | `NavItemStyle` — a `RadioButton` with a left accent bar that appears only on the active item |
| `NowPlaying.xaml` (89) | `ArtworkFrameStyle` (rounded frame + drop shadow), `LyricLineStyle`, `LyricListStyle`, `ChipStyle` |

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
| `PlayerBarView.xaml(.cs)` | 147 + 12 | Artwork button (opens Now Playing), metadata, favourite button, transport, seek bar, volume. No code-behind logic. |

**`Views/Pages/`**

| File | Lines | Contents |
| --- | ---: | --- |
| `HomePageView.xaml(.cs)` | 181 + 12 | Gradient hero banner with two CTAs, a `UniformGrid` of three quick-access cards, and a compact "library is empty" card. |
| `LibraryPageView.xaml(.cs)` | 75 + 12 | Toolbar (`0 tracks · 0 albums`, disabled import button) and a centred empty state; visibility driven by `IsLibrarySelected`. |

**`Views/Overlays/NowPlayingView.xaml(.cs)`** (349 + 113) — the `Grid.RowSpan="3"` layer:
its own caption strip (back button, `NOW PLAYING`, `OverlayMaximizeButton`), an
artwork/facts/transport card on the left, and the lyric sheet on the right. Its code-behind
holds the two genuinely view-only concerns: `ActiveLyricIndex` → `BringIntoView()` scrolling
(deferred to `DispatcherPriority.Loaded` so freshly realised containers have a valid position)
and click-to-seek on a lyric row.

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
handling (click-to-seek + drag). The thumb dot appears only on hover or while dragging, so
the resting player bar stays flat.

**`Converters/BoolToVisibilityConverter.cs` (38 lines)** — one converter with an `Invert`
flag, registered twice in `App.xaml` as `BoolToVisibility` and `InverseBoolToVisibility`.
Avoids a `Style`/`DataTrigger` boilerplate for every "show this when X" case.

### 4.3 Layer 3 — Presentation state (`ViewModels/`)

Four view models. The shell composes the two children and is the only type that knows both;
the children never reference each other or their parent (§7.11). All use
`CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`), so no
`INotifyPropertyChanged` boilerplate is written by hand.

**`MainWindowViewModel.cs` (215 lines)** — navigation state plus composition; the mediator.

- **Own state**: `CurrentPage`, `SearchText`, `IsNowPlayingOpen`.
- **Derived state**: `IsHomeSelected` / `IsLibrarySelected` (settable, see §7.3), `PageTitle`,
  `PageSubtitle`.
- **Commands**: `ShowLibrary`, `OpenNowPlaying` (gated by `CanOpenNowPlaying`),
  `CloseNowPlaying`, `SeekToLyric(LyricLineViewModel?)`.
- **Composition**: exposes `PlayerBar` and `NowPlaying` as properties, so the views bind
  *through* them (`{Binding PlayerBar.TrackTitle}`) while the window keeps one `DataContext`.
- **Mediation**: `OnPlayerBarPropertyChanged` is the only edge between the two children —
  `PositionSeconds` → `NowPlaying.UpdateActiveLyric(...)`, `CurrentTrack` →
  `NowPlaying.LoadFor(...)` and a re-evaluation of `OpenNowPlayingCommand`.

**`ViewModels/Shell/PlayerBarViewModel.cs` (184 lines)** — transport plus track metadata, and
the single source of truth for the playback position.

- **Input state**: `CurrentTrack` (`Track.Sample` by default), `IsPlaying`, `PositionSeconds`,
  `Volume` (70).
- **Derived state**: `HasTrack`, `TrackTitle`, `TrackArtist`, `TrackAlbum`,
  `TrackAlbumPosition`, `ArtworkGlyph`, `DurationSeconds`, `DurationText`, `PositionText`,
  `PlayPauseGlyph`, `PlayPauseTooltip`.
- **Commands**: `TogglePlayback` (gated by `HasTrack`), which also drives the injected
  `IAudioPlayer`; `OnPositionSecondsChanged` forwards seeks to the same interface.

**`ViewModels/Shell/NowPlayingViewModel.cs` (109 lines)** — the karaoke half of the overlay.

- `Lyrics` (an `ObservableCollection<LyricLineViewModel>`), `HasLyrics`, `ActiveLyricIndex`.
- `LoadFor(Track)` rebuilds the rows from the injected `ILyricProvider`;
  `UpdateActiveLyric(TimeSpan)` picks the last line whose start time ≤ position and flips only
  the rows that change.
- It holds **no** transport state: the position is pushed in by the shell mediator, so the
  overlay and the player bar always show the same instant.

**`LyricLineViewModel.cs` (43 lines)** — one row: immutable `Time`/`Text` plus a mutable
`IsActive`. Putting the highlight flag on the row means a playback tick notifies only the two
rows that change, instead of invalidating the whole list.

### 4.4 Layer 4 — Domain data (`Models/`)

| File | Contents |
| --- | --- |
| `Track.cs` (38) | `sealed record Track(string Title, string Artist, TimeSpan Duration, string? FilePath = null)` with `IsPlayable`, plus `Track.Empty` (nothing loaded) and `Track.Sample` ("Midnight Drive" / "The Wanderers" / 3:32) so the player bar is reviewable before the audio engine exists |
| `PlayerPage.cs` (15) | `enum PlayerPage { Home, Library }` — the overlay is intentionally *not* a member |
| `LyricLine.cs` (12) | `sealed record LyricLine(TimeSpan Time, string Text)` |

These are pure data: no WPF types, no notification, no behaviour. The sample lyrics that used
to live here are now `Services/Lyrics/SampleLyricProvider.cs` — they are a data *source*, not
a domain type.

### 4.5 Layer 5 — Services (`Services/`)

**`Library/` (330 lines)** — the import pipeline, split by responsibility so each piece can be
replaced or faked on its own. `IMusicLibraryService` is the only library type a view model may
depend on.

| File | Lines | Responsibility |
| --- | ---: | --- |
| `IMusicLibraryService.cs` | 31 | Contract: `ScanAsync` / `LoadAsync` / `SaveAsync` |
| `MusicLibraryService.cs` | 79 | Orchestration only: walks the scanner's output, asks the reader for tags, delegates persistence |
| `IAudioFileScanner.cs` / `AudioFileScanner.cs` | 24 + 42 | Extension whitelist and `Directory.EnumerateFiles` |
| `ITrackMetadataReader.cs` / `TrackMetadataReader.cs` | 19 + 67 | TagLibSharp title/artist/duration with an explicit exception whitelist, so one corrupt file cannot abort a scan |
| `LibraryStore.cs` | 78 | `library.json` read/write with a temp-file-then-move atomic write |

**`Lyrics/` (63 lines)** — `ILyricProvider.GetLyrics(Track)` and `SampleLyricProvider` (16
placeholder lines, one every 12 s). This seam exists so an `.lrc` parser drops in without
touching a view or a view model.

**`Audio/` (95 lines)** — `IAudioPlayer` (`IsPlaying`, `Play`, `Pause`, `Stop`, `Seek`) and
`SilentAudioPlayer`, which records the requested state without producing sound. The view
models depend on the interface only, so a real engine swaps in without touching the views
(§9).

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

   drag seek ──► PlayerBar.PositionSeconds ──┬─► IAudioPlayer.Seek
                                             ├─► PositionText ──► time label
                                             ├─► seek slider Value (TwoWay)
                                             └─► MainWindowViewModel.OnPlayerBarPropertyChanged
                                                   └─► NowPlaying.UpdateActiveLyric
                                                         ├─► LyricLineViewModel.IsActive ──► DataTrigger
                                                         └─► ActiveLyricIndex ──► code-behind BringIntoView

   click lyric row ──► (NowPlayingView code-behind) MainWindowViewModel.SeekToLyricCommand(line)
                          └─► PlayerBar.PositionSeconds (see above)
```

Notable binding decisions:

- The window keeps **one** `DataContext` (the shell view model); every child view binds
  *through* it (`{Binding PlayerBar.TrackTitle}`, `{Binding NowPlaying.Lyrics}`). Nested view
  models without nested `DataContext`s keep the routing visible in the XAML — a typo shows up
  in the binding path instead of silently resolving against the wrong object.
- `PositionSeconds` is the **single source of truth** for the playback position. The slider,
  both time labels, the karaoke highlight and the engine seek all derive from it, and the
  engine is never asked to advance it — so there is exactly one write path per gesture and no
  feedback loop between the two child view models.
- `Value="{Binding PlayerBar.PositionSeconds, Mode=TwoWay}"` on **both** seek sliders.
  One-way binding was the original choice and it was a bug: dragging moved the painted fill but
  never the view model, so the time label and the lyric highlight desynchronised.
- `IsChecked="{Binding IsHomeSelected, Mode=TwoWay}"` with **no** `Command` on nav items, so
  there is exactly one code path that navigates.
- The overlay's transport controls bind to the *same* `PlayerBar` properties as the player
  bar, so the two never disagree — the overlay simply covers the bar.

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
| Home page | ✅ layout / 🟡 content | `Views/Pages/HomePageView.xaml` | Static copy; "0 tracks imported" is hard-coded |
| Local Music page | ✅ layout / 🟡 content | `Views/Pages/LibraryPageView.xaml` | Empty state only; no `ItemsControl` yet |
| Player bar | ✅ | `Views/Shell/PlayerBarView.xaml` | Artwork, metadata, transport, seek, volume |
| Now Playing overlay (open/close, Esc, covers the shell) | ✅ | `Views/Overlays/NowPlayingView` | |
| Lyric sheet with karaoke highlight | ✅ | `ViewModels/Shell/NowPlayingViewModel.cs`, `Themes/NowPlaying.xaml` | Highlight logic is real, driven by `PositionSeconds` |
| Click a lyric line to seek | ✅ | `Views/Overlays/NowPlayingView.xaml.cs` + `MainWindowViewModel` | |
| Custom-drawn slider (seek + volume) | ✅ | `Controls/SlimSlider.cs` | Click, drag, hover thumb |
| Play / pause | 🟡 | `PlayerBarViewModel.TogglePlayback` → `IAudioPlayer` | Command and engine call are real; the shipped engine is silent |
| Playback position advancing | ⛔ | — | `PositionSeconds` only changes by seeking |
| Search filtering | ⛔ | `MainWindowViewModel.SearchText` | Box is bound but nothing consumes the text |
| Library scanning / import folder | 🟡 backend / ⛔ UI | `Services/Library/` is complete and untested-by-UI | `IMusicLibraryService` is built by the composition root but no page owns a collection yet; buttons stay disabled |
| Audio decoding & output | ⛔ | `Services/Audio/` | `IAudioPlayer` exists; `SilentAudioPlayer` is the only implementation |
| Tag reading, real cover art | ✅ backend / ⛔ UI | `Services/Library/TrackMetadataReader.cs` | TagLibSharp reads title/artist/duration; `ArtworkGlyph` / `TrackAlbum` are still placeholders |
| `.lrc` parsing | ⛔ | `Services/Lyrics/` | `ILyricProvider` is the swap-in point |
| Playlists | ⛔ | — | Nav entry disabled |
| Favourites, recently played, shuffle, repeat, next/previous | ⛔ | — | Buttons disabled or non-committal |
| Settings / persistence | ⛔ | — | Nothing is written to disk |

Rough measure of "how much is real": **all of the presentation layer and all of the
interaction logic that does not require media files** — i.e. roughly 70% of the code in the
repository — is complete and verified. The remaining 30% is a media backend that has not been
started, and it is fenced off behind clear seams (§9).

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

### 7.7 Lyrics are data, not view state

The overlay renders whatever `NowPlayingViewModel` puts in `Lyrics`. Each row owns an
`IsActive` flag, and `UpdateActiveLyric` walks the list once and flips only the changed rows.
**Why:** a playback tick can fire many times per second; re-creating or re-rendering the whole
list would be wasteful and would break scrolling. Because the view model only consumes
`ILyricProvider`, a real `.lrc` parser can replace `SampleLyricProvider` with no view change
at all.

### 7.8 The shell ships with sample data

`PlayerBarViewModel.CurrentTrack` defaults to `Track.Sample`, and the shell mediator calls
`NowPlaying.LoadFor(...)` with it once, in its constructor. **Why:** the entire player bar and
Now Playing view are only reviewable against real content. Shipping a sample track keeps the
UI verifiable and screenshot-able while the audio engine is absent; `Track.Empty` remains the
"nothing loaded" state that disables the transport.

### 7.9 Unimplemented controls are visibly disabled

Import, playlists, shuffle, repeat, next/previous, favourite and the "more" button are either
`IsEnabled="False"` or `CanExecute = nameof(HasTrack)` / `nameof(CanOpenNowPlaying)`. **Why:**
an inert control that *looks* live is worse than an obviously disabled one — it turns a
missing feature into a bug report.

### 7.10 The composition root is hand-written

`App.xaml.cs` builds `AudioFileScanner`, `TrackMetadataReader`, `LibraryStore`,
`MusicLibraryService`, `SilentAudioPlayer` and `SampleLyricProvider`, then hands the services
and `IAudioPlayer` to `MainWindowViewModel` and wraps that in `MainWindow`. `App.xaml` no
longer declares `StartupUri`, so the window is created after the services exist, and
`MainWindow.xaml` carries only a `d:DataContext` design instance. **Why not DI:** the object
graph is six types deep and has no scopes, lifetimes or configurability to speak of; a
container would add a package, a registration file and a runtime indirection floor to save
about ten lines of explicit `new`. And because every construction site names a concrete type,
swapping `SilentAudioPlayer` for a real engine is one line in one place — which is the actual
reason for having a composition root here.

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

---

## 8. Known limitations

- **No audio.** `IsPlaying` is a flag; `PositionSeconds` never advances on its own, so the
  karaoke highlight only moves when you seek or click a lyric line. `IAudioPlayer` is the
  seam, and its current implementation is silent by design (§4.5).
- **No library UI.** Both pages are empty states; `0 tracks · 0 albums` is static text. The
  scanning/tagging backend exists but nothing consumes it yet, so `MainWindow` still shows
  `Track.Sample`.
- **Placeholder artwork and album facts.** Album, track number and artwork are still
  `TrackAlbum`, `TrackAlbumPosition` and `ArtworkGlyph`, even though `TrackMetadataReader` can
  read real tags.
- **No persistence.** Volume, window state and page are not remembered between runs, and
  nothing is written to disk (`LibraryStore` is implemented but unused).
- **Dark theme only.** The token layer makes a light theme possible but no switch exists;
  `App.xaml` hard-codes `ThemeMode="Dark"`.
- **Two of the seven views have no view model.** Home and Library render static content, so
  they bind to the shell view model only for their title and visibility.
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

**Wire real playback**
`IAudioPlayer` already exists and `PlayerBarViewModel` already calls it from both
`TogglePlayback` and `OnPositionSecondsChanged`; the shipped implementation is
`SilentAudioPlayer`. So the work is:
1. Write an `NAudio`-backed `IAudioPlayer` (or a `MediaPlayer` wrapper) and construct it in
   `App.xaml.cs` instead of `SilentAudioPlayer`. Nothing else in the graph changes.
2. Have that engine report its position — e.g. a `PositionChanged` event that
   `PlayerBarViewModel` writes into `PositionSeconds`. Everything downstream (label, slider,
   lyric highlight, `ActiveLyricIndex` → scroll) already works off that one property.
3. Drive `IsPlaying` from the engine's state rather than from the click, so the glyph cannot
   lie.

**Load real lyrics**
Implement `ILyricProvider` with an `.lrc` parser that produces `LyricLine` values and construct
it in `App.xaml.cs` instead of `SampleLyricProvider`. Nothing else changes — the view, the
highlight logic and click-to-seek are all generic.

**Load real artwork**
Replace the glyph `TextBlock` inside the two artwork `Border`s with an `Image` bound to a new
`ArtworkSource` property on `PlayerBarViewModel` (sourced from `TrackMetadataReader`'s
`TagLib` picture frame). `ArtworkFrameStyle` and the overlay's `Viewbox` already handle
sizing.

**Import a library**
`IMusicLibraryService` is already built by the composition root; hand it to a new
`ViewModels/Pages/LibraryPageViewModel.cs`, give that view model
`ObservableCollection<Track> Tracks` plus `HasTracks`, bind an `ItemsControl` in
`Views/Pages/LibraryPageView.xaml` to it, and enable the two "Import folder" buttons behind
`ScanAsync`. Wire it into `MainWindowViewModel` with the same mediator pattern as
`PlayerBar` / `NowPlaying`: selecting a row is a command on the shell that assigns
`PlayerBar.CurrentTrack`.

---

## 10. Quality gates currently enforced

These were checked and are currently clean; they are worth keeping clean:

| Gate | How | Current result |
| --- | --- | --- |
| Builds without warnings | `dotnet build` | 0 warnings, 0 errors |
| English only | `grep -rlP '[\x{4e00}-\x{9fff}]' --include=*.xaml --include=*.cs --include=*.md .` | no matches |
| No missing style keys | diff used vs. defined `StaticResource` keys | 0 missing |
| No dead style keys | same diff, reversed | 0 unused |
| Binding paths resolve | every `{Binding X}` in `Views/` checked against the view model it inherits | checked manually, not by the compiler — bindings fail at runtime in WPF |
| Comments on non-obvious code | comment-line ratio per file | 34% overall; every public type and command documented |
| Runtime smoke test | launch, navigate every page, open/close the overlay, maximize, seek | passes at 1180×760 and 1920×1032 |

---

*Generated from the state of the repository at the time of writing. Line counts are exact for
that revision.*
