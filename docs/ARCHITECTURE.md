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
| Only NuGet dependency | `CommunityToolkit.Mvvm` 8.4.2 |
| C# source | 12 files, 1,164 lines (30% comment lines) |
| XAML | 4 files, 1,622 lines |
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
├── App.xaml.cs                        │  Application composition root
├── AssemblyInfo.cs                   ─┘
│
├── Themes/                           ─┐
│   ├── Colors.xaml                    │  LAYER 1 — Design system
│   └── Controls.xaml                  │  (no logic, only tokens + styles)
│                                      ─┘
├── MainWindow.xaml                   ─┐
├── MainWindow.xaml.cs                 │  LAYER 2 — View
├── Controls/SlimSlider.cs             │  (layout, templates, window chrome)
├── Converters/BoolToVisibilityConverter.cs
│                                      ─┘
├── ViewModels/                       ─┐
│   ├── MainWindowViewModel.cs         │  LAYER 3 — Presentation state
│   └── LyricLineViewModel.cs          │  (all UI state + commands)
│                                      ─┘
├── Models/                           ─┐
│   ├── Track.cs                       │  LAYER 4 — Domain data
│   ├── PlayerPage.cs                  │  (immutable records / enums)
│   ├── LyricLine.cs                   │
│   └── SampleLyrics.cs                │
│                                      ─┘
└── Interop/                          ─┐
    └── WindowMaximizeHelper.cs        │  LAYER 5 — Platform interop
                                       ─┘  (Win32, no UI knowledge)
