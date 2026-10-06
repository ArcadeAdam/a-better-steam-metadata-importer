# Changelog

## 1.1.0 — A Better Steam Metadata Importer

- Adopted the public name A Better Steam Metadata Importer in the window, context menu, assembly product metadata, documentation, and release filenames.
- Preserved the SteamMetadataImporter DLL, install folder, settings, and import history for compatible upgrades from 1.0.x.
- Removed machine-specific build paths; builds accept a LaunchBox installation and discover the .NET SDK on PATH.
- Added public installation, upgrade, and release-packaging instructions.

## 1.0.3

- Added saved limits for each media type, defaulting to two per game and counting existing media.
- Added zero to skip a type and blank for unlimited downloads.
- Kept older and user-edited imported files in the inventory so repeat runs respect the limit before LaunchBox refreshes its image cache.
- Shared the Video limit between Video and Theme Video assignments; shared files count once.

## 1.0.2

- Added bottom queue progress, current-game status, and a persistent Done! message.
- Distinguished completed, previewed, cancelled, and failed queues.

## 1.0.1

- Added the two-folder installation ZIP with FFprobe, licenses, and corresponding sources.
- Allowed LaunchBox's menu callback to return before opening the plugin dialog, releasing the host's busy cursor.

## 1.0.0

- Initial selected-game Steam metadata and media importer.
- Added preview, missing-field preservation, artwork, full MP4/WebM/DASH/HLS trailers, and a shared primary trailer for missing Video/Theme Video.
- Added per-game backups, import manifests, integrity checks, source fallback, and repeat-import deduplication.
