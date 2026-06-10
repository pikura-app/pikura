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


