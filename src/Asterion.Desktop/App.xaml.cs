using System.Windows;
using Asterion.Core;
using Asterion.Zwo;

namespace Asterion.Desktop;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private ICameraSession? _cameraSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _cameraSession = new CameraSession(new ZwoAsiCameraBackend());
        var pipeServer = new CameraPipeServer(_cameraSession);
        _ = pipeServer.RunAsync(_lifetime.Token);

        MainWindow = new MainWindow(_cameraSession);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        _cameraSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lifetime.Dispose();
        base.OnExit(e);
    }
}