```

**Dependency direction is strictly downward**: `Interop` knows nothing about the view,
`Models` know nothing about WPF, and `ViewModels` reference `Models` only. The view layer is
the only place that knows all of them. There is no service/DI layer yet — deliberately,
because there is nothing to inject until the audio engine exists.

### Line counts by layer

| Layer | Files | Lines |
| --- | ---: | ---: |
| Design system (`Themes/`) | 2 | 622 |
| View (`MainWindow.*`, `Controls/`, `Converters/`) | 4 | 1,483 |
| Presentation state (`ViewModels/`) | 2 | 401 |
| Domain data (`Models/`) | 4 | 89 |
| Platform interop (`Interop/`) | 1 | 146 |
| Composition root (`App.*`, `AssemblyInfo.cs`) | 3 | 45 |
| **Total source** | **16** | **2,786** |

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

**`Themes/Controls.xaml` (533 lines)** — 20 styles, each built only on the tokens above, so
retheming means editing `Colors.xaml` alone. Grouped by purpose:

| Group | Styles |
| --- | --- |
| Text | `DisplayTextStyle`, `TitleTextStyle`, `BodyTextStyle`, `CaptionTextStyle`, `SectionLabelStyle` (small uppercase section headers), `GlyphTextStyle` (Segoe MDL2 Assets icon font) |
| Navigation | `NavItemStyle` — a `RadioButton` with a left accent bar that appears only on the active item |
| Buttons | `IconButtonStyle` (34×34 round ghost icon), `PlayButtonStyle` (filled accent circle), `ArtworkButtonStyle` (clickable album art with a hover ring), `SecondaryButtonStyle` (neutral pill), `CaptionButtonStyle`, `CaptionCloseButtonStyle` (red hover) |
| Surfaces | `CardStyle`, `WellStyle` (muted inner surface for thumbnails/icon wells) |
| Input | `SearchBoxStyle` — placeholder text, magnifier glyph, accent focus border |
| Now Playing | `ArtworkFrameStyle` (rounded frame + drop shadow), `LyricLineStyle`, `LyricListStyle`, `ChipStyle` |

### 4.2 Layer 2 — View

**`MainWindow.xaml` (977 lines)** — one window, five regions, marked by banner comments:

1. **Caption bar** (row 0, 40px) — brand mark on the left, window buttons on the right.
   Buttons are marked `shell:WindowChrome.IsHitTestVisibleInChrome="True"` so they receive
   clicks even though the whole strip is a drag area.
2. **Sidebar** (column 0 of the body, 244px) — `BROWSE` section with `Home`, `Local Music`,
   and a disabled `Playlists` entry, plus a `Preview build` footer.
3. **Page host** (column 1) — a shared header (`PageTitle`, `PageSubtitle`, search box)
   above two visibility-toggled pages:
   - **Home**: gradient hero banner with two CTAs, a `UniformGrid` of three quick-access
     cards, and a compact "library is empty" card.
   - **Local Music**: toolbar (`0 tracks · 0 albums`, disabled import button) and a centred
     empty state.
4. **Player bar** (row 2, 88px) — artwork button (opens Now Playing), title/artist,
   favourite button, transport controls, seek bar, volume.
5. **Now Playing overlay** — a `Grid.RowSpan="3"` layer with its own caption strip (back
   button + `NOW PLAYING` + window buttons), an artwork/facts/transport card on the left and
   the lyric sheet on the right.

**`MainWindow.xaml.cs` (173 lines)** — the only code-behind, and it deliberately holds **no
UI state**. It contains exactly four concerns:

- Window chrome: `MinimizeButton_Click`, `MaximizeButton_Click`, `CloseButton_Click`,
  `UpdateMaximizeGlyph()` (swaps `E922`/`E923` on both the main and overlay caption buttons).
- Chrome polish: an `OnMouseLeftButtonDown` override that stops the caption from stealing
  focus, and `WindowMaximizeHelper.Attach(this)`.
- Overlay keyboard handling: `OnPreviewKeyDown` closes the overlay on `Esc` (preview-time so
  a focused lyric row cannot swallow the key).
- Lyric scrolling: subscribes to `MainWindowViewModel.PropertyChanged`, and on
  `ActiveLyricIndex` calls `BringIntoView()` on the new row via
  `Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)`. This is view-only work — the view
  model never touches a `ListBox`.

**`Controls/SlimSlider.cs` (299 lines)** — a `Slider` subclass that paints itself. Five
dependency properties (`TrackBrush`, `FillBrush`, `ThumbBrush`, `TrackThickness` = 4.0,
`ThumbDiameter` = 11.0). It inherits *all* behaviour from `Slider` (binding, keyboard
stepping, value coercion, `ValueChanged`) and only replaces rendering (`OnRender`) and mouse
handling (click-to-seek + drag). The thumb dot appears only on hover or while dragging, so
the resting player bar stays flat.

**`Converters/BoolToVisibilityConverter.cs` (34 lines)** — one converter with an `Invert`
flag, registered twice in `App.xaml` as `BoolToVisibility` and `InverseBoolToVisibility`.
Avoids a `Style`/`DataTrigger` boilerplate for every "show this when X" case.

### 4.3 Layer 3 — Presentation state (`ViewModels/`)

**`MainWindowViewModel.cs` (358 lines)** — the single view model for the shell. Uses
`CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`), so no
`INotifyPropertyChanged` boilerplate is written by hand.

- **Input state** (7 `[ObservableProperty]` fields): `CurrentPage`, `CurrentTrack`,
  `IsPlaying`, `PositionSeconds`, `Volume` (70), `SearchText`, `IsNowPlayingOpen`.
- **Derived state** (computed getters, re-raised from `partial void On…Changed`): `HasTrack`,
  `PageTitle`, `PageSubtitle`, `TrackTitle`, `TrackArtist`, `TrackAlbum`,
  `TrackAlbumPosition`, `DurationSeconds`, `PositionText`, `DurationText`, `PlayPauseGlyph`,
  `PlayPauseTooltip`, `HasLyrics`, `ArtworkGlyph`.
- **Navigation**: `IsHomeSelected` / `IsLibrarySelected` (settable, see §7.3) and the
  `ShowLibrary` command.
- **Overlay**: `OpenNowPlaying` (gated by `CanExecute = nameof(HasTrack)`), `CloseNowPlaying`.
- **Transport**: `TogglePlayback` (gated by `HasTrack`), `SeekToLyric`.
- **Lyrics**: `Lyrics` collection, `ActiveLyricIndex`, `LoadLyrics()`, and
  `UpdateActiveLyric(TimeSpan)` which picks the last line whose start time ≤ position.

**`LyricLineViewModel.cs` (43 lines)** — one row: immutable `Time`/`Text` plus a mutable
`IsActive`. Putting the highlight flag on the row means a playback tick notifies only the two
rows that change, instead of invalidating the whole list.

### 4.4 Layer 4 — Domain data (`Models/`)

| File | Contents |
| --- | --- |
| `Track.cs` (24) | `sealed record Track(string Title, string Artist, TimeSpan Duration)`; `Track.Empty` (nothing loaded) and `Track.Sample` ("Midnight Drive" / "The Wanderers" / 3:32) preloaded so the player bar is reviewable before the audio engine exists |
| `PlayerPage.cs` (15) | `enum PlayerPage { Home, Library }` — the overlay is intentionally *not* a member |
| `LyricLine.cs` (12) | `sealed record LyricLine(TimeSpan Time, string Text)` |
| `SampleLyrics.cs` (38) | `IReadOnlyList<LyricLine> ForSampleTrack` — 16 placeholder lines, one every 12s |

These are pure data: no WPF types, no notification, no behaviour.

### 4.5 Layer 5 — Platform interop (`Interop/`)

**`WindowMaximizeHelper.cs` (146 lines)** — answers `WM_GETMINMAXINFO` (0x0024) with the work
area of the monitor the window is currently on, resolved per message via
`MonitorFromWindow` + `GetMonitorInfo`. This is what keeps the player bar on screen when the
borderless window is maximized (see §7.5).

---

## 5. Data flow and binding map

```
   User gesture                 ViewModel                        View
   ────────────                 ─────────                        ────
   click nav item ──► IsHomeSelected = true ──► CurrentPage ──► PageTitle / PageSubtitle
                        (setter navigates)         │              page Visibility
                                                   └─► OnCurrentPageChanged raises derived props

   click artwork ──► OpenNowPlayingCommand ──► IsNowPlayingOpen ──► overlay Visibility
                                                                     caption+body Visibility (inverted)

   click lyric row ──► (code-behind) SeekToLyricCommand(line) ──► PositionSeconds
                          └─► PositionText                    ├─► seek slider Value (TwoWay)
                              UpdateActiveLyric ──► LyricLineViewModel.IsActive ──► DataTrigger
                                                └─► ActiveLyricIndex ──► code-behind BringIntoView

   click play ──► TogglePlaybackCommand ──► IsPlaying ──► PlayPauseGlyph / PlayPauseTooltip
