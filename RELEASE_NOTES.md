# Asterion Observatory v0.1.0 Preview

This is the first packaged preview of Asterion Observatory, an independent astronomy imaging app with a WPF desktop interface and local MCP camera tools.

## Included

- Self-contained Windows x64 desktop application and MCP stdio server
- ZWO ASI camera discovery, connect/disconnect, continuous preview, exposure/gain controls, and PNG snapshots
- Live image heuristics for mean brightness, highlight clipping, focus response, and bright-point candidates
- MCP tools for live image return, bounded settings, saved analysis captures, and non-applying exposure/gain suggestions
- Bundled x64 ZWO `ASICamera2.dll`; third-party license is included in `vendor/asi/LICENSE.txt`

## Install and start

1. Download and extract `Asterion-Observatory-v0.1.0-preview.1-win-x64.zip`.
2. Run `Asterion.Desktop/Asterion.Desktop.exe`.
3. Connect a supported ZWO ASI camera with its Windows driver installed.
4. Add `Asterion.McpServer/Asterion.McpServer.exe` as an MCP server using stdio in your MCP client. Keep the desktop app open while using MCP.
5. Verify the downloaded archive with its `.sha256` file if desired.

Snapshots are saved to `Pictures/Asterion Observatory/Captures`.

## Preview status

This release has passed the Windows x64 Release build in GitHub Actions. The application has not yet been validated against the user's physical camera in a live capture session. Video recording, plate solving, catalog-based object identification, and mount/focuser integrations are not included. Bright-point and focus metrics are image heuristics; they do not identify astronomical objects. AI image analysis is provided by the connected MCP client, not an embedded model.

Camera settings are sent to the camera only when an operator or MCP client requests a change. The suggestion tool only returns a proposal.
