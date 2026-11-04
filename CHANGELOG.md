# Changelog

All notable changes to Pikura are documented in this file.

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