```

Notable binding decisions:

- `Value="{Binding PositionSeconds, Mode=TwoWay}"` on **both** seek sliders. One-way binding
  was the original choice and it was a bug: dragging moved the painted fill but never the
  view model, so the time label and the lyric highlight desynchronised.
- `IsChecked="{Binding IsHomeSelected, Mode=TwoWay}"` with **no** `Command` on nav items, so
  there is exactly one code path that navigates.
- The overlay's transport controls are bound to the *same* view-model properties as the
  player bar, so the two never disagree — the overlay simply covers the bar.

---

## 6. Implementation completeness

Legend: ✅ done · 🟡 visual only / stubbed · ⛔ not started

| Feature | State | Where | Notes |
| --- | --- | --- | --- |
| Application bootstrap, theme merge, converter registration | ✅ | `App.xaml` | |
| Borderless window, custom caption, drag, minimize/maximize/close | ✅ | `MainWindow.xaml`, `.xaml.cs` | Glyph swaps on `StateChanged` |
| Maximized window stays inside the work area | ✅ | `Interop/WindowMaximizeHelper.cs` | Multi-monitor and DPI safe |
| Design tokens + 20 reusable styles | ✅ | `Themes/` | Only three literal colors remain in the view, all translucent white overlays (`#40FFFFFF`, `#26FFFFFF`, `#1AFFFFFF`) that exist purely for depth on the artwork |
| Sidebar navigation with active indicator | ✅ | `MainWindow.xaml` | `Playlists` entry disabled on purpose |
| Home page | ✅ layout / 🟡 content | `MainWindow.xaml` | Static copy; "0 tracks imported" is hard-coded |
| Local Music page | ✅ layout / 🟡 content | `MainWindow.xaml` | Empty state only; no `ItemsControl` yet |
| Player bar | ✅ | `MainWindow.xaml` | Artwork, metadata, transport, seek, volume |
| Now Playing overlay (open/close, Esc, covers the shell) | ✅ | `MainWindow.xaml`, `.xaml.cs` | |
| Lyric sheet with karaoke highlight | ✅ | `ViewModels/`, `Themes/Controls.xaml` | Highlight logic is real, driven by `PositionSeconds` |
| Click a lyric line to seek | ✅ | `MainWindow.xaml.cs` + VM | |
| Custom-drawn slider (seek + volume) | ✅ | `Controls/SlimSlider.cs` | Click, drag, hover thumb |
| Play / pause | 🟡 | `MainWindowViewModel.TogglePlayback` | Flips `IsPlaying`; no audio, no timer |
| Playback position advancing | ⛔ | — | `PositionSeconds` only changes by seeking |
| Search filtering | ⛔ | `SearchText` | Box is bound but nothing consumes the text |
| Library scanning / import folder | ⛔ | — | Buttons are disabled, not fake-working |
| Audio decoding & output | ⛔ | — | No `NAudio`/`MediaFoundation` integration yet |
| Tag reading, real cover art | ⛔ | `ArtworkGlyph`, `TrackAlbum` | Procedural gradient + glyph placeholder |
| `.lrc` parsing | ⛔ | `Models/SampleLyrics.cs` | Swap-in point is isolated |
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
in `Themes/Controls.xaml` references them, and the views reference the styles. **Why:** the
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

