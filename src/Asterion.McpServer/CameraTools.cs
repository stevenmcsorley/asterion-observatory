using System.ComponentModel;
using Asterion.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Asterion.McpServer;

[McpServerToolType]
public sealed class CameraTools(CameraPipeClient camera)
{
    [McpServerTool, Description("List astronomy cameras currently available to the running Asterion desktop app.")]
    public Task<IReadOnlyList<CameraDescriptor>> ListCameras(CancellationToken cancellationToken) =>
        camera.CallAsync<IReadOnlyList<CameraDescriptor>>("list_cameras", null, cancellationToken);

    [McpServerTool, Description("Return live connection, capture, settings, frame-rate, and dropped-frame state from Asterion.")]
    public Task<CameraStatus> GetCameraStatus(CancellationToken cancellationToken) =>
        camera.CallAsync<CameraStatus>("get_camera_status", null, cancellationToken);

    [McpServerTool, Description("Connect Asterion to a camera by the exact ID returned from list_cameras.")]
    public Task<CameraStatus> ConnectCamera(
        [Description("Exact camera ID returned by list_cameras.")] string cameraId,
        CancellationToken cancellationToken) =>
        camera.CallAsync<CameraStatus>("connect_camera", new { cameraId }, cancellationToken);

    [McpServerTool, Description("Disconnect the active astronomy camera from Asterion.")]
    public Task<CameraStatus> DisconnectCamera(CancellationToken cancellationToken) =>
        camera.CallAsync<CameraStatus>("disconnect_camera", null, cancellationToken);

    [McpServerTool, Description("Return the current live camera preview as an image with timestamp, dimensions, and acquisition settings. Use your vision capability to inspect it.")]
    public async Task<IEnumerable<ContentBlock>> GetLiveView(CancellationToken cancellationToken)
    {
        var frame = await camera.CallAsync<ImageFrame>("get_preview_frame", null, cancellationToken);
        return FrameContent(frame, "Current live preview frame.");
    }

    [McpServerTool, Description("Change camera exposure and/or gain. Values must be within the active camera's reported limits.")]
    public async Task<CameraSettings> SetCameraSettings(
        [Description("Exposure time in milliseconds. Leave null to keep the current value.")] double? exposureMilliseconds,
        [Description("Analog gain. Leave null to keep the current value.")] double? gain,
        CancellationToken cancellationToken)
    {
        if (exposureMilliseconds is null && gain is null)
            throw new ArgumentException("Provide exposureMilliseconds, gain, or both.");

        var status = await camera.CallAsync<CameraStatus>("get_camera_status", null, cancellationToken);
        var current = status.Settings ?? throw new InvalidOperationException("Connect a camera before changing its settings.");
        var requested = current with
        {
            ExposureMilliseconds = exposureMilliseconds ?? current.ExposureMilliseconds,
            Gain = gain ?? current.Gain
        };
        return await camera.CallAsync<CameraSettings>("set_camera_settings", requested, cancellationToken);
    }

    [McpServerTool, Description("Capture one astronomy image, optionally using temporary exposure/gain settings, and return it as image content for AI vision analysis.")]
    public async Task<IEnumerable<ContentBlock>> CaptureForAnalysis(
        [Description("Optional target label used for the saved capture.")] string? targetName,
        [Description("Optional exposure in milliseconds; must be within the camera's reported range.")] double? exposureMilliseconds,
        [Description("Optional analog gain; must be within the camera's reported range.")] double? gain,
        CancellationToken cancellationToken)
    {
        var status = await camera.CallAsync<CameraStatus>("get_camera_status", null, cancellationToken);
        var original = status.Settings ?? throw new InvalidOperationException("Connect a camera before capturing.");
        try
        {
            if (exposureMilliseconds is not null || gain is not null)
            {
                var requested = original with
                {
                    ExposureMilliseconds = exposureMilliseconds ?? original.ExposureMilliseconds,
                    Gain = gain ?? original.Gain
                };
                await camera.CallAsync<CameraSettings>("set_camera_settings", requested, cancellationToken);
            }
            var frame = await camera.CallAsync<ImageFrame>("capture_snapshot", new { targetName }, cancellationToken);
            return FrameContent(frame, "Captured image for visual analysis. Interpret cautiously; image-only object naming may be uncertain.");
        }
        finally
        {
            if (exposureMilliseconds is not null || gain is not null)
                await camera.CallAsync<CameraSettings>("set_camera_settings", original, CancellationToken.None);
        }
    }

    private static IEnumerable<ContentBlock> FrameContent(ImageFrame frame, string description) =>
    [
        new TextContentBlock
        {
            Text = $"{description} {frame.Width}x{frame.Height}; {frame.Timestamp:O}; exposure {frame.Settings.ExposureMilliseconds} ms; gain {frame.Settings.Gain}."
        },
        ImageContentBlock.FromBytes(frame.Data, frame.MimeType)
    ];
}
