using System.Windows;
using Asterion.Core;

namespace Asterion.Desktop;

public partial class MainWindow : Window
{
    private readonly ICameraSession _cameraSession;
    public MainWindow(ICameraSession cameraSession)
    {
        InitializeComponent();
        _cameraSession = cameraSession;
        var status = cameraSession.GetStatus();
        StatusMessage.Text = "ASI camera ready to scan.";
        PreviewMessage.Text = status.Message ?? PreviewMessage.Text;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var cameras = await _cameraSession.EnumerateAsync(CancellationToken.None);
            if (cameras.Count == 0) { StatusMessage.Text = "No ZWO ASI cameras found."; return; }
            await _cameraSession.ConnectAsync(cameras[0].Id, CancellationToken.None);
            StatusMessage.Text = $"Connected: {cameras[0].Name} ({cameras[0].Width} × {cameras[0].Height})";
            PreviewMessage.Text = "Camera connected. Request live frames through the MCP server.";
        }
        catch (Exception ex) { StatusMessage.Text = ex.Message; }
        finally { ConnectButton.IsEnabled = true; }
    }
}
