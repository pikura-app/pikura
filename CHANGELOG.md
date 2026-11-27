# Changelog

All notable changes to Pikura are documented in this file.

## [2.6.9]

### Fixed

- `SeasonalParticles` / `EffectParticles`: glyphs drawn from cached `GlyphImage` bitmaps; timers pause while the window is minimized or hidden, an ancestor is hidden, or the control is detached; occlusion checks only run while corner scenery or a sprite is shown (#27).
- Seasonal tint no longer overrides High Contrast border brushes and is reapplied when High Contrast turns off; ambient schedules reset when the animation clock restarts (#28).
- Gallery: artist-list centering only on programmatic reveal, single `SelectedArtist` notification, `RevealArtworkAsync` adopts only lone tabs, page loads serialized (#29).
- Inline and fullscreen viewers follow `ShowPixivEffects` / `ReduceMotion` changes via `SettingsService.Changed` (#30).
- Fullscreen Load more / Load all: `LoadUntilArtworkAsync` loads up to the viewed artwork, restored tabs adopt the cached artist gallery, `MergeDuplicateTabs` merges same-artwork tabs, and duplicate sources (same artworks in any order) are dropped.
- Gallery Natural cards are exactly one column wide, so the hover/reveal outline isn't covered.
- Library cards are edge-to-edge; `MasonryPanel` `StretchToFill` measures at the arranged width so a column isn't dropped; filter dropdowns use `MinWidth`.

### Changed

- `LibraryAutoIndexDownloads` moved to Settings › Storage & Naming; `LibraryDuplicateThreshold` is set only from Library › Duplicates.

## [2.6.8]

### Added

- `SeasonalTheme` / `SeasonalParticles`: Season, Holiday and Anime themes with Light/Dark palettes, intensity, corner scenery, falling decorations, built-in `PixelSprite` motif sprites and per-character ambient effects (lightning, embers, ripples, mist, wind, sparkles, rising, Dragon Balls). Settings: `SeasonalTheme`, `HolidayTheme`, `AnimeTheme`, `ThemeColorsFrom`, `ThemeMixDecorations`, `ThemeBlendAccent`, `SeasonalTint`, `SeasonalIntensity`, `SeasonalScenery`, `SeasonalEffects`.
- Overlay occlusion: corner scenery hides and the sprite moves to the nearest clear spot, avoiding buttons (including disabled), inputs, text, images and tag chips.
- `PixivEffects` / `EffectParticles`: effect-tag recognition (`pixiv…Effect`, `pixiv<Name><year>`) and per-tag overlays in the inline viewer and fullscreen; `ShowPixivEffects` setting.
- Pixivision Pages mode articles-per-page selector.
- Tests: `SeasonalThemeTests`, `PixivEffectsTests`.

### Fixed

- "Open artist gallery" clears a hiding artist filter, scrolls the artist list to the artist, reveals the artwork (`RevealArtworkAsync`) and adopts the tab into the gallery navigation list.
- Gallery hover ring (`Border.hoverRing`) drawn above the card so all four sides show.

## [2.6.7]

### Fixed

- Library scroll paging moved into `LibraryScrollPager`, with generation tracking against overlapping loads and a `PagingUnblocked` re-check after busy work or deletions (#25).
- `LibraryIndexService.DeleteFilesAsync` returns the IDs actually deleted; `DeleteAndUnloadAsync` adjusts `_offset` around in-flight page loads, and appended pages are de-duplicated (#25).
- Library preview locates the current card by instance / `LibraryFileId` and merges nav cards per file, so multi-page artworks navigate correctly (#25).
- Deleting closes the viewer tab showing the file and removes it from other tabs' navigation lists (#25).
- `LoadChipsAsync` replaces tag chips instead of appending; source links are limited to http/https via `LibraryLinks.SourceUrlFor` (#25).

### Added

- `tests/Pikura.Avalonia.Tests` (39 tests); `LibraryRepository.SearchAsync` is virtual for tests (#25).

## [2.6.6]

### Fixed

- `LibraryViewModel` is a singleton; `InlineArtworkViewer` looks up library files through `LibraryIndexService.Repository` instead of resolving a new view model per lookup (#24).
- Library scroll paging triggers on distance to the bottom instead of `ExtentDelta.Y > 0`, and waits for the first page before paging (#24).
- Gallery defers `FilteredArtworks` rebuilds during batch adds (`DeferFilterRebuild`), so cached-artist restore, Load More and recent works rebuild once (#26).
- Gallery load-more failures are caught and reported in the status bar; auto-load backs off 30 s after HTTP 429/503 and 5 s after other errors.

### Improved

- Fullscreen artwork strip uses `VirtualizingStackPanel` and loads thumbnails nearest the current artwork first.
- Fullscreen page thumbnails load 4 at a time, nearest the current page first.

## [2.6.5]

### Fixed

- Update downloads are written to `.part`, verified against the GitHub asset `size` and `digest` (SHA-256), then moved into place with a `.sha256` record; unverified or partial files are discarded on startup, on update check and before install (`UpdateCheckService.IsVerifiedDownload`).
- Update downloads retry transient failures up to 3 times and fail on a 60 s stall.

## [2.6.4]

### New

- History R-18 filter (All / Hide / Only, filtered in SQL) and Blur R-18 toggle, persisted (`HistoryR18Filter`, `HistoryBlurR18`).
- Fullscreen R-18 filter for the artwork strip and ← / → (`FullscreenR18Filter`), shown only when R-18 mode is on and the source contains R-18 works.
- `SearchableComboBox`: type-to-filter for Library artist, folder, character and series dropdowns.
- Analytics: Most Viewed Artists from viewed history (`GetTopViewedArtistsAsync`).

### Improved

- History calendar blacks out (fades) dates with no viewed history.
- Pages ↔ autoload and items-per-page changes keep the topmost visible item in view and open its page (`ScrollAnchor`) across Gallery, Rankings, Search, History, Collections and Pixivision.
- Fullscreen: cached page lists, parallel preview/original fetch, off-thread decode, recent decoded originals kept by device tier, directional prefetch of the next artwork, `PixivImageLoader.IsCached`.
- Fullscreen page grid upgrades tiles to Large (1200 px) and reuses the current original; page indicators sit above the artwork strip.

### Fixed

- Search and Gallery reset to page 1 on mode/page-size changes; Rankings/History reopened a stale page; Collections and Pixivision autoload restarted from page 1 (Pixivision also cleared the active source); Pixivision mode switches replaced date-filtered results.
- Page-grid thumbnails could be downgraded by a later lower-resolution load.
- History R-18 visibility updated from a background settings thread.

## [2.6.3]

### Improved

- Fullscreen strip: artist entries can load that artist into the Gallery (restored tabs adopt the loaded gallery list); → at the end of a single-artwork tab does the same.
- Strip outlines and scrolls to the current artwork; defaults to the active tab (or the current artist) rather than "This submission".
- Leaving fullscreen restores the side panel to the last page viewed.
- Ugoira zoom and pan in fullscreen.
- → past the last loaded artwork loads the next page; Esc closes the page grid before fullscreen.
- Strip text localized (en/ja/zh/ko).

### Fixed

- Viewer nav lists and the fullscreen strip used the paginated Gallery page; they now use the full filtered, sorted list (`NavigableArtworks`).
- Load all offered without a selected artist; Load all hidden while the Gallery was loading.
- Arrow keys limited to one artwork after opening the strip; arrow keys and strip clicks now share one navigation path.

## [2.6.2]

### New

- Library template rename for selected files or all filtered results, with editable templates, token picker, preview, collision numbering, sidecar moves, index updates, empty-folder cleanup, library-root safety and Undo.
- `%character%` and `%series%` filename tokens from AI tags.
- Library artist dropdown (with counts) and folder filter (includes subfolders).
- AI tagger content rating stored; files without pixiv info count as R-18 when rated explicit. pixiv's rating takes precedence.

### Improved

- Find original uses SauceNAO's web search when no API key is set (and when a key lacks API access), skips the slow Danbooru tag lookup, and opens SauceNAO's results page in the browser. Viewer button shows progress and the result.

## [2.6.1]

### New

- Pixivision search (pixivision.net `/s/?q=`) with paging, active-search chip, client-side category filter and date range on results.
- Clickable article tags open pixivision tag pages (`/t/{id}`); older entries without tag ids search by name.
- Recent Pixivision searches (last 10).
- Viewed-article history grouped by day (last 500) with filter, remove, pause and clear.

### Improved

- Saved Articles hides the feed's category, date, Featured and pager controls; Saved and History honour Grid/List and card size and have a title/tag filter.
- Back from an article returns to Saved, History or search results.
- Translated "Save for later" button.

## [2.6.0]

### New

- Quick Setup wizard shown once after the splash (existing users once after updating): language, pixiv sign-in, downloads with template presets/tokens/path preview, existing-file handling, notifications, content filters, appearance, startup/tray/updates, optional Hoshi and anime tagger installs; Settings → About → Run setup again.
- Accent colour setting with eight presets and a Windows System accent that follows changes.
- Popup viewer page bar and all-pages grid; Popup opens at the current page.
- Hoshi follows the app language: language instruction in the prompt, translated quick-action requests and notes.

### Improved

- Live language switching across all screens, status messages, side panel, Settings, download queue and Hoshi; localized History dates.
- Pixivision reloads in the new language immediately.
- Fullscreen opens at the current page; translated Blocklist scope options.

### Fixed

- AI filter treated pixiv `aiType` 1 ("not AI-generated") as AI; only `aiType` 2 is filtered now (feeds and downloads).
- Blank side panel after opening an artwork from a Hoshi chat and navigating back.

## [2.5.2]

### Fixed

- Natural-height cards overlapped the next column in Rankings, Viewed history and Discover artist works; cards now take their column's width.

## [2.5.1]

### Fixed

- Pixivision article list, Monthly Ranking/Featured sidebar and related articles were empty after pixivision.net switched to absolute article links.
- Returning from an article to an empty Pixivision list reloads it instead of showing the article's status.
- What's New no longer reappears on patch updates.

## [2.5.0]

### New

- Library — indexes downloads and existing download folders; full-text search (incl. Japanese/Chinese), folders, Character/Series filters, exact and similar duplicate finder, metadata backfill, reverse search.
- Background AI tagging of the Library (pauses while Hoshi answers).
- Hoshi chat history in a local database with search, pinning, folders, archive, filters, grouping and cleanup; existing chats migrate automatically.
- Hoshi conversation memory, artwork awareness and actions (search, similar art/artists, open, download, favourite, suggest tags).
- Stop button for Hoshi answers.
- GPU acceleration: DirectML tagger with CPU fallback, optional integrated GPU for Ollama, Auto/GPU/CPU setting.
- Touch and pen support with a touch mode.
- Resumable downloads, always-on rate-limit backoff, visible retry countdowns.
- Artwork metadata export (CSV/TSV/JSON/text).
- Collage picker and fullscreen navigation strip.
- Full Japanese, Chinese and Korean translations with live language switching.
- Velopack installer with delta updates on Windows, with one-time migration from the old installer.

### Improved

- Hoshi: answers stay with their chat, chats start on request, per-chat images incl. downloaded files, Similar Artists thumbnails, smoother streaming, smaller images for Describe.
- Recommended -instruct models and a warning for thinking models.
- Optional tab restore, remembered viewer panel state, scrollable info row.
- Background overlays accept pixiv IDs/links and bulk input.
- Faster, crash-safe settings saves; faster Library hashing and thumbnails; off-UI-thread image decoding; prioritised viewer images.
- Consistent buttons, light-mode contrast, full-width Natural rows, centred touch-mode text.
- Cached followed-artists list; resizable Pixivision sidebar.

### Fixed

- Ollama installer never launched without consent; Restart Now, restart banner and update-banner dismissal (#23).
- Clear message for blocked/intercepted networks, HTTP/3 fallback, retries after connection errors.
- Followed-artists index retry after startup network failure.
- Crash when opening an artwork from Hoshi on an unreliable connection.
- Empty replies from thinking models; Ollama model list on first try.
- Library thumbnails respect Blur R-18; Discover loading indicator.
- Bookmark thumbnails vanishing on next page; cropped portrait pages in the all-pages grid.
- Tab image reload flash; clipped History card dates; surprise migration prompt.

### Changed

- Removed the Bookmarks "Liked" tab (pixiv cannot list liked artworks).

## [2.1.0]

### New

- Collections — full support for Pixiv's collection-curation feature: browse Featured and All collections, open details, download selected/entire collections, bookmark collections, and post/read collection comments.
- Collage — open selected artworks as a single, unique collage tab; add or remove individual images; download the whole collage.
- Pixiv-connected actions — likes, public/private bookmarks, follows/unfollows, views, comments, replies, stickers, and Pixiv emoji now operate on your real Pixiv account.

### Improved

- Chrome-style drag-to-reorder for inline viewer tabs — tabs now lift and neighboring tabs slide aside smoothly as you drag.
- "View in new tabs" support across Gallery, Discover, Rankings, Pixivision, Bookmarks, Search, and Collections.
- Bookmarks now includes every artwork you've liked on Pixiv (even ones that aren't bookmarked) alongside your public/private bookmarks and bookmarked collections.
- Search now has a History dropdown so you can jump back to a previous search (your last 25 queries, with the filters used at the time) — click outside it to close.
- Search now surfaces Popular Tags as clickable chips so you can search by tag with one click.
- Synchronization updates immediately across the gallery, Discover, Rankings, Search, Pixivision, bookmarks, and the inline viewer.
- Public artwork statistics reflect Pixiv's public like, bookmark, and view counts.
- Comment threads support correct-thread replies, text and sticker comments, Pixiv custom-emoji shortcodes, and deletion of your own comments after confirmation.
- Refined light and dark themes with improved contrast, button hover rounding, and better caption-button visibility.
- System theme mode now follows the OS light/dark preference correctly and updates live when the system theme changes.
- Feature highlight screenshots updated to show the refreshed UI, themes, and new Pixiv-connected actions.
- Collections tile context menu now has an "Open on pixiv.net" link alongside Copy collection link.
- Windows uninstaller can now fully remove Pikura (settings, saved Pixiv login, download history/database, cached images, and any downloaded AI tagging models) or keep that data in place for a future reinstall.

### Fixed

- Pixivision Monthly Ranking/Featured sidebar banners were unreadable in light/dark mode; both now use a fixed black background with white text.
- Updated the Pixiv-connected actions preview GIF used on the feature highlights page.
