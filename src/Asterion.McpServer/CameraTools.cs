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

    [McpServerTool, Description("Suggest a bounded exposure/gain adjustment from live frame brightness and highlight clipping. This reports a proposal without changing the camera.")]
    public async Task<ExposureSuggestion> SuggestCameraSettings(CancellationToken cancellationToken)
    {
        var frame = await camera.CallAsync<ImageFrame>("get_preview_frame", null, cancellationToken);
        var status = await camera.CallAsync<CameraStatus>("get_camera_status", null, cancellationToken);
        var caps = await camera.CallAsync<CameraCapabilities>("get_camera_capabilities", null, cancellationToken);
        var current = status.Settings ?? throw new InvalidOperationException("Connect a camera first.");
        var m = frame.Metrics ?? throw new InvalidOperationException("The backend did not return image metrics.");
        var factor = m.ClippedPixelFraction > 0.03 || m.MeanLuminance > 90 ? 0.5
            : m.MeanLuminance < 1 ? 4.0
            : m.MeanLuminance < 8 ? 2.5
            : m.MeanLuminance < 18 ? 1.5
            : 1.0;
        // Keep suggestions useful for live preview; very long integrations belong in a future capture workflow.
        var previewCeiling = Math.Min(caps.ExposureMilliseconds.Maximum, 5000);
        var exposure = Math.Clamp(current.ExposureMilliseconds * factor, caps.ExposureMilliseconds.Minimum, previewCeiling);
        var gain = current.Gain;
        if (factor > 1 && exposure >= caps.ExposureMilliseconds.Maximum * 0.98)
            gain = Math.Min(caps.Gain.Maximum, gain + Math.Max(caps.Gain.Step, 5));
        if (factor < 1 && exposure <= caps.ExposureMilliseconds.Minimum * 1.02)
            gain = Math.Max(caps.Gain.Minimum, gain - Math.Max(caps.Gain.Step, 5));
        var reason = factor > 1 ? "The sampled frame is dark; this proposal raises exposure for a useful live preview, capped at 5 seconds." : factor < 1 ? "The sampled frame is bright or has clipped highlights; this proposal lowers exposure." : "The frame brightness is within the current target band; no change is suggested.";
        return new(current, current with { ExposureMilliseconds = exposure, Gain = gain }, reason, m);
    }

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
            Text = $"{description} {frame.Width}x{frame.Height}; {frame.Timestamp:O}; exposure {frame.Settings.ExposureMilliseconds} ms; gain {frame.Settings.Gain}. {FormatMetrics(frame.Metrics)} {FormatPath(frame.FilePath)}"
        },
        ImageContentBlock.FromBytes(frame.Data, frame.MimeType)
    ];

    private static string FormatMetrics(ImageMetrics? m) => m is null
        ? "No image quality metrics available."
        : $"Sampled image metrics: mean luminance {m.MeanLuminance:0.0}/255, 95th percentile {m.Luminance95thPercentile:0}/255, clipped highlights {m.ClippedPixelFraction:P2}, focus score {m.FocusScore:0.0}, bright point candidates {m.BrightPointCandidates}. These are image heuristics, not object identification. ";
    private static string FormatPath(string? path) => path is null ? string.Empty : $"Saved locally to {path}.";
}

public sealed record ExposureSuggestion(CameraSettings Current, CameraSettings Proposed, string Reason, ImageMetrics Metrics);
