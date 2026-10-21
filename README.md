# Asterion Observatory

A Windows astronomy imaging app for ZWO ASI cameras, with camera control exposed to AI clients through MCP. The desktop application owns the camera session; an AI client can inspect returned preview images using its own vision capability and request bounded exposure/gain changes or a follow-up still capture.

## Current status

The initial implementation includes a WPF desktop with continuous preview, live exposure/gain controls, saved snapshots, image quality metrics (brightness, clipping, focus heuristic, and bright-point candidates), and a local stdio MCP server bridged to the desktop through a same-user named pipe. MCP can propose a bounded exposure/gain adjustment without applying it; the vision-capable client inspects returned frames and can request a follow-up still. Video recording and astronomy catalog/plate-solving integrations remain future work. Snapshots are saved under Pictures/Asterion Observatory/Captures. Hardware operation has not yet been validated against the attached camera in this environment.

## Projects

- `src/Asterion.Desktop` — Windows desktop application
- `src/Asterion.Core` — camera session and pipe protocol
- `src/Asterion.Zwo` — direct P/Invoke adapter for the supported ZWO SDK
- `src/Asterion.McpServer` — MCP tools using stdio
- `research` — sanitized REA summary of SharpCap interfaces

## Build and run

Requires Windows and the .NET 9 SDK. Build with:

```powershell
dotnet build .\Asterion.sln
```

Launch `Asterion.Desktop`, then connect to the first detected ASI camera. Configure an MCP client to launch `Asterion.McpServer.exe` from its build output using stdio. Keep the desktop app open while the MCP server is in use.

The bundled x64 `ASICamera2.dll` is ZWO's MIT-licensed SDK runtime (see `vendor/asi/LICENSE.txt`). Camera hardware requires a Windows system and compatible ZWO driver. The current build targets x64 camera runtime use.

## Vision flow

Use `get_live_view` to return an image for the connected AI client's visual analysis. The AI can then call `capture_for_analysis` with optional in-range exposure or gain for one follow-up still. Temporary settings are restored afterward. Image-only object names are inherently uncertain; reliable sky identification needs observation time/location and ideally mount coordinates plus plate solving/catalog lookup.

## REA research

The public findings summary explains what was inspected and what remains unknown. Asterion uses ZWO's public SDK and independent code; it does not use SharpCap assemblies or copied implementation code.

### MCP client setup

Build the solution and use the generated `Asterion.McpServer.exe` as the MCP stdio command. For clients that accept JSON server configuration, use this shape and update the absolute executable path:

```json
{
  "mcpServers": {
    "asterion-observatory": {
      "command": "F:\\asterion-observatory\\src\\Asterion.McpServer\\bin\\Debug\\net9.0\\Asterion.McpServer.exe"
    }
  }
}
```
