## Pikura 2.0.0

Pikura 2.0.0 is a **major release** — the biggest update yet, introducing three brand-new tabs, a new onboarding experience, and significant performance work across the app.

### New Features

- **Pixivision tab** — browse official Pixivision articles, artist interviews and featured spotlights without leaving the app; save articles, filter content, and go back to previous articles using the built-in calendar
- **Search tab** — global search across artworks, artists, novels and users with a single query
- **Viewed tab** — every artwork you open is remembered; scroll back through your history or use the built-in calendar to jump to any past day. Clear history from the past hour, day, week, month, year, or all time — or set it to clear automatically after a configurable retention window in Settings
- **Artwork background overlay** — use any artwork as a full-window background with per-image opacity, brightness, pan and zoom; cycle through up to five favorites
- **Gallery search** — search inside the gallery by tag, title, artist, caption, date range, R-18 mode, AI generation and more
- **Advanced filtering** — filter by AI generation, R-18 type, blocklist scope, tags, titles and artists independently in Gallery, Rankings, Discover, Search, Pixivision, Viewed and Bookmarks

### Performance Improvements

- Reduced memory usage and faster startup
- Smoother gallery scrolling
- More responsive download coordination
- Thread-safe animated image decoding with instant-display frame preloading

### Other Changes

- What's New onboarding splash showcasing new features on major updates
- GitHub Page link and View Changelog button on the About settings page
- Numerous bug fixes and UI polish throughout the app

---

## Pikura 1.8.5

