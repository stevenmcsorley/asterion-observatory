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

## Current MCP tools

- `list_cameras` — report detected camera names and stable identifiers.
- `get_camera_status` — report connection and current exposure/gain state.
- `get_camera_capabilities` — report exposure and gain ranges for safe settings.
- `get_live_view` — return a preview image with sampled brightness, highlight, focus, and bright-point heuristics. The AI client supplies image interpretation.
- `set_camera_settings` — set exposure and gain after checking device ranges.
- `capture_for_analysis` — capture one still using optional in-range temporary settings, save it under Pictures/Asterion Observatory/Captures, return image content, and restore original settings.
- `suggest_camera_settings` — propose a bounded exposure/gain adjustment from frame brightness without applying it.

The vision loop remains client-driven: inspect a returned frame, optionally request a suggested setting or bounded follow-up still, then inspect again. Catalog-based identification needs observing time/location and ideally mount coordinates plus plate solving; vision-only identification remains uncertain.
## Broader observatory tools

Future milestones can add tools for:

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

1. Add reliable video recording, capture session management, progress/cancellation, and logging.
2. Improve camera selection, exposure/gain feedback, and frame-quality workflows.
3. Add astronomy-specific tools in stages: focus/metrics, plate solving, equipment, then sequences and stacking.
## Compatibility and clean-room notes

REA findings are summarized in `research/sharpcap-findings.md` and relate to the inspected SharpCap build. They inform workflow selection and terminology; Asterion uses the supported ZWO SDK and its own UI, state model, and implementation. Findings are static and do not establish SharpCap runtime behavior.

