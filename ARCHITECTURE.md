# App and MCP architecture

## Goal

Let an AI client inspect and operate an attached astronomy camera live through MCP, while keeping Asterion Observatory's desktop UI available for direct human control.

## Components

```text
AI client
   │ MCP requests and image results
   ▼
Asterion MCP server
   │ typed internal commands/events
   ▼
Camera session service ─── ZWO ASI SDK ─── ASI120MC
   │
   └── preview, capture files, settings, status
          ▲
          └──────── WPF desktop UI
```

- **Camera session service:** the single owner of connection state, settings, preview frames, and capture lifecycle. It wraps the supported ZWO SDK behind an app-owned interface so the rest of the app is not coupled to vendor calls.
- **Desktop UI:** displays live preview and current camera state, supports manual operation, and reflects changes made by MCP calls.
- **MCP server:** exposes typed tools through local stdio and forwards them to the running desktop app over a same-user named pipe. The camera service itself is not exposed directly to the network.

## Initial MCP tools

- `list_cameras` — report detected camera names and stable identifiers.
- `get_camera_status` — report connection, preview, active capture, current exposure/gain, ROI, and frame/drop statistics when available.
- `get_preview_frame` — return one current preview image and relevant settings.
- `set_camera_settings` — set only requested exposure, gain, and ROI values after checking device ranges and current camera state.
- `capture_snapshot` — save one image to the configured session folder and return its path/metadata (and image content where the client supports it).
- `start_recording` — begin a recording with an explicit duration or frame limit, format, and target name.
- `stop_recording` — stop the current recording and report its result.
- `analyze_live_view` — return the current image as MCP image content with capture settings, dimensions, and timestamp. The connected AI client supplies vision interpretation.
- `capture_for_analysis` — capture one frame or a short bounded burst with requested in-range settings, then return image content and metadata for another AI inspection.

The AI vision cycle is intentionally client-driven: get preview image → interpret → optionally request a bounded higher-quality capture → interpret the returned image. This keeps image interpretation compatible with vision-capable MCP clients and avoids coupling the camera app to one model vendor. Catalog-based identification can add likely object names when the app has enough context (time/location and preferably mount coordinates); vision-only descriptions must be presented as uncertain.

## Broader observatory tools

After the first live-imaging milestone, the same app service can expose tools for:

- Exposure/gain suggestions based on the frame histogram and capture goal.
- Focus score and star-quality measurements, with optional focuser control when one is connected.
- Plate solving and object identification using a local solver/catalog; include evidence, coordinates, and confidence.
- Mount and filter-wheel status, then explicit user-enabled movement/filter changes.
- Capture sequences, dark/flat frames, live stacking, mosaics, and session summaries.
- Camera health, USB/frame rate, dropped-frame reporting, and safe reconnect actions.

Tools that move equipment or change observing configuration should report the proposed operation and honor app-level bounds. Slewing and other equipment movement should require an explicit user-enabled policy; they should not be inferred from an image description.

The API should return structured state and actionable errors. Long-running operations should expose progress and support cancellation rather than blocking the MCP request indefinitely.

## Control rules

- Bound exposure, gain, ROI, recording duration, and frame count using the connected camera's reported capabilities and app-level limits.
- Require an active, visible camera session before camera-changing commands are accepted.
- Never accept arbitrary Python, shell commands, DLL paths, or raw SDK calls as MCP input.
- Make recording start/stop explicit and report when recording is active in both the UI and tool result.
- Serialize commands through the camera session service; reject incompatible setting changes during capture or apply them only through a documented transition.
- Keep the human UI able to stop a capture and disconnect the camera immediately.
- Save captures under the configured session root; return paths and metadata rather than allowing arbitrary output paths in the first release.
- Keep an audit log of tool name, timestamp, requested values, applied values, result, and capture identifier. Do not log image data by default.

## Implementation sequence

1. Define `ICameraBackend` and `ICameraSession` abstractions and model connection, preview, setting, and capture states.
2. Implement the ZWO backend for enumerate/connect/disconnect, preview, exposure/gain/ROI, snapshot, and video recording.
3. Build the WPF UI against the session service and confirm manual operations work.
4. Connect the same session service to the stdio MCP endpoint; current tools expose discovery/status, preview images, bounded exposure/gain changes, and one-frame snapshots.
5. Add reliable frame delivery, bounded video recording, capture session folders, progress/cancellation, and logging.
6. Add the client-driven vision loop and capture-quality helpers.
7. Add astronomy-specific tools in stages: focus/metrics, plate solving, equipment, then sequences and stacking.

## Compatibility and clean-room notes

REA findings are summarized in `research/sharpcap-findings.md` and relate to the inspected SharpCap build. They inform workflow selection and terminology; Asterion uses the supported ZWO SDK and its own UI, state model, and implementation. Findings are static and do not establish SharpCap runtime behavior.
