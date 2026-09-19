# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/lang/zh-CN/spec/v2.0.0.html).

## [0.9.15] - 2026-09-19

### Added
- **Multi-language support**: Chinese and English with runtime switching (no restart required)
- **Process icons**: Display executable icons in the Top Offenders tab
- **About dialog**: Show version information in tray menu and settings tab
- **Compile-time dictionary**: Replace JSON-based localization with C# dictionaries for zero-load-failure
- **JIT pre-warming**: Pre-compile ApplyLocalization method to eliminate first-switch lag

### Changed
- **Green portable deployment**: Switch from single-file to multi-file self-contained deployment (no system pollution)
- **Startup optimization**: Reduce startup time from 63s to <2s by using `Environment.GetLogicalDrives()` instead of `DriveInfo.IsReady`
- **Race condition fix**: Add lock to prevent concurrent ShowMainForm calls
- **UI improvements**: 
  - Increase row height to 36px (44px for clean tab) to accommodate descender letters (p, y, g, j, q)
  - Increase column header height to 32px
  - Add 4px vertical padding to cells
  - Use FlowLayoutPanel for clean tab buttons to prevent overlap
- **Performance**: Add SuspendLayout/ResumeLayout to ApplyLocalization to prevent UI lag during language switch

### Fixed
- **Data refresh crash**: Fix ArgumentOutOfRangeException in GetProcessIcon when icon extraction fails
- **Clean tab UI**: Fix status label being obscured by buttons
- **Settings tab**: Fix 21-second delay caused by slow drive enumeration
- **Language switch**: Fix lag on 3rd/4th language switch (not just first switch)
- **Row height**: Ensure all DataGridView rows have consistent height via EnsureRowHeights helper

### Removed
- **Single-file publish**: Removed PublishSingleFile to avoid .NET extraction overhead and system pollution
- **Debug logs**: Removed startup timing logs from production code

## [0.9.14] - 2026-09-18

### Added
- Compile-time C# dictionaries for localization (replacing embedded JSON resources)

### Fixed
- Fix key-literal display bug caused by EmbeddedResource naming issues

## [0.9.13] - 2026-09-17

### Changed
- Enable PerMonitorV2 DPI awareness for crisp text rendering
- Set global font to Segoe UI 8.25pt for better visual quality

## [0.9.12] - 2026-09-16

### Added
- Preserve scroll position during data refresh

## [0.9.11] - 2026-09-15

### Changed
- Auto-size buttons to prevent text clipping at different DPI settings
- Status bar height adapts to content

## [0.9.10] - 2026-09-14

### Fixed
- Fix search hint and input box overlap issue

## [0.9.9] - 2026-09-13

### Added
- Truncate long hash-named executables for better readability

## [0.9.8] - 2026-09-12

### Added
- Unified application icon (tray, window, taskbar)

### Fixed
- Fix blank taskbar button issue

## [0.9.7] - 2026-09-11

### Added
- Status bar at bottom showing precision mode and daily write volume
- Tab owner-draw with active tab highlighting
- Grid styling with alternating row colors and double buffering

### Changed
- Move status display from title bar to bottom status bar

## [0.9.6] - 2026-09-10

### Added
- Chart double buffering to eliminate flickering
- Grid double buffering for smooth scrolling

### Fixed
- Fix tray menu not appearing (menu was built but not bound)

## [0.9.5] - 2026-09-09

### Fixed
- Fix tab strip being covered by search bar (z-order issue)

## [0.9.4] - 2026-09-08

### Added
- Restore-to-screen logic for off-screen window recovery
- TopMost flash to force window to front

## [0.9.3] - 2026-09-07

### Added
- Window position persistence
- Off-screen window detection and auto-centering

## [0.9.2] - 2026-09-06

### Added
- Top Folders popup form

## [0.9.1] - 2026-09-05

### Changed
- Move SQLite/WMI queries to background thread to prevent UI freezing

## [0.9.0] - 2026-09-04

### Added
- Initial release
- ETW-based precise disk I/O attribution
- 8 comprehensive views (Offenders, Folders, Stream, Chart, Process Tree, Clean, Tools, Settings)
- Real-time monitoring with 3-second refresh
- System tray integration
- SQLite storage with WAL mode
- Auto-archiving (>30 days)
- CSV export
- Surge detection with balloon notifications
- Single instance with NamedPipe wake-up
- Administrator privileges for ETW access

[0.9.15]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.15
[0.9.14]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.14
[0.9.13]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.13
[0.9.12]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.12
[0.9.11]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.11
[0.9.10]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.10
[0.9.9]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.9
[0.9.8]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.8
[0.9.7]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.7
[0.9.6]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.6
[0.9.5]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.5
[0.9.4]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.4
[0.9.3]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.3
[0.9.2]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.2
[0.9.1]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.1
[0.9.0]: https://github.com/yourusername/disk-eye/releases/tag/v0.9.0
