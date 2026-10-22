namespace Asterion.Core;

public sealed class CameraSession(ICameraBackend? backend) : ICameraSession
{
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private bool _disposed;


    public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken) =>
        backend is null
            ? Task.FromResult<IReadOnlyList<CameraDescriptor>>([])
            : backend.EnumerateAsync(cancellationToken);

    public CameraStatus GetStatus() => backend?.GetStatus() ??
        new CameraStatus(CameraConnectionState.Disconnected, CameraCaptureState.Idle, null, null, null, 0,
            "The ZWO camera backend is not configured.");

    public Task ConnectAsync(string cameraId, CancellationToken cancellationToken) =>
        backend is null
            ? Task.FromException(new InvalidOperationException("The ZWO camera backend is not configured."))
            : backend.ConnectAsync(cameraId, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        backend is null
            ? Task.CompletedTask
            : backend.DisconnectAsync(cancellationToken);

    public Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        RequireBackend().GetCapabilitiesAsync(cancellationToken);

    public Task<ImageFrame> GetPreviewFrameAsync(CancellationToken cancellationToken) =>
        RequireBackend().GetPreviewFrameAsync(cancellationToken);

    public async Task<CameraSettings> SetSettingsAsync(CameraSettings requested, CancellationToken cancellationToken)
    {
        var active = RequireBackend();
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            var capabilities = await active.GetCapabilitiesAsync(cancellationToken);
            ValidateRange(requested.ExposureMilliseconds, capabilities.ExposureMilliseconds, "Exposure");
            ValidateRange(requested.Gain, capabilities.Gain, "Gain");
            if (requested.PreviewQuality is < 40 or > 95)
                throw new ArgumentOutOfRangeException(nameof(requested), "Preview JPEG quality must be between 40 and 95.");
            if (capabilities.BinningOptions is { } bins && !bins.Contains(requested.Bin))
                throw new ArgumentOutOfRangeException(nameof(requested), $"Binning must be one of: {string.Join(", ", bins)}.");
            if (requested.PixelFormat is { } format && !capabilities.PixelFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentOutOfRangeException(nameof(requested), $"Pixel format '{format}' is not supported by this camera.");
            ValidateRoi(requested, capabilities);

            var current = await active.GetSettingsAsync(cancellationToken);
            var applied = requested with
            {
                RoiX = requested.RoiX ?? current.RoiX,
                RoiY = requested.RoiY ?? current.RoiY,
                RoiWidth = requested.RoiWidth ?? current.RoiWidth,
                RoiHeight = requested.RoiHeight ?? current.RoiHeight,
                PixelFormat = requested.PixelFormat ?? current.PixelFormat
            };
            await active.SetSettingsAsync(applied, cancellationToken);
            return applied;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public Task<ImageFrame> CaptureSnapshotAsync(string? targetName, CancellationToken cancellationToken) =>
        RequireBackend().CaptureSnapshotAsync(SanitizeTargetName(targetName), cancellationToken);

    public Task<CaptureResult> CaptureSequenceAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        if (request.FrameLimit is null && request.Duration is null)
            throw new ArgumentException("Set either a frame limit or a duration.", nameof(request));
        if (request.FrameLimit is not null && request.Duration is not null)
            throw new ArgumentException("Set either a frame limit or a duration, not both.", nameof(request));
        if (request.FrameLimit is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request), "Frame limit must be between 1 and 1000.");
        if (request.Duration is { } duration && (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(12)))
            throw new ArgumentOutOfRangeException(nameof(request), "Duration must be between 1 ms and 12 hours.");

        var safeRequest = request with { TargetName = SanitizeTargetName(request.TargetName) ?? "capture" };
        return RequireBackend().CaptureSequenceAsync(safeRequest, cancellationToken);
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken) => RequireBackend().StopCaptureAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settingsGate.Dispose();
        if (backend is not null)
            await backend.DisposeAsync();
    }

    private ICameraBackend RequireBackend() => backend ??
        throw new InvalidOperationException("The ZWO camera backend is not configured yet.");

    private static void ValidateRange(double value, NumericRange range, string name)
    {
        if (value < range.Minimum || value > range.Maximum || double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, $"{name} must be in the range {range.Minimum} to {range.Maximum}.");
    }

    private static void ValidateRoi(CameraSettings settings, CameraCapabilities capabilities)
    {
        if (settings.RoiX is < 0 || settings.RoiY is < 0 || settings.RoiWidth is <= 0 || settings.RoiHeight is <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "ROI coordinates and dimensions are invalid.");

        if (settings.RoiWidth is { } width && width > capabilities.MaximumRoiWidth ||
            settings.RoiHeight is { } height && height > capabilities.MaximumRoiHeight)
            throw new ArgumentOutOfRangeException(nameof(settings), "ROI dimensions exceed the camera limit.");

        if (settings.RoiX is { } x && settings.RoiWidth is { } roiWidth && x + roiWidth > capabilities.MaximumWidth ||
            settings.RoiY is { } y && settings.RoiHeight is { } roiHeight && y + roiHeight > capabilities.MaximumHeight)
            throw new ArgumentOutOfRangeException(nameof(settings), "ROI lies outside the sensor area.");
    }

    private static string? SanitizeTargetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var safe = string.Concat(value.Trim().Take(64).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return safe.Trim('_');
    }
}