Follow-up hotfix for bookmark pagination (issue #22 was still occurring after 1.8.4 for a different reason).

### Fixes

- **Private/public bookmarks still stalling pagination past ~96 items** — Pixiv's bookmarks endpoint occasionally returns a raw numeric JSON token for `works[N].id` (and related id fields) instead of a quoted string once you page deep enough into the list. The strict `string`-typed model threw a `JsonException` on that token, silently dropping the entire page and stalling pagination — a different root cause than the `lang=` fix shipped in 1.8.4. `BookmarkedArtwork.Id`, `BookmarkedArtwork.UserId`, and `BookmarkData.Id` now use a lenient converter that accepts either a JSON string or number.

---

## Pikura 1.8.4

Hotfix for bookmark pagination. Adds the `lang=` query parameter to Pixiv's bookmark AJAX endpoints so offset pagination no longer caps public/private bookmarks at ~96/144 images.

### Fixes

- **Bookmarks stopped loading after the first chunk** — `PixivClient.GetBookmarkedArtworksAsync`, `GetBookmarkedNovelsAsync`, and `GetRecentBookmarksAsync` now send `lang=` alongside `tag=` and `rest=`. Without this, Pixiv's server returns only the initial slice of bookmarks regardless of `offset`, causing the Bookmarks tab to stop at two pages for public (96 images) and three for private (144 images).

---

## Pikura 1.8.3

Local ONNX anime image tagger and a curated recommended-model catalog for Hoshi AI, plus a broad pass of Hoshi AI reliability, UI, and performance fixes.

### New Features

- **Anime image tagger (ONNX)** — new section in Settings → Hoshi AI lets you enable a local Danbooru-style tagger for accurate anime / Pixiv tag prediction. Supports WD SwinV2 Tagger v3, ML-Danbooru Caformer, and PixAI Tagger v0.9.
- **Curated recommended models** — one-click install chips for Ollama text and vision models, plus install buttons for ONNX anime taggers. Tagger models download from Hugging Face on demand.
- **Auto-tag downloaded images** — optional setting runs the selected ONNX tagger on every downloaded image and writes a sidecar `<filename>.tags.txt` file with predicted Character, Copyright, Artist, General, and Meta tags.
- **AI chat tag suggestions prefer the ONNX tagger** — the Hoshi AI panel's *Suggest tags* command uses the local tagger when enabled and installed, falling back to the vision model otherwise.
- **Hoshi AI in the inline viewer gets Similar Art / Similar Artists** — the compact Hoshi panel embedded in the artwork viewer now has the same recommendation actions as the full Hoshi tab.
- **Multi-page submission support for Hoshi AI** — a new "Describe all pages" option (via the Describe dropdown, which only appears once a multi-page submission is detected) runs the vision model over every page instead of just the one you're viewing.
- **"Full View" shortcut** — jump from the inline viewer's compact Hoshi panel straight to the full-size Hoshi tab, continuing the same chat session.
- **Device-aware performance scaling** — the app now detects available CPU cores and memory at startup and scales image-fetch concurrency, decoded-image cache size, and background fetch parallelism accordingly, instead of assuming a high-end desktop.
- **Real vector icons for the sidebar navigation**, replacing the emoji-glyph placeholders.
- **Click-to-enlarge on Hoshi's attached-image thumbnail**, since the compact strip preview is too small to see details on its own.

### Improvements

- **Hoshi AI settings reorganized** into two focused cards: Active models and Model management. All install, refresh, and model-management actions live under Model management.
- **Unified recommended models list** — Ollama text/vision models and ONNX anime taggers now appear together as chips. Hovering shows each model's category, description, and estimated size.
- **Active model selectors are now dropdowns** — choose the Ollama text and vision models from the list of installed models instead of typing names.
- **Tagger settings are conditional** — threshold, max tags, and auto-tag only appear after the selected ONNX tagger model is installed.
- **Installed models list now includes everything** — Ollama and installed ONNX anime taggers appear together, with category-appropriate actions (use for chat/vision/tagger, uninstall).
- **AnimeTaggerService tag-list support** now covers CSV, plain-text, and JSON tag files.
- **Hoshi AI answers artwork-title questions from Pixiv's real metadata** instead of letting the vision model guess from pixels alone (it has no access to the actual title and would otherwise hallucinate).
- **Smoother streamed chat responses** — tokens are now coalesced into UI updates roughly every 40ms instead of one dispatch per token, fixing stutter during long responses like Describe.
- **Hoshi's image preprocessing (resize/encode for vision queries) now runs off the UI thread**, so attaching a large image no longer momentarily freezes the window.
- **"Similar Art" / "Similar Artists" no longer misfire into each other** (a shared keyword-matching bug), and results are now shuffled/rotated across a larger pool instead of always returning the same picks.
- **Local Favorites star badge** repositioned to sit consistently at the top-right of a card (was overlapping the selection checkbox in one layout, and floating in an empty gap in others) and now renders as a proper vector icon instead of a plain glyph.
- **Windows Aero Snap / Snap Layouts / maximize no longer clip window edges** (Avalonia 12.0.3 → 12.1.0 upgrade, plus removal of a redundant legacy resize handler that was fighting the OS's native resize frame).
- **"View Artwork" from a Hoshi chat result** now opens the artwork in a **new viewer tab** instead of silently doing nothing, and works for both the full Hoshi tab and the compact inline-viewer Hoshi panel.
- **Top "View Artwork" button in the Hoshi tab** now uses the selected session's own artwork ID, so switching to another session no longer opens the previous session's artwork.
- **"Open URL" quick action** added to Hoshi chat results — opens the artwork or artist Pixiv page directly in the default browser.
- **"Hoshi is thinking…" indicator** now also shows in the inline viewer's compact Hoshi panel, not just the full Hoshi tab, so it's clear a request is actually in progress.

### Fixes

- **Tagger install progress** now reports percent and status correctly during ONNX model downloads.
- **App no longer hangs indefinitely when closing the main window** — fixed a UI-thread deadlock in the session-save-on-exit path.
- **Local-favorite thumbnails permanently failing to load** for artworks added via a Hoshi "Open" action (wrong Pixiv API field mapping) — existing broken entries are now automatically repaired the next time Local Favorites loads.
- **Followed-artists list fetch no longer fires unbounded concurrent requests** at every startup; it's now throttled and scaled to the device's capability.
- **Hoshi session duplication** now preserves the linked Pixiv artwork ID, so the top "View Artwork" button stays available on copied sessions.
- **Hoshi chat quick actions** now read artwork/artist IDs and the Pixiv URL directly from the message DataContext and use Avalonia's cross-platform `Launcher`, fixing cases where the buttons appeared but did nothing.

---

## Pikura 1.8.2

Per-artwork / per-page preset apply controls and batch download scope fixes.

### New Features

- **Per-artwork page selection** — the Download Preset dialog now shows a page-selection text box for each selected artwork so you can choose which pages to download (e.g. `1,3,5-6`; default `all`).
- **Per-page preset apply** — new Apply buttons in the page picker let you apply the current preset only to selected pages, while a separate button applies it to the whole artwork.
- **Preset download scope** — choose whether to apply presets to the original image, the processed image, or both.

### Improvements

- **Batch preset lookup correctness** — downloads now use the original artwork index for per-artwork and per-page preset lookups, so presets apply to the correct artworks even after filtering or sorting.
- **Explicit artwork/page selection parsing** — the manual download-artworks text box now supports strings like `1-3:1-2,5; 5:1,3`.

### Fixes

- **Mismatched preset application on filtered galleries** — `batchArtworkIndex` now references the original index instead of the filtered index.

---

## Pikura 1.8.1

Inline viewer navigation and tab-state improvements across Gallery, Rankings, Discover, and Bookmarks.

### New Features
- **First and last artwork controls** — Added `<<` and `>>` buttons for immediate navigation to the beginning or end of the active tab's artwork list.
- **Jump to artwork** — Enter an artwork position and press Enter to navigate directly to it.

### Improvements
- **Section-specific navigation totals** — Each viewer tab now keeps the artwork list and total from the section that opened it instead of inheriting Gallery's initial batch count.
- **End-of-list navigation** — Advancing from the last loaded artwork now loads more and continues to the next artwork automatically when more results are available.

### Fixes
- **Tabs preserved when filtering** — Applying tag filters or starting tag searches no longer closes existing viewer tabs.
- **Incorrect repeated `96 / 96` counters** — Reused tabs now update their source correctly, preventing Gallery synchronization from replacing Rankings, Discover, or Bookmarks navigation lists.
- **Rankings counters** — Rankings viewer tabs display and jump by the actual rank number instead of the filtered-list index.
- **Backward navigation after reaching the end** — Previous-artwork navigation now consistently uses the active tab's retained list.

---

## Pikura 1.8.0

Live settings, artist profile image downloads, 429 error recovery, and UI polish.

### New Features

- **Download artist avatar and banner** — new checkbox in Settings → Advanced → Download Behavior. When enabled, Pikura fetches the artist's full profile and saves `avatar.jpg` and `banner.jpg` to their folder before downloading artworks.
- **HTTP 429 error panel in viewer** — when Pixiv rate-limits an image load in the inline gallery viewer, a clearly-worded error panel with a Retry button is shown instead of a blank tile. Click Retry to reload without navigating away.
- **Fullscreen viewer keyboard navigation shows full resolution** — pressing arrow keys to move between artworks now correctly loads the full-size original image for each artwork, with canvas state fully reset between navigations.

### Improvements

- **Live settings for running download jobs** — Safe Mode, delay between downloads, retry count, and retry delay are now read from current settings at each use point. Toggling Safe Mode or adjusting delays mid-job takes effect at the next artwork boundary without restarting.
- **R-18 toggle button spacing** — Content mode (Off / Show / Only) and Type filter (Both / R-18 / R-18G) buttons have improved padding and spacing for a cleaner look.
- **Overwrite behavior button spacing** — Skip / Overwrite / Backup buttons match the improved toggle style.
- **Blocklist Add button spacing** — The gap between the text input and Add button in the Tags, Titles, and Member IDs columns is improved.

### Changes

- **Blacklist renamed to Blocklist** — the section label in Settings is updated to "Blocklist (block download)".

---

## Pikura 1.7.5

Download queue reliability, retry performance, and History date grouping.

### New Features

- **History — date-grouped, collapsible sections** — Completed, Failed, and Cancelled tabs now group download jobs by date with collapsible headers ("Today", "Yesterday", weekday, or full date). Each header shows the job count and can be clicked to expand or collapse the group. The list is fully virtualized so it stays performant with 1000+ history entries.

### Improvements

- **Retry speed for failed / cancelled jobs** — Per-target completion status is now persisted to the database immediately when each artwork finishes, so retrying a partially-completed job skips already-downloaded artworks instantly without re-fetching metadata.
- **Download behavior is now universal** — The Overwrite Mode setting (Skip / Overwrite / Backup) is applied consistently across all download types: gallery, bookmarks, discover, rankings, and scheduled artist downloads.

### Fixes

- **App freeze during "Download All"** — Queue placeholder job creation is now fully async; the UI thread is never blocked during large bulk-queue operations.
- **Downloads wouldn't start while others were running** — Removed RelayCommand auto-disable that was preventing new downloads from being queued while concurrent slots were occupied.
- **Queued jobs stuck as paused** — Fixed atomic slot-claiming so queued jobs correctly start as soon as a running slot becomes free, respecting the Max Concurrent Jobs setting.
- **"Select an artist first" error on loaded gallery** — Preserves the selected artist during followed-artist list refresh so navigation state is never lost.
- **Stuck loading spinner** — IsLoading is now correctly reset on cache hits when switching artist galleries.
- **Orphaned queued jobs on startup** — Stale Queued/Pending jobs from previous sessions are cleaned up on launch.

---

## Pikura 1.7.4

Quality-of-life fixes for bookmark sorting, cross-section viewer persistence, and crash report noise.

### Features

- **Bookmark sorting** — New sort dropdown in the Bookmarks toolbar lets you order by Newest Bookmarked (default/API order), Newest Posted, Oldest Posted, Title A→Z, Title Z→A, or Most Pages. Sorting is client-side and applies instantly across all three tabs (Public, Private, Local Favorites).

### Fixes

- **Viewer image blank when switching sections** — Navigating back to Bookmarks or Rankings when a viewer tab was already open left the image panel empty. Each section now forces the inline viewer to reload the current card on attach.
- **Stale crash report dialog** — The crash report dialog no longer appears for crashes older than 5 minutes or for XamlLoadException (which is a build-artifact issue, not a real app crash). The flag is cleared automatically in both cases.

---

## Pikura 1.7.3

Quality-of-life improvements to job naming, followed-artists loading reliability, and download job lifecycle fixes.

### Fixes

- **Followed artists — overlapping page fetch to prevent gap drift** — Pagination now steps by half the page size (24 instead of 48) so consecutive windows overlap. Artists that shift between pages mid-fetch due to Pixiv's unstable offset ordering are no longer missed. The shared deduplication set discards any duplicates introduced by the overlap.
- **Pause triggers "Completed" notification** — Pausing a job no longer double-fires the JobCompleted event. The ContinueWith handler now owns the event exclusively, preventing paused jobs from appearing in the Completed list.
- **Job restart loop after pause** — Pausing a job no longer triggers TryStartNextPendingJobAsync, which was causing the just-paused job (or another queued job) to restart immediately.
- **Cancel does not route to Cancelled list** — Fixed a JobStatus enum ordering regression introduced in 1.7.2 where adding Queued without explicit integer values shifted all existing database status codes by one, causing Cancelled jobs to be read as Failed, Paused jobs to restart as Running, and so on. All enum values now have explicit fixed integers.
- **Orphaned Queued/Pending jobs restart on launch** — Startup cleanup now cancels all Queued and Pending jobs from prior sessions, not just Running ones.

### Improvements

- **Descriptive download job names** — Artist download jobs now show the artist name instead of "Download N Artists". Single artist shows name and ID; 2–3 artists listed by name; 4 or more shows the first three names and a count of the rest.
- **Gallery job names include artist** — Downloading selected or all artworks from an artist's gallery now prefixes the job name with the artist's name (e.g. "ArtistName: 42 artworks").
- **Scheduled job names include artist** — Scheduled artist downloads show the same descriptive artist label in the job name.

---

## Pikura 1.7.2

Followed-artists pagination reliability, download queue status accuracy, and job lifecycle fixes.

### Fixes

- **Followed artists — missing artists after partial load (#18)**
  Fixed: sequential pagination was stopping early whenever Pixiv returned a short page
  (e.g. 46 instead of 48). Pixiv returns inconsistently-sized pages throughout the list,
  not just at the end, so a short page is not a reliable end-of-list signal. Pagination
  now only stops on a truly empty page or when `offset >= Total`. Also switched from
  parallel to sequential per-branch fetching so that page drift caused by follows/unfollows
  mid-fetch no longer causes missed or duplicated artists.

- **Download queue — incorrect initial job status**
  Fixed: `CreateJobAsync` had a dead-code branch (`startImmediately ? Pending : Pending`)
  that always saved jobs as `Pending` regardless of slot availability. A new `Queued`
  status has been added to `JobStatus` to distinguish jobs waiting for a slot from jobs
  about to start. The UI now shows **⏳ Queued** for waiting jobs and **⏳ Starting…**
  for jobs about to execute, with correct cancellable/resumable button visibility.

- **Pause triggers "Completed" notification**
  Fixed: `PauseJobAsync` was firing `JobCompleted` immediately after cancelling the task
  token, while `ContinueWith` was also about to fire it once the task actually ended.
  The double-fire caused paused jobs to appear in the Completed list. `PauseJobAsync`
  no longer fires the event directly — `ContinueWith` now owns it exclusively.

- **Job restart loop after pause**
  Fixed: the `ContinueWith` Paused guard was calling `TryStartNextPendingJobAsync()`
  before returning, which could pick up the paused job (or another queued job) and
  restart it immediately. Pausing a job no longer triggers the next-job dequeue.

---

## Pikura 1.7.1

Polish and bug-fix release covering job queue UX, download history, folder resolution, and selection controls across all views. Closes issues #18, #19, and #20.

### New Features
- **Select All / Deselect All** — Added to the main toolbar in **Gallery**, **Bookmarks**, **Discover**, and **Rankings**. "☑ Select All" is always visible; "☒ Deselect All (n)" appears whenever items are selected. Consistent across all four views.
- **Queue number badge** — Active job cards in History now show a numbered badge (1, 2, 3…) indicating their position in the running queue, updating dynamically as jobs start, pause, and complete.
- **Open Folder — multi-artist jobs** — "Open Folder" for Discover / Rankings / Bookmarks "Download Selected" jobs (which span multiple artists) now opens the **DownloadRoot** directly instead of one artist's subfolder.

### Improvements
- **Job queue ordering** — Running jobs are always sorted to the top of the Active list; Paused jobs appear next; Pending jobs at the bottom.
- **Pause / Resume responsiveness** — Pause and Resume commands update the UI optimistically before the async call completes, eliminating the visible lag.
- **Progress preserved across pause/resume** — Resuming a paused job reuses the existing VM so the completed/total counter and progress bar are never reset to zero. A fresh progress subscription is registered on each resume so events continue flowing.
- **Initial progress on resume** — An immediate progress event is emitted at the start of job execution so the counter is correct from the very first frame after resume.
- **Open Folder — artist-level resolution** — For single-artist jobs with multi-page subfolders or R-18 subfolders (`ArtistName/R-18/12345_Title/`), Open Folder now correctly walks up to the artist-level folder (the direct child of DownloadRoot), not the artwork subfolder.
- **Open Folder — legacy jobs** — Jobs downloaded before `output_folder` was tracked now fall back to searching DownloadRoot for a folder whose name contains the artist's user ID.
- **Gallery toolbar** — "☒ Deselect All" button added to the top toolbar (next to "Download Selected"), matching the existing bottom-area button.

### Fixes
- **Folder named by member ID only (closes #19)** — "Download Selected" (image ID) jobs were creating folders named `(141556065)` with no artist name. Root cause: `ArtworkDetailBody` had wrong JSON property names (`"id"` / `"name"` instead of `"userId"` / `"userName"`), so the artist name always deserialized as null and `%artist%` resolved to empty in the folder template. Fixed.
- **Output folder not saved for image ID jobs (closes #20)** — `DownloadArtworkAsync` never captured the saved file's directory and never set `job.OutputFolder`, so "Open Folder" never appeared for image-ID download jobs. Fixed by adding an `onOutputFolder` callback matching the pattern used by artist downloads.
- **R-18 badge overlapping checkbox in Bookmarks** — The R-18 badge was positioned top-left, colliding with the selection checkbox. Moved to top-right in both Fixed card templates, matching Gallery layout.
- **Blue selection highlight clipped in Bookmarks** — Selection border overlay was inside a `ClipToBounds` container and not visible. Restructured so the overlay sits outside the clipped border.

---

## Pikura 1.7.0

Safer downloads with a new Safe Mode toggle, reliable Linux sign-in via Playwright Chromium, Hoshi sidebar fixes, and a fix for incomplete followed-artists lists.

### New Features
- **Safe Mode (anti-suspension)** — New toggle in **Settings → Downloads → Download Behavior**, default OFF. When enabled, downloads run sequentially with 2–4 s jittered gaps between artworks, 300–800 ms between pages of multi-page works, 2–4 s between targets in multi-target batch jobs, and honor `Retry-After` headers with exponential backoff on HTTP 429/503 — so Pikura no longer trips Pixiv's "unauthorized access attempts" suspensions on long batch jobs.
- **Copy artist ID anywhere** — Added "Copy artist ID" to the inline viewer image context menu and to every gallery card context menu (grid + list, natural + fixed). The artist name in the inline viewer is now also a single-click copy target. Mirrors to both the OS clipboard and the in-app artist-ID queue.
- **Linux sign-in via Playwright Chromium** — Linux login no longer relies on WPE WebKit / libwebkit2gtk and no longer asks users to paste their PHPSESSID. A real Chromium window opens, the user signs in normally, and the session cookie is captured automatically. First-run downloads ~150 MB of bundled Chromium with a clear progress dialog; subsequent sign-ins are instant. Works on Ubuntu, Debian, Fedora, Arch, openSUSE, and other distros.
- **Pinned Chromium cache** — Playwright's Chromium is installed into a Pikura-owned cache directory so future upgrades don't silently re-download the browser when the existing install is still usable.

### Improvements
- **Inline viewer status feedback** — Copy actions now show confirmation in the status bar (e.g. *"Copied artist ID 12345 (Username)"*).
- **Manual PHPSESSID dialog** — Reworded as an emergency fallback with an explanation of why it's appearing; shown only if the Playwright Chromium install fails. Most users will never see it again.
- **Windows — Control Panel** — Pikura now shows its icon in Programs and Features (previously a generic placeholder). The version is also displayed without the leading "v". Upgrading from an old "Pixora" install? The new installer detects and offers to remove the old entry automatically.
- **Windows installer — old Pixora cleanup** — The installer scans the registry for any existing "Pixora" uninstall entry and offers to silently remove it before installing Pikura, so users don't end up with two entries in Programs and Features.

### Fixes
- **Linux — Chromium permission denied on launch** — Fixed: .NET's single-file extractor unpacks embedded binaries without the executable bit set on Linux. Pikura now runs `chmod +x` on its embedded Playwright `node` binary before every login attempt, preventing the `EACCES (13)` error that caused the Chromium window to never open and fall back to the manual cookie dialog.
- **Linux — Chromium login dialog threading** — Fixed: the Chromium install progress dialog and fallback manual-cookie dialog were being constructed on a background thread, causing a cross-thread `InvalidOperationException`. All Avalonia window creation is now correctly marshalled to the UI thread.
- **Followed artists — incomplete list (#18)** — Fixed: only 48 of N followed artists loaded. Root causes: (a) required Pixiv URL params (`tag=`, `acceptingRequests=0`, `lang=`) were missing, causing Pixiv to ignore `offset` and return the first page repeatedly; (b) the loader stopped paginating when `total` came back as 0 — sequential discovery is now used as a fallback; (c) the deduplication `seen` set was seeded without holding `seenLock`, creating a race condition where parallel page tasks could insert duplicate artists; (d) `GalleryViewModel` was never receiving a real `ILogger` — it inherited `NullLogger.Instance` from `ViewModelBase`, silently swallowing all `[FollowedArtists]` log lines; fixed by injecting `ILogger<GalleryViewModel>` via DI. Verbose `[FollowedArtists]` diagnostic lines are now confirmed working.
- **Hoshi sidebar — prompt bubble disappearing mid-response** — Fixed: clicking *Describe*/*Tags*/*R-18* showed the prompt briefly, then it vanished as the AI streamed its answer. The `SessionsChanged` event used by the account-switch handler was being raised by routine session create/delete/duplicate operations and wiping the active chat. It now fires only on actual account swaps.
- **Hoshi sidebar — "I don't have the ability to see the image"** — Fixed: Pikura wiped the AI's image bytes on every card switch and only repopulated them after the full-resolution image finished downloading. The quick-action buttons now have an instant thumbnail-byte seed plus a belt-and-suspenders fallback that re-fetches from the cache before sending a vision query.
- **Inline viewer — chat bubble race** — Assistant streaming chunks now marshal cleanly to the UI thread via `Dispatcher.InvokeAsync` instead of racing with the user prompt add from a background thread.


