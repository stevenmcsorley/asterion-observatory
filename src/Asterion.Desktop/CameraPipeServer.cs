using System.IO.Pipes;
using System.IO;
using System.Text.Json;
using Asterion.Core;

namespace Asterion.Desktop;

internal sealed class CameraPipeServer(ICameraSession session)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeProtocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleConnectionAsync(pipe, cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
            await HandleAsync(pipe, cancellationToken);
    }

    private async Task HandleAsync(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            var request = await PipeProtocol.ReadAsync<PipeRequest>(pipe, cancellationToken);
            var response = await DispatchAsync(request, cancellationToken);
            await PipeProtocol.WriteAsync(pipe, response, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PipeProtocol.WriteAsync(pipe, PipeResponse.Fail(ex.Message), cancellationToken);
        }
    }

    private async Task<PipeResponse> DispatchAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "list_cameras":
                return PipeResponse.Ok(await session.EnumerateAsync(cancellationToken));

            case "get_camera_status":
                return PipeResponse.Ok(session.GetStatus());

            case "connect_camera":
                await session.ConnectAsync(RequiredString(request.Arguments, "cameraId"), cancellationToken);
                return PipeResponse.Ok(session.GetStatus());

            case "disconnect_camera":
                await session.DisconnectAsync(cancellationToken);
                return PipeResponse.Ok(session.GetStatus());

            case "get_preview_frame":
                return PipeResponse.Ok(await session.GetPreviewFrameAsync(cancellationToken));

            case "set_camera_settings":
                var settings = request.Arguments.Deserialize<CameraSettings>() ??
                    throw new InvalidDataException("Camera settings are missing.");
                return PipeResponse.Ok(await session.SetSettingsAsync(settings, cancellationToken));

            case "capture_snapshot":
                var target = OptionalString(request.Arguments, "targetName");
                return PipeResponse.Ok(await session.CaptureSnapshotAsync(target, cancellationToken));

            case "capture_sequence":
                var capture = request.Arguments.Deserialize<CaptureRequest>() ??
                    throw new InvalidDataException("Capture request is missing.");
                return PipeResponse.Ok(await session.CaptureSequenceAsync(capture, cancellationToken));

            case "stop_capture":
                await session.StopCaptureAsync(cancellationToken);
                return PipeResponse.Ok(session.GetStatus());

            default:
                throw new InvalidOperationException($"Unknown camera operation '{request.Operation}'.");
        }
    }

    private static string RequiredString(JsonElement args, string name) =>
        OptionalString(args, name) ?? throw new InvalidDataException($"'{name}' is required.");

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
