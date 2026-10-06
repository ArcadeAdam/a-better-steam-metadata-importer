# A Better Steam Metadata Importer

A LaunchBox plugin by **ArcadeAdam** that fills missing metadata and adds official Steam artwork and trailers to selected games. Built for LaunchBox's .NET 9 plugin API; tested against **LaunchBox 13.27**. It does not create duplicate games or change launch commands or LaunchBox database links.

**[Download the latest release](https://github.com/ArcadeAdam/a-better-steam-metadata-importer/releases/latest)** · **[Report an issue](https://github.com/ArcadeAdam/a-better-steam-metadata-importer/issues)**

Select one game or a queue, preview its Steam match, then import only what is missing. The plugin supports Steam artwork, screenshots, and full trailers, including modern DASH/HLS video. Saved media limits default to two files per type per game and count existing media. A bottom progress bar tracks the queue and shows **Done!** when processing finishes. No Steam login or API key is needed.

![Steam import queue showing per-type media limits, metadata preview, and progress](https://raw.githubusercontent.com/ArcadeAdam/a-better-steam-metadata-importer/main/docs/images/importer-preview.png)

*Previewing a queue of games with media limits and progress tracking. Screenshot captured before the v1.1.0 name change.*

This is an independent community plugin for LaunchBox. It is not affiliated with Valve or LaunchBox, and it is a separate implementation from srxz's Steam Scraper.

## Use

1. Right-click one or more games and choose **A Better Steam Metadata Importer…**.
2. Check the detected Steam app IDs. Steam launch URLs, `-applaunch` commands and local `.url` shortcuts are recognized. Enter an app ID or Steam store URL if necessary. Title matching is never guessed automatically.
3. Click **Preview Steam data**. Check the Steam title and proposed metadata. Uncheck unavailable games before importing.
4. Choose **Metadata**, **Artwork**, **Trailers / Video**, and/or **Use primary trailer for missing Theme Video**. Under **Media limits per game**, set a maximum for each media type. Every type defaults to **2**, counting existing files. Use **0** to skip a type or leave its box **blank** for unlimited. Limits are saved when you preview or import.
5. Click **Import missing data**. The bottom progress bar tracks processed games in the selected queue and names the current game. **Done!** remains visible when the queue finishes; check the log for individual media results. Cancellation stops the current operation and remaining queue, retaining the partial progress count. Completed media are kept and reused next time.

Existing nonempty metadata is always preserved. Existing explicit or automatically discovered videos are preserved. Artwork is added using LaunchBox's naming and configured media folders; no existing media file is overwritten. Exact image duplicates and previously imported assets are reused. Press F5 in LaunchBox if it has cached an older image.

Each game has a separate limit for **Box - Front**, **Steam Poster**, **Clear Logo**, **Banner**, **Steam Banner**, **Fanart - Background**, **Screenshot - Gameplay**, and **Video**. Existing files from LaunchBox and previous imports count toward their type's limit; shared paths count once. Failed downloads do not use a slot. For example, a game with one screenshot and a screenshot limit of 2 can receive one more screenshot. If it already has five, all five stay and no more are added. **Reset all to 2** restores the default values; preview or import to save them.

Video and Theme Video share the **Video** limit because they can refer to the same downloaded trailer. Different existing Video and Theme Video files each count. A zero Video limit also skips filling those assignments. The Video limit replaces the previous **All trailers** checkbox: choose 1 for a single trailer, or blank for every available trailer. Limits are saved separately for each LaunchBox installation in `Data\media-limits.json` and apply to later queues.

## Media coverage

- Library cover and Steam poster, library hero/background and transparent logo.
- Header/banner, main/small store capsules, store hero capsule, store page background and app icon when Steam exposes them.
- Public store screenshots at their largest discovered resolution, up to the selected screenshot limit.
- Full store trailers, including older direct MP4/WebM and current H.264 DASH/HLS streams, up to the selected Video limit. Adaptive trailers are assembled with audio into MP4 at up to 1080p. Blank limits allow every available screenshot or trailer.

The library cover is mapped to both Box - Front and Steam Poster. Store capsule variants and app icons are stored as Steam Banner/Steam Poster variants. The plugin does not synthesize 3D boxes, scrape community uploads or download every language/thumbnail variant. Some games do not provide every asset; failures are reported independently, and other media continue.

Steam trailers can fill both Video and Theme Video. **Theme Video points to the same primary trailer file**; Steam does not supply separate Big Box theme productions.

Metadata includes description, developer, publisher, genres, play modes, franchise, source and confirmed release date/status. Player count is filled only when the store text states it explicitly. A numeric Steam age threshold is not converted into an invented ESRB rating. Store lookups currently use English/US public data; region-restricted or delisted entries may be unavailable.

## Install / remove

Close LaunchBox. Extract **both top-level folders** from the release ZIP directly into the folder containing `LaunchBox.exe`, merging the folders when prompted. Do not extract the whole ZIP inside `Plugins`. Restart that exact LaunchBox build.

Download `ABetterSteamMetadataImporter-v<version>.zip` for installation. The `-source.zip` asset and GitHub's automatic source-code archives are for developers and do not contain the compiled plugin or bundled FFprobe.

```text
LaunchBox/
  Plugins/SteamMetadataImporter/SteamMetadataImporter.dll
  Plugins/SteamMetadataImporter/README.md
  ThirdParty/FFMPEG/ffprobe.exe
  ThirdParty/FFMPEG/FFprobe-Notices/
```

The ZIP supplies FFprobe and its notices/source material. LaunchBox's existing `ThirdParty\FFMPEG\ffmpeg.exe` is also required and is not replaced. `ffprobe.exe` belongs directly in `FFMPEG`, not inside a `requiredfiles` subfolder. Do not copy the LaunchBox API DLL into the plugin directory.

**Upgrading from Steam Metadata Importer 1.0.x:** install into the same LaunchBox root and replace the existing DLL. The folder and DLL deliberately retain their original `SteamMetadataImporter` names so the update uses the same saved limits, import history, and backups. Keep the `Data` subfolder. Do not install a second copy under a different plugin folder.

The plugin uses `ThirdParty\FFMPEG\ffmpeg.exe` and `ffprobe.exe`. Some LaunchBox installations omit FFprobe; version 1.0.1 includes it to cover that case. It requires no Steam login, API key, yt-dlp, Python or standalone .NET installation for normal use. If either video tool is unavailable, artwork and metadata still work and the video error is shown.

To disable, close LaunchBox and move the plugin folder outside `Plugins`. Keep its `Data` subfolder if you want to retain import history. Disabling the plugin does not remove imported media or metadata.

## Backups and logs

`Plugins\SteamMetadataImporter\Data` contains:

- `Backups`: values captured immediately before each game import, including original launch commands and media assignments.
- `Manifests`: imported asset keys, source URLs, file paths and SHA256 checksums for repeat-run deduplication.
- `Logs`: per-game summaries, unavailable assets and errors.
- `Staging`: temporary downloads, normally removed after each operation.
- `media-limits.json`: saved per-type media limits for this LaunchBox installation.

The plugin changes selected in-memory `IGame` records and saves through LaunchBox's API. It never edits platform XML directly. Media writes use temporary files and integrity checks before installation. Do not run multiple importer instances against the same library at once.

## Build and verify

Source: `src\SteamMetadataImporter`. Run `build.ps1` with a .NET 9 Windows SDK and the path to a matching LaunchBox build:

```powershell
.\build.ps1 -LaunchBoxRoot 'C:\LaunchBox'
```

Install a .NET 9 Windows SDK on `PATH`, or pass `-DotNetPath 'C:\path\to\dotnet.exe'`. You can also set `LAUNCHBOX_ROOT` instead of supplying `-LaunchBoxRoot`. The build uses the plugin API DLL from your own LaunchBox installation; that DLL is not included in this repository. `build.ps1` builds Release and runs the offline test suite by default.

To reproduce the two-folder release ZIP, use PowerShell 7 and supply a directory containing the bundled `ffprobe.exe` and its complete `FFprobe-Notices` directory. The existing release ZIP's `ThirdParty\FFMPEG` directory is a suitable source. Retain the corresponding sources and notices when redistributing the executable.

```powershell
.\package.ps1 -FFprobeDirectory 'C:\release-inputs\ThirdParty\FFMPEG' -OutputDirectory 'C:\release-output'
```

The input directory must not contain `ffmpeg.exe` or LaunchBox API DLLs. Keep bundled runtime files out of the Git repository; the release assets carry FFprobe and its corresponding source material. See [third-party notices](THIRD-PARTY-NOTICES.md) for details.

No NuGet packages are required. The test runner covers ID/shortcut resolution, preservation of existing metadata, edits between preview and import, current and legacy Steam responses, missing entries, source fallback, image validation, cancellation, existing-media detection, media-limit settings, and existing-file counting across LaunchBox and import history. Optional live tests exercise Steam discovery and actual trailer download. No LaunchBox API binaries are redistributed. The plugin's MIT license applies to the plugin; the separately invoked FFprobe executable retains its own license and source notices.

Version 1.0.0 validation on October 5, 2026: 24 offline checks passed. A real LaunchBox import of Railbreak DX (4685520) filled Video and Theme Video, added 8 media files, reused 12, and reported no unavailable assets. The 104.6-second trailer was verified as 1920×1080 H.264 with AAC audio and played in LaunchBox. All existing metadata and the other eight Steam games were preserved. A repeat import reused all 20 assets, added nothing, and left the platform XML byte-for-byte unchanged. Separate live downloads verified both DASH and HLS.

Version 1.0.1 adds the two-folder installation package and FFprobe. It also lets LaunchBox's menu callback finish before opening the modal importer, releasing the host's busy cursor while preserving the dialog's ownership and single-instance behavior.

Version 1.0.2 adds a persistent bottom progress bar and queue status. Finished import queues show **Done!** and the processed-game count. Preview, cancelled operations, and interrupted operations have separate statuses. Progress counts games processed, including failed attempts; it is not a byte-download percentage.

Version 1.0.3 adds saved per-game limits for each media type, defaulting to 2. Existing files count toward limits, including imports made by earlier versions. Lowering a limit never deletes media. Repeat runs reuse prior assets and respect remaining capacity; failed downloads leave room for other available sources.

Version 1.1.0 adopts the public name **A Better Steam Metadata Importer**, updates the interface and download names, and makes the build instructions portable. The existing install and data paths remain compatible with 1.0.x. See [the changelog](CHANGELOG.md) for release history.

## Research and compatibility

The existing [Steam Scraper by srxz](https://forums.launchbox-app.com/files/file/1440-steam-scraper/) is a separate project. This plugin is a new implementation and does not contain that project's source code.

Relevant primary references: [LaunchBox IGame API](https://pluginapi.launchbox-app.com/html/b33d2055-e2be-3f42-12c6-adbc5668f454.htm), [LaunchBox media-path guidance](https://forums.launchbox-app.com/topic/89263-launchbox-file-paths/), [Steam graphical assets](https://partner.steamgames.com/doc/store/assets), [Steam trailers](https://partner.steamgames.com/doc/store/trailer).

Steam's public store endpoints and response formats can change. The plugin reports unavailable data and retains existing library values rather than guessing a match or deleting metadata.
