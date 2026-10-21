using System.IO.Pipes;
using System.Text.Json;
using Asterion.Core;

namespace Asterion.McpServer;

public sealed class CameraPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<T> CallAsync<T>(string operation, object? arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await PipeProtocol.WriteAsync(pipe, new PipeRequest(operation, JsonSerializer.SerializeToElement(arguments ?? new { })), cancellationToken);
        var response = await PipeProtocol.ReadAsync<PipeResponse>(pipe, cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "The desktop app rejected the request.");
        if (response.Data is not { } data)
            throw new InvalidDataException("The desktop app returned no data.");

        return data.Deserialize<T>() ?? throw new InvalidDataException("The desktop app returned an invalid response.");
    }
}
