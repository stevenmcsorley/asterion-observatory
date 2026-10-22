# Asterion Observatory Preview 5

This preview adds camera throughput controls and capture tools to Asterion Observatory.

## Included

- Self-contained Windows x64 desktop application and MCP stdio server
- ZWO ASI camera discovery, continuous preview, exposure/gain, camera-reported binning, and RGB24/RAW8 selection
- Save the current preview frame as PNG, take a fresh still PNG, or record a stoppable SER video sequence
- A one-click auto-exposure adjustment based on live image brightness and clipping
- Live image heuristics for mean brightness, highlight clipping, focus response, and bright-point candidates
- MCP tools for live image return, bounded settings, saved analysis captures, and non-applying exposure/gain suggestions
- Bundled x64 ZWO `ASICamera2.dll`; third-party license is included in `vendor/asi/LICENSE.txt`

## Install and start

1. Download and extract `Asterion-Observatory-v0.1.0-preview.5-win-x64.zip`.
2. Run `Asterion.Desktop/Asterion.Desktop.exe`.
3. Connect a supported ZWO ASI camera with its Windows driver installed.
4. Add `Asterion.McpServer/Asterion.McpServer.exe` as an MCP server using stdio in your MCP client. Keep the desktop app open while using MCP.
5. Verify the downloaded archive with its `.sha256` file if desired.

Stills and preview screenshots are saved to `Pictures/Asterion Observatory/Captures`. SER recordings are saved to `Videos/Asterion Observatory/Recordings`.

## Preview status

This release adds RAW8/binned acquisition and SER recording. Live 1280×960 preview and saved PNG capture were verified with an attached ZWO ASI120MC on the previous preview. RAW8, binning, and SER recording should be exercised with the operator's camera before an observing session. Plate solving, catalog-based object identification, and mount/focuser integrations are not included. Bright-point and focus metrics are image heuristics; they do not identify astronomical objects. AI image analysis is provided by the connected MCP client, not an embedded model.

Camera settings are sent to the camera only when an operator or MCP client requests a change. The suggestion tool only returns a proposal.
