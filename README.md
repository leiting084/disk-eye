# DiskEye - Disk I/O Monitor for Windows

[中文文档](README.zh-CN.md)

Real-time disk I/O monitoring tool for Windows with precise process-level attribution using ETW (Event Tracing for Windows).

## Features

- **Precise Attribution**: Uses ETW Kernel-File Provider to accurately track which process wrote which file
- **8 Comprehensive Views**:
  - Top Offenders - Processes ranked by write volume
  - Hot Folders - Directories with most write activity
  - Event Stream - Real-time file operation events
  - Trend Chart - 24-hour write activity visualization
  - Process Tree - Parent-child process relationships
  - Junk Cleaner - Safe cleanup of temp files and browser caches
  - Tools - Data export and archiving
  - Settings - Configuration and preferences
- **Real-time Monitoring**: 3-second refresh interval with scroll position preservation
- **Multi-language Support**: Chinese and English with runtime switching (no restart required)
- **System Tray Integration**: Minimize to tray, real-time byte counter, quick access menu
- **Green & Portable**: No installation required, single folder deployment, no system pollution
- **Smart Alerts**: Surge detection with balloon notifications for unusual write activity
- **Data Management**: SQLite storage with WAL mode, auto-archiving (>30 days), CSV export
- **Performance Optimized**: Fast startup (<2s), JIT pre-warming, efficient drive enumeration

<!-- TODO(screenshots): docs/screenshots/ 下暂无图片，恢复本节前需先补齐以下三个文件：
     docs/screenshots/main-window.png
     docs/screenshots/settings.png
     docs/screenshots/tray-menu.png
## Screenshots

![Main Window](docs/screenshots/main-window.png)
![Settings](docs/screenshots/settings.png)
![Tray Menu](docs/screenshots/tray-menu.png)
-->

## Download

Download the latest release from the [Releases](../../releases) page.

**Portable Version**: Extract the zip file to any folder and run `DiskEye.exe`. No installation required.

## Requirements

- Windows 10/11 (64-bit)
- Administrator privileges (required for ETW)
- .NET 8.0 Runtime (included in self-contained build)

## Quick Start

1. Download and extract the portable version
2. Run `DiskEye.exe` as Administrator
3. Select drives to monitor in Settings tab
4. View real-time disk activity across multiple tabs

## Building from Source

### Prerequisites

- .NET 8.0 SDK
- Windows 10/11 (64-bit)

### Build Commands

```bash
# Debug build
dotnet build src/DiskEye/DiskEye.csproj

# Release build
dotnet publish src/DiskEye/DiskEye.csproj -c Release -o publish
```

The published output will be in the `publish` folder.

## Architecture

### Core Components

- **EtwProcessResolver**: Manages ETW session and process tracking
- **EventAggregator**: Aggregates file events by process and folder
- **AttributionEngine**: Attributes file writes to processes using ETW data
- **EventStore**: SQLite-based storage with WAL mode for concurrent access
- **MonitorService**: Orchestrates all monitoring components

### Data Flow

```
ETW Events → EventAggregator → AttributionEngine → EventStore → UI Refresh
                                    ↓
                              Process Resolution
```

See [docs/architecture.md](docs/architecture.md) (Chinese) for detailed technical documentation.

## Configuration

Configuration is stored in `config.json` next to the executable:

```json
{
  "MonitoredDrives": ["C", "D"],
  "AutoStart": false,
  "Language": "zh",
  "IncludePrefixes": [],
  "IgnorePrefixes": [],
  "ProcessSurgeThresholdBytes": 104857600,
  "FolderSurgeThresholdBytes": 524288000,
  "SurgeWindowSeconds": 60,
  "SurgeCooldownMinutes": 30
}
```

## Privacy

- All data is stored locally in SQLite database
- No telemetry or data collection
- No network connections (except for GitHub updates if enabled)
- Open source - you can audit the code

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

### Development Setup

1. Clone the repository
2. Open `src/DiskEye/DiskEye.csproj` in Visual Studio or VS Code
3. Build and run as Administrator

### Code Style

- Follow C# coding conventions
- Use meaningful variable names
- Add XML documentation for public APIs
- Write unit tests for new features

## Roadmap

- [ ] Dark mode support
- [ ] Historical data analysis
- [ ] Custom alert rules
- [ ] Export to multiple formats (Excel, JSON)
- [ ] Plugin system for custom analyzers
- [ ] Performance profiling integration

## Known Issues

- ETW requires Administrator privileges
- Some system processes may not have complete path information
- High write activity may cause temporary UI lag (mitigated by background refresh)

## Troubleshooting

### "ETW child process unavailable"

This message indicates the ETW monitoring subprocess encountered an error. The application will automatically fall back to heuristic mode.

**Solutions**:
- Ensure you're running as Administrator
- Check Windows Event Viewer for ETW-related errors
- Restart the application

### Slow startup

First launch may take 2-3 seconds due to JIT compilation. Subsequent launches are faster.

### Missing process icons

Some processes may not have accessible icons. A default icon will be displayed instead.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/) - ETW event processing
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) - SQLite database access
- Windows ETW Kernel-File Provider for precise I/O attribution

## Support

- **Issues**: [GitHub Issues](../../issues)
- **Discussions**: [GitHub Discussions](../../discussions)

---

**Note**: This tool is designed for monitoring and diagnostic purposes. Use responsibly and respect system performance.
