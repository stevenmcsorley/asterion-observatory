# Asterion Observatory

A Windows astronomy imaging app for ZWO ASI cameras, with camera control exposed to AI clients through MCP. The desktop application owns the camera session; an AI client can inspect returned preview images and request bounded camera setting changes or a follow-up still capture.

## Current status

The first packaged preview includes a WPF desktop with continuous preview, live exposure/gain controls, saved PNG snapshots, and sampled image heuristics (brightness, clipping, focus response, and bright-point candidates). The local stdio MCP server returns images and metrics, can propose bounded exposure/gain settings without applying them, and can capture a still with temporary settings. Video recording, plate solving, and astronomy catalog integrations remain future work. Hardware operation has not yet been validated against the user's physical camera.

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
