# Asterion Observatory

A Windows astronomy imaging app for ZWO ASI cameras, with camera control exposed to AI clients through MCP. The desktop application owns the camera session; an AI client can inspect returned preview images and request bounded camera setting changes or a follow-up still capture.

## Current status

The preview includes a WPF desktop with continuous live JPEG preview and adjustable preview quality, logarithmic exposure, live gain, camera-reported binning, RGB24/RAW8 selection, auto exposure, current-frame screenshots, full PNG still capture, and SER or MJPEG AVI video recording. A borderless fullscreen live viewer is available from the video toolbar and exits with Escape. SER preserves raw sensor samples and per-frame timestamps for stacking and lucky imaging; AVI is easier to play and share, at the cost of JPEG compression and larger files than SER. RAW8, binning, and lower JPEG quality reduce preview data volume; shorter exposure also raises the achievable frame rate. The desktop reports measured preview FPS and skipped UI refresh ticks. A connected ZWO ASI120MC has been verified at 640×480, RAW8, 2× binning, and 33 ms exposure (about 14 recorded frames/sec); a 260-frame Bayer SER file was opened successfully by ffprobe. Plate solving and astronomy catalog integrations remain future work.

## Projects

- `src/Asterion.Desktop` — Windows desktop application
- `src/Asterion.Core` — camera session and pipe protocol
- `src/Asterion.Zwo` — direct P/Invoke adapter for the ZWO SDK
- `src/Asterion.McpServer` — MCP tools using stdio
- `research` — sanitized REA summary of SharpCap interfaces

## Build from source

Requires Windows and the .NET 10 SDK. Build with:

```powershell
dotnet build .\Asterion.sln --configuration Release
```

## Install the Windows preview

Download the self-contained Windows x64 ZIP from GitHub Releases and extract it. No separate .NET runtime installation is required. Start `Asterion.Desktop/Asterion.Desktop.exe`, connect a supported ZWO ASI camera, and keep the app open while using its MCP server. MCP clients can use the included `mcp-config.example.json` after updating its path to the extracted `Asterion.McpServer/Asterion.McpServer.exe`.

Snapshots are stored under `Pictures/Asterion Observatory/Captures`. The bundled x64 `ASICamera2.dll` is ZWO's MIT-licensed SDK runtime; its license and notice are under `vendor/asi/`.

## Vision flow

Use MCP `get_live_view` to return an image for the connected AI client's visual analysis. MCP `suggest_camera_settings` returns a bounded exposure/gain proposal without applying it. The AI client can then call `capture_for_analysis` with optional in-range temporary settings; those settings are restored afterward. Image-only object names are uncertain; reliable sky identification needs observation time/location and ideally mount coordinates plus plate solving/catalog lookup.

## REA research

The public findings summary explains what was inspected and what remains unknown. Asterion uses ZWO's public SDK and independent code; it does not use SharpCap assemblies or copied implementation code.
