# Changelog

Every release needs an entry here before it's tagged — the release build copies the matching section
into the GitHub release notes (and fails if there isn't one).

## [1.0.4] - 2026-10-07

### Changed
- Updates no longer restart WolfSpeak by themselves. When a new version is downloaded, an **Update** button
  appears at the top — click it when it suits you. Clicking during a call asks you to hang up first.
- If you quit WolfSpeak with an update waiting, it installs quietly so the next start is up to date.
- **Check for updates** now just downloads and shows the Update button instead of restarting right away.

## [1.0.3] - 2026-10-07

### Added
- Version number next to the WolfSpeak title and in Settings.
- **Check for updates** button in Settings — installs right away (never during a call).

### Changed
- Source code moved from `src/WolfSpeak/` to `src/`.

## [1.0.2] - 2026-10-07

- Same app as 1.0.1, published to test automatic updates.

## [1.0.1] - 2026-10-07

First release.

### Added
- Direct LAN voice calls: raw 48 kHz audio, ~40–60 ms mouth-to-ear, no servers.
- One-click installer with automatic updates from GitHub Releases.
- Encrypted calls (signed key exchange + AES-256-GCM) with a safety code to compare, and a warning
  when a friend's security key changes.
- Calls only connect when both sides run the same version.
- Tray mode, push-to-talk, global mute/deafen hotkeys, device hot-plug.
