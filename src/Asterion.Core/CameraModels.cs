using System.Text.Json;

namespace Asterion.Core;

public enum CameraConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

public enum CameraCaptureState
{
    Idle,
    Preparing,
    Capturing,
    Stopping
}

public sealed record CameraDescriptor(
    string Id,
    string Name,
    string Model,
    string? SerialNumber,
    int Width,
    int Height,
    bool IsColor,
    bool HasShutter,
    bool HasCooler);

public sealed record NumericRange(double Minimum, double Maximum, double Step);

public sealed record CameraCapabilities(
    NumericRange ExposureMilliseconds,
    NumericRange Gain,
    IReadOnlyList<string> PixelFormats,
    int MaximumWidth,
    int MaximumHeight,
    int MaximumRoiWidth,
    int MaximumRoiHeight,
    IReadOnlyList<int>? BinningOptions = null);

public sealed record CameraSettings(
    double ExposureMilliseconds,
    double Gain,
    int? RoiX = null,
    int? RoiY = null,
    int? RoiWidth = null,
    int? RoiHeight = null,
    string? PixelFormat = null,
    int Bin = 1,
    int PreviewQuality = 75);

public sealed record CameraStatus(
    CameraConnectionState Connection,
    CameraCaptureState Capture,
    CameraDescriptor? Camera,
    CameraSettings? Settings,
    double? FramesPerSecond,
    long DroppedFrames,
    string? Message);

public sealed record ImageFrame(
    byte[] Data,
    string MimeType,
    int Width,
    int Height,
    DateTimeOffset Timestamp,
    CameraSettings Settings,
    ImageMetrics? Metrics = null,
    string? FilePath = null);

public sealed record ImageMetrics(
    double MeanLuminance,
    double Luminance95thPercentile,
    double ClippedPixelFraction,
    double FocusScore,
    int BrightPointCandidates);

public sealed record CaptureRequest(
    string TargetName,
    string Format,
    int? FrameLimit,
    TimeSpan? Duration,
    CameraSettings? Settings);

public sealed record CaptureResult(
    string FilePath,
    string Format,
    long FramesCaptured,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    CameraSettings Settings);

public interface ICameraBackend : IAsyncDisposable
{
    Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
    Task ConnectAsync(string cameraId, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task SetSettingsAsync(CameraSettings settings, CancellationToken cancellationToken);
    Task<ImageFrame> GetPreviewFrameAsync(CancellationToken cancellationToken);
    Task<ImageFrame> CaptureSnapshotAsync(string? targetName, CancellationToken cancellationToken);
    Task<CaptureResult> CaptureSequenceAsync(CaptureRequest request, CancellationToken cancellationToken);
    Task StopCaptureAsync(CancellationToken cancellationToken);
    CameraStatus GetStatus();
}

public interface ICameraSession : IAsyncDisposable
{
    Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
    Task ConnectAsync(string cameraId, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<CameraCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    CameraStatus GetStatus();
    Task<ImageFrame> GetPreviewFrameAsync(CancellationToken cancellationToken);
    Task<CameraSettings> SetSettingsAsync(CameraSettings requested, CancellationToken cancellationToken);
    Task<ImageFrame> CaptureSnapshotAsync(string? targetName, CancellationToken cancellationToken);
    Task<CaptureResult> CaptureSequenceAsync(CaptureRequest request, CancellationToken cancellationToken);
    Task StopCaptureAsync(CancellationToken cancellationToken);
}

public sealed record PipeRequest(string Operation, JsonElement Arguments);

public sealed record PipeResponse(bool Success, JsonElement? Data, string? Error)
{
    public static PipeResponse Ok<T>(T value) => new(true, JsonSerializer.SerializeToElement(value), null);
    public static PipeResponse Fail(string error) => new(false, null, error);
}

public static class PipeProtocol
{
    public const string PipeName = "Asterion.CameraControl.v1";
    public const int MaximumMessageBytes = 64 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumMessageBytes)
            throw new InvalidDataException("Message exceeds the configured limit.");

        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > MaximumMessageBytes)
            throw new InvalidDataException("Invalid message length.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty message.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}
