<div align="center">
  <img src="src/Pikura.Avalonia/Assets/pikura-logo.png" width="96" height="96" alt="Pikura logo"/>
  <h1>Pikura · ピクラ</h1>
  <p>A modern desktop manager for Pixiv artwork — browse, download, schedule, and organise your collection.</p>

  [![Release](https://img.shields.io/github/v/release/pikura-app/pikura?style=flat-square)](https://github.com/pikura-app/pikura/releases/latest)
  [![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
  [![.NET](https://img.shields.io/badge/.NET-10-purple?style=flat-square)](https://dotnet.microsoft.com)
  [![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey?style=flat-square)](#download)

  <br/>

  [![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/mrplusultra)
  [![Support me on Patreon](https://img.shields.io/badge/Patreon-Support-FF424D?style=flat-square&logo=patreon&logoColor=white)](https://www.patreon.com/mrplusultra/join)

  <br/>
</div>

---

## Screenshots

![Pikura Gallery screenshot](.github/screenshots/gallery.png)
---

## Features

- **Gallery browser** — browse your Pixiv feed and followed artists with grid or list view; expand any artwork inline via the side panel with metadata, tags, and quick actions (Prev/Next, Artist, Popup, Copy, Favorite, Hoshi)
- **Bookmarks** — browse and manage your Pixiv bookmarks (public and private) in one place
- **Rankings** — browse daily/weekly/AI/male/female Pixiv rankings
- **Discover** — explore recommended and trending artwork from Pixiv
- **History** — view your previously downloaded artworks with timestamps
- **Analytics** — charts and stats for your download history and collection
- **Image editor** — non-destructive adjustments (brightness, contrast, saturation, hue, temperature, tint, highlights, shadows, blur, sharpness), color overlay, opacity slider, and live GPU-accelerated preview; export to PNG with alpha preserved
- **Batch download** — download entire artist galleries, bookmark collections, or search results; paginates through Pixiv's API to honour your Max Results setting
- **Schedules** — set recurring auto-downloads with per-schedule content filters and tag rules
- **Content filters** — skip AI-generated, Manga, Ugoira, R-18, R-18G content globally or per schedule
- **Tag filters** — include/exclude artworks by tag
- **FANBOX support** — download FANBOX posts alongside Pixiv artwork
- **Multi-account** — switch between multiple Pixiv accounts from the sidebar
- **Per-account settings** — override download root, filename templates, and filters per account
- **Hoshi AI** — ask questions about artworks using a local Ollama vision model (text + vision)
- **Auto-updater** — checks for updates on startup, downloads silently in the background, and installs with one click
- **System tray** — runs quietly in the background with tray icon and notifications
- **Themes** — light, dark, and system-default mode

## Download

| Platform | Installer | Portable |
|----------|-----------|----------|
| **Windows** | [Pikura-Setup.exe](https://github.com/pikura-app/pikura/releases/latest) | [.zip](https://github.com/pikura-app/pikura/releases/latest) |
| **macOS** | [Pikura.dmg](https://github.com/pikura-app/pikura/releases/latest) | — |
| **Linux** | [.AppImage](https://github.com/pikura-app/pikura/releases/latest) | [.tar.gz](https://github.com/pikura-app/pikura/releases/latest) |

> **macOS note:** Pikura is not notarised. On first launch right-click → Open to bypass Gatekeeper.

## What's New in v2.1.0

- **Collections** — full support for Pixiv's collection-curation feature: browse Featured and All collections, open details, download selected or entire collections, bookmark collections, and post/read collection comments.
- **Collage** — open any selection of artworks as a single unique collage tab, add or remove individual images, and download the whole collage.
- **Pixiv-connected actions** — likes, bookmarks, follows/unfollows, and comments now operate on your real Pixiv account; public/private bookmark state is supported and kept separate from local favorites.
- **Chrome-style tab reordering** — drag inline viewer tabs left or right and watch the strip slide around the tab in real time.
- **View in new tabs** — open selected artworks in their own inline viewer tabs from Gallery, Discover, Rankings, Pixivision, Bookmarks, Search, and Collections.
- **Bookmarks improvements** — Bookmarks now includes every artwork you've liked on Pixiv (even ones that aren't bookmarked) alongside your public/private bookmarks and bookmarked collections.
- **Search improvements** — a History dropdown lets you jump back to a previous search (with the filters used at the time), and Popular Tags are shown as clickable chips so you can search by tag with one click.
- **Smoother sync** — likes, bookmarks, follows, and view counts update immediately across the gallery, Discover, Rankings, Search, Pixivision, and the inline viewer.
- **Refreshed themes** — light and dark modes have been cleaned up with better contrast, rounded button hover highlights, visible caption-button hover states, and a working system-theme toggle.

## Requirements

- A Pixiv account
- Windows 10+, macOS 12+, or a modern Linux distro
- No separate .NET installation needed — the download is self-contained

## Building from source

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download)

```bash
git clone https://github.com/pikura-app/pikura.git
cd pikura
dotnet build src/Pikura.Avalonia/Pikura.Avalonia.csproj
dotnet run --project src/Pikura.Avalonia/Pikura.Avalonia.csproj
```

## Project structure

```
src/
  Pikura.Core/        # Business logic, API clients, download services
  Pikura.Avalonia/    # Avalonia UI — views, viewmodels, assets
tools/
  MakeIco/            # CLI tool to generate pikura.ico from SVG
  installer/          # Inno Setup script for Windows installer
  convert-icon.ps1    # SVG → ICO helper script
.github/workflows/
  release.yml         # CI — builds & publishes all platforms on git tag
```

## Releasing

```bash
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions builds Windows (installer + portable), macOS (DMG), and Linux (AppImage + tar.gz) automatically and attaches them to the release.

## License

MIT — see [LICENSE](LICENSE). Free for personal, non-commercial use.
