using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Asterion.Core;

namespace Asterion.Desktop;

public partial class MainWindow : Window
{
    private readonly ICameraSession _cameraSession;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _readingFrame;
    private bool _updatingControls;
    private bool _connected;
    private CameraSettings? _currentSettings;

    public MainWindow(ICameraSession cameraSession)
    {
        InitializeComponent();
        _cameraSession = cameraSession;
        _previewTimer.Tick += PreviewTimer_Tick;
        _settingsTimer.Tick += SettingsTimer_Tick;
        _statusTimer.Tick += UiRefresh_Tick;
        _statusTimer.Start();
        var status = cameraSession.GetStatus();
        StatusMessage.Text = status.Message ?? "Connect an ASI camera to begin.";
        if (status.Connection == CameraConnectionState.Connected)
            _ = ReflectConnectedCameraAsync(status);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            if (_connected || _cameraSession.GetStatus().Connection == CameraConnectionState.Connected)
            {
                _previewTimer.Stop();
                await _cameraSession.DisconnectAsync(CancellationToken.None);
                SetDisconnected("Camera disconnected.");
                return;
            }

            StatusMessage.Text = "Searching for ZWO cameras…";
            var cameras = await _cameraSession.EnumerateAsync(CancellationToken.None);
            if (cameras.Count == 0) { StatusMessage.Text = "No ZWO ASI cameras found. Check the USB connection and driver."; return; }
            var camera = cameras[0];
            await _cameraSession.ConnectAsync(camera.Id, CancellationToken.None);
            CameraName.Text = camera.Name;
            CameraModel.Text = $"{camera.Width} × {camera.Height} · ZWO ASI";
            var capabilities = await _cameraSession.GetCapabilitiesAsync(CancellationToken.None);
            ExposureSlider.Minimum = Math.Max(capabilities.ExposureMilliseconds.Minimum, 0.001);
            ExposureSlider.Maximum = Math.Max(capabilities.ExposureMilliseconds.Maximum, ExposureSlider.Minimum + 0.001);
            GainSlider.Minimum = capabilities.Gain.Minimum;
            GainSlider.Maximum = Math.Max(capabilities.Gain.Maximum, capabilities.Gain.Minimum + 1);
            _currentSettings = await _cameraSession.SetSettingsAsync(_cameraSession.GetStatus().Settings!, CancellationToken.None);
            ShowSettings(_currentSettings);
            _connected = true;
            ConnectionLabel.Text = "CAMERA CONNECTED";
            ConnectionLabel.Foreground = System.Windows.Media.Brushes.LightGreen;
            ConnectButton.Content = "Disconnect camera";
            SnapshotButton.IsEnabled = true;
            ExposureSlider.IsEnabled = GainSlider.IsEnabled = true;
            StatusMessage.Text = $"Connected to {camera.Name}. Live preview is active.";
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            _previewTimer.Start();
        }
        catch (Exception ex) { StatusMessage.Text = ex.Message; }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_readingFrame || !_connected) return;
        _readingFrame = true;
        try
        {
            var frame = await _cameraSession.GetPreviewFrameAsync(CancellationToken.None);
            ShowFrame(frame);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Preview paused: {ex.Message}";
            _previewTimer.Stop();
        }
        finally { _readingFrame = false; }
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        SnapshotButton.IsEnabled = false;
        try
        {
            var frame = await _cameraSession.CaptureSnapshotAsync("snapshot", CancellationToken.None);
            StatusMessage.Text = $"Snapshot saved · {frame.FilePath}";
            ShowFrame(frame);
        }
        catch (Exception ex) { StatusMessage.Text = $"Snapshot failed: {ex.Message}"; }
        finally { SnapshotButton.IsEnabled = _connected; }
    }

    private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || ExposureValue is null) return;
        ExposureValue.Text = $"{ExposureSlider.Value:0.###} ms";
        ScheduleSettings();
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || GainValue is null) return;
        GainValue.Text = $"{GainSlider.Value:0} gain";
        ScheduleSettings();
    }

    private void ScheduleSettings()
    {
        if (_connected) { _settingsTimer.Stop(); _settingsTimer.Start(); }
    }

    private async void SettingsTimer_Tick(object? sender, EventArgs e)
    {
        _settingsTimer.Stop();
        if (!_connected || _currentSettings is null) return;
        try
        {
            var requested = _currentSettings with { ExposureMilliseconds = ExposureSlider.Value, Gain = GainSlider.Value };
            _currentSettings = await _cameraSession.SetSettingsAsync(requested, CancellationToken.None);
            StatusMessage.Text = "Acquisition settings applied.";
        }
        catch (Exception ex) { StatusMessage.Text = $"Could not apply settings: {ex.Message}"; }
    }

    private async void UiRefresh_Tick(object? sender, EventArgs e) { await RefreshStatusAsync(); }

    private async Task ReflectConnectedCameraAsync(CameraStatus status)
    {
        try
        {
            if (status.Camera is { } camera)
            {
                CameraName.Text = camera.Name;
                CameraModel.Text = $"{camera.Width} × {camera.Height} · ZWO ASI";
                _currentSettings = status.Settings;
                var capabilities = await _cameraSession.GetCapabilitiesAsync(CancellationToken.None);
                ExposureSlider.Minimum = Math.Max(capabilities.ExposureMilliseconds.Minimum, 0.001);
                ExposureSlider.Maximum = Math.Max(capabilities.ExposureMilliseconds.Maximum, ExposureSlider.Minimum + 0.001);
                GainSlider.Minimum = capabilities.Gain.Minimum;
                GainSlider.Maximum = Math.Max(capabilities.Gain.Maximum, capabilities.Gain.Minimum + 1);
                if (_currentSettings is not null) ShowSettings(_currentSettings);
            }
            _connected = true;
            ConnectionLabel.Text = "CAMERA CONNECTED";
            ConnectionLabel.Foreground = System.Windows.Media.Brushes.LightGreen;
            ConnectButton.Content = "Disconnect camera";
            SnapshotButton.IsEnabled = true;
            ExposureSlider.IsEnabled = GainSlider.IsEnabled = true;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            _previewTimer.Start();
        }
        catch (Exception ex) { StatusMessage.Text = ex.Message; }
    }

    private async Task RefreshStatusAsync()
    {
        var status = _cameraSession.GetStatus();
        if (status.Connection == CameraConnectionState.Connected && !_connected)
            await ReflectConnectedCameraAsync(status);
        else if (status.Connection == CameraConnectionState.Disconnected && _connected)
            SetDisconnected("Camera disconnected from the active session.");
        else if (status.Connection == CameraConnectionState.Faulted && !_connected)
        {
            ConnectionLabel.Text = "CAMERA ERROR";
            ConnectionLabel.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
        if (status.Settings is { } settings && _connected)
        {
            _currentSettings = settings;
            if (!_settingsTimer.IsEnabled) ShowSettings(settings);
        }
    }

    private void ShowSettings(CameraSettings settings)
    {
        _updatingControls = true;
        try
        {
            ExposureSlider.Value = Math.Clamp(settings.ExposureMilliseconds, ExposureSlider.Minimum, ExposureSlider.Maximum);
            GainSlider.Value = Math.Clamp(settings.Gain, GainSlider.Minimum, GainSlider.Maximum);
            ExposureValue.Text = $"{settings.ExposureMilliseconds:0.###} ms";
            GainValue.Text = $"{settings.Gain:0} gain";
        }
        finally { _updatingControls = false; }
    }

    private void ShowFrame(ImageFrame frame)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(frame.Data);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        FrameInfo.Text = frame.Metrics is { } m
            ? $"LIVE · {frame.Width} × {frame.Height} · {frame.Timestamp:HH:mm:ss} UTC · LUMA {m.MeanLuminance:0} · FOCUS {m.FocusScore:0}"
            : $"LIVE · {frame.Width} × {frame.Height} · {frame.Timestamp:HH:mm:ss} UTC";
    }

    private void SetDisconnected(string message)
    {
        _connected = false;
        ConnectionLabel.Text = "CAMERA DISCONNECTED";
        ConnectionLabel.Foreground = System.Windows.Media.Brushes.DarkGray;
        ConnectButton.Content = "Find and connect camera";
        SnapshotButton.IsEnabled = false;
        ExposureSlider.IsEnabled = GainSlider.IsEnabled = false;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        CameraName.Text = "No camera selected";
        CameraModel.Text = "ZWO ASI camera";
        FrameInfo.Text = "MCP CAMERA BRIDGE";
        StatusMessage.Text = message;
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop(); _settingsTimer.Stop(); _statusTimer.Stop();
        base.OnClosed(e);
    }
}