The overlay renders whatever the view model puts in `Lyrics`. Each row owns an `IsActive`
flag, and `UpdateActiveLyric` walks the list once and flips only the changed rows. **Why:**
a playback tick can fire many times per second; re-creating or re-rendering the whole list
would be wasteful and would break scrolling. Because the view only consumes
`IReadOnlyList<LyricLine>`, a real `.lrc` parser can replace `SampleLyrics` with no view
change at all.

### 7.8 The shell ships with sample data

`MainWindowViewModel.CurrentTrack` defaults to `Track.Sample` and the lyrics are preloaded.
**Why:** the entire player bar and Now Playing view are only reviewable against real content.
Shipping a sample track keeps the UI verifiable and screenshot-able while the audio engine is
absent; `Track.Empty` remains the "nothing loaded" state that disables the transport.

### 7.9 Unimplemented controls are visibly disabled

Import, playlists, shuffle, repeat, next/previous, favourite and the "more" button are either
`IsEnabled="False"` or `CanExecute = nameof(HasTrack)`. **Why:** an inert control that *looks*
live is worse than an obviously disabled one — it turns a missing feature into a bug report.

---

## 8. Known limitations

- **No audio.** `IsPlaying` is a flag; `PositionSeconds` never advances on its own, so the
  karaoke highlight only moves when you seek or click a lyric line.
- **No library.** Both pages are empty states; `0 tracks · 0 albums` is static text.
- **No tag reading.** Album, track number and artwork are placeholders
  (`TrackAlbum`, `TrackAlbumPosition`, `ArtworkGlyph`).
- **No persistence.** Volume, window state and page are not remembered between runs.
- **Dark theme only.** The token layer makes a light theme possible but no switch exists;
  `App.xaml` hard-codes `ThemeMode="Dark"`.
- **Single-window assumptions.** `MainWindow.xaml.cs` reaches into `LyricList` by name for
  scrolling; if a second window ever hosts a lyric sheet, that helper needs to move.
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
3. Add a `RadioButton` to the sidebar and a `Grid` with
   `Visibility="{Binding IsXSelected, Converter={StaticResource BoolToVisibility}}"` to the
   page host.

**Wire real playback**
1. Replace the body of `TogglePlayback` with an audio-engine call and have the engine push
   `PositionSeconds` on a timer. Everything downstream (label, slider, lyric highlight,
   `ActiveLyricIndex` → scroll) already works off that one property.
2. Drive `IsPlaying` from the engine's state rather than from the click, so the glyph cannot
   lie.

**Load real lyrics**
Replace the body of `LoadLyrics()` with an `.lrc` parser that produces `LyricLine` values.
Nothing else changes — the view, the highlight logic and click-to-seek are all generic.

**Load real artwork**
Replace the glyph `TextBlock` inside the two artwork `Border`s with an `Image` bound to a new
`ArtworkSource` property. `ArtworkFrameStyle` and the overlay's `Viewbox` already handle
sizing.

**Import a library**
Add the scanner behind the two disabled "Import folder" buttons, populate a new
`ObservableCollection<Track>`, and replace the Local Music empty state with an
`ItemsControl` bound to it (toggle it with the existing `HasTracks`-style boolean pattern).

---

## 10. Quality gates currently enforced

These were checked and are currently clean; they are worth keeping clean:

| Gate | How | Current result |
| --- | --- | --- |
| Builds without warnings | `dotnet build` | 0 warnings, 0 errors |
| English only | `grep -rlP '[\x{4e00}-\x{9fff}]' --include=*.xaml --include=*.cs --include=*.md .` | no matches |
| No missing style keys | diff used vs. defined `StaticResource` keys | 0 missing |
| No dead style keys | same diff, reversed | 0 unused |
| Comments on non-obvious code | comment-line ratio per file | 25% overall; every public type and command documented |
| Runtime smoke test | launch, navigate every page, open/close the overlay, maximize, seek | passes at 1180×760 and 1920×1032 |

---

*Generated from the state of the repository at the time of writing. Line counts are exact for
that revision.*
