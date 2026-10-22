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
    private NumericRange? _exposureRange;
    private bool _recording;
    private ImageFrame? _lastFrame;
    private DateTimeOffset? _previousFrameTimestamp;
    private double _smoothedFps;
    private long _skippedPreviewTicks;

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
            SetExposureRange(capabilities.ExposureMilliseconds);
            BinningCombo.ItemsSource = capabilities.BinningOptions ?? [1];
            PixelFormatCombo.ItemsSource = capabilities.PixelFormats;
            BinningCombo.IsEnabled = PixelFormatCombo.IsEnabled = true;
            GainSlider.Minimum = capabilities.Gain.Minimum;
            GainSlider.Maximum = Math.Max(capabilities.Gain.Maximum, capabilities.Gain.Minimum + 1);
            _currentSettings = await _cameraSession.SetSettingsAsync(_cameraSession.GetStatus().Settings!, CancellationToken.None);
            ShowSettings(_currentSettings);
            _connected = true;
            ConnectionLabel.Text = "CAMERA CONNECTED";
            ConnectionLabel.Foreground = System.Windows.Media.Brushes.LightGreen;
            ConnectButton.Content = "Disconnect camera";
            SnapshotButton.IsEnabled = true;
            ScreenshotButton.IsEnabled = AutoExposureButton.IsEnabled = true;
            FastPreviewButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
            ExposureSlider.IsEnabled = GainSlider.IsEnabled = true;
            QualitySlider.IsEnabled = true;
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
        if (!_connected) return;
        if (_readingFrame) { SkippedLabel.Text = $"{++_skippedPreviewTicks} UI skips"; return; }
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

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is null) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Asterion Observatory", "Captures");
            Directory.CreateDirectory(folder);
            var extension = _lastFrame.MimeType == "image/jpeg" ? ".jpg" : ".png";
            var path = Path.Combine(folder, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}{extension}");
            await File.WriteAllBytesAsync(path, _lastFrame.Data);
            StatusMessage.Text = $"Current frame saved · {path}";
        }
        catch (Exception ex) { StatusMessage.Text = $"Screenshot failed: {ex.Message}"; }
    }

    private async void AutoExposureButton_Click(object sender, RoutedEventArgs e)
    {
        AutoExposureButton.IsEnabled = false;
        try
        {
            var frame = await _cameraSession.GetPreviewFrameAsync(CancellationToken.None);
            if (frame.Metrics is not { } metrics || _currentSettings is null) return;
            var factor = metrics.ClippedPixelFraction > 0.03 || metrics.MeanLuminance > 100 ? 0.5
                : metrics.MeanLuminance < 1 ? 4.0 : metrics.MeanLuminance < 8 ? 2.5
                : metrics.MeanLuminance < 25 ? 1.5 : 1.0;
            var proposed = _currentSettings with { ExposureMilliseconds = Math.Clamp(_currentSettings.ExposureMilliseconds * factor, _exposureRange?.Minimum ?? 0.001, Math.Min(_exposureRange?.Maximum ?? 5000, 5000)) };
            _currentSettings = await _cameraSession.SetSettingsAsync(proposed, CancellationToken.None);
            ShowSettings(_currentSettings);
            StatusMessage.Text = "Auto exposure applied from live frame brightness. Adjust exposure or gain manually for fine control.";
        }
        catch (Exception ex) { StatusMessage.Text = $"Auto exposure failed: {ex.Message}"; }
        finally { AutoExposureButton.IsEnabled = _connected; }
    }

    private async void FastPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        FastPreviewButton.IsEnabled = false;
        try
        {
            var capabilities = await _cameraSession.GetCapabilitiesAsync(CancellationToken.None);
            var bin = capabilities.BinningOptions?.Contains(2) == true ? 2 : 1;
            var exposure = Math.Clamp(33d, capabilities.ExposureMilliseconds.Minimum, capabilities.ExposureMilliseconds.Maximum);
            var proposed = (_currentSettings ?? new CameraSettings(exposure, capabilities.Gain.Minimum)) with
            {
                ExposureMilliseconds = exposure,
                Bin = bin,
                PixelFormat = capabilities.PixelFormats.Contains("RAW8") ? "RAW8" : capabilities.PixelFormats[0]
            };
            _currentSettings = await _cameraSession.SetSettingsAsync(proposed, CancellationToken.None);
            ShowSettings(_currentSettings);
            StatusMessage.Text = "Fast preview applied. The shorter exposure and lighter data format favor frame rate over faint detail.";
        }
        catch (Exception ex) { StatusMessage.Text = $"Fast preview failed: {ex.Message}"; }
        finally { FastPreviewButton.IsEnabled = _connected; }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        if (_recording)
        {
            RecordButton.IsEnabled = false;
            await _cameraSession.StopCaptureAsync(CancellationToken.None);
            return;
        }
        _recording = true;
        RecordButton.Content = "Stop recording";
        ExposureSlider.IsEnabled = GainSlider.IsEnabled = QualitySlider.IsEnabled = BinningCombo.IsEnabled = PixelFormatCombo.IsEnabled = false;
        SnapshotButton.IsEnabled = ScreenshotButton.IsEnabled = AutoExposureButton.IsEnabled = FastPreviewButton.IsEnabled = false;
        StatusMessage.Text = "Recording SER video… click Stop recording to finish.";
        try
        {
            var result = await _cameraSession.CaptureSequenceAsync(new CaptureRequest("observation", "SER", null, TimeSpan.FromHours(12), null), CancellationToken.None);
            StatusMessage.Text = $"Video saved · {result.FramesCaptured} frames · {result.FilePath}";
        }
        catch (Exception ex) { StatusMessage.Text = $"Recording failed: {ex.Message}"; }
        finally
        {
            _recording = false; RecordButton.Content = "Record SER"; RecordButton.IsEnabled = _connected;
            ExposureSlider.IsEnabled = GainSlider.IsEnabled = QualitySlider.IsEnabled = BinningCombo.IsEnabled = PixelFormatCombo.IsEnabled = _connected;
            SnapshotButton.IsEnabled = ScreenshotButton.IsEnabled = AutoExposureButton.IsEnabled = FastPreviewButton.IsEnabled = _connected;
        }
    }

    private void FormatSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingControls || !_connected || _currentSettings is null) return;
        var format = PixelFormatCombo.SelectedItem?.ToString() ?? _currentSettings.PixelFormat;
        _currentSettings = _currentSettings with { Bin = BinningCombo.SelectedItem is int bin ? bin : 1, PixelFormat = format };
        ScheduleSettings();
    }

    private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || ExposureValue is null) return;
        ExposureValue.Text = $"{ExposureFromSlider(ExposureSlider.Value):0.###} ms";
        ScheduleSettings();
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || GainValue is null) return;
        GainValue.Text = $"{GainSlider.Value:0} gain";
        ScheduleSettings();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || QualityValue is null) return;
        QualityValue.Text = $"{QualitySlider.Value:0}%";
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
            var requested = _currentSettings with { ExposureMilliseconds = ExposureFromSlider(ExposureSlider.Value), Gain = GainSlider.Value, Bin = BinningCombo.SelectedItem is int bin ? bin : 1, PixelFormat = PixelFormatCombo.SelectedItem?.ToString() ?? "RGB24", PreviewQuality = (int)Math.Round(QualitySlider.Value) };
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
                SetExposureRange(capabilities.ExposureMilliseconds);
                BinningCombo.ItemsSource = capabilities.BinningOptions ?? [1];
                PixelFormatCombo.ItemsSource = capabilities.PixelFormats;
                BinningCombo.IsEnabled = PixelFormatCombo.IsEnabled = true;
                GainSlider.Minimum = capabilities.Gain.Minimum;
                GainSlider.Maximum = Math.Max(capabilities.Gain.Maximum, capabilities.Gain.Minimum + 1);
                if (_currentSettings is not null) ShowSettings(_currentSettings);
            }
            _connected = true;
            ConnectionLabel.Text = "CAMERA CONNECTED";
            ConnectionLabel.Foreground = System.Windows.Media.Brushes.LightGreen;
            ConnectButton.Content = "Disconnect camera";
            SnapshotButton.IsEnabled = true;
            ScreenshotButton.IsEnabled = AutoExposureButton.IsEnabled = true;
            FastPreviewButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
            ExposureSlider.IsEnabled = GainSlider.IsEnabled = true;
            QualitySlider.IsEnabled = true;
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
            ExposureSlider.Value = ExposureToSlider(settings.ExposureMilliseconds);
            GainSlider.Value = Math.Clamp(settings.Gain, GainSlider.Minimum, GainSlider.Maximum);
            BinningCombo.SelectedItem = settings.Bin;
            PixelFormatCombo.SelectedItem = settings.PixelFormat;
            QualitySlider.Value = settings.PreviewQuality;
            QualityValue.Text = $"{settings.PreviewQuality}%";
            ExposureValue.Text = $"{settings.ExposureMilliseconds:0.###} ms";
            GainValue.Text = $"{settings.Gain:0} gain";
        }
        finally { _updatingControls = false; }
    }

    private void SetExposureRange(NumericRange range)
    {
        _exposureRange = range;
        ExposureSlider.Minimum = 0;
        ExposureSlider.Maximum = 100;
    }

    private double ExposureFromSlider(double position)
    {
        var range = _exposureRange ?? new NumericRange(0.001, 1000, 0.001);
        var minimum = Math.Max(range.Minimum, 0.001);
        var maximum = Math.Max(range.Maximum, minimum);
        return minimum * Math.Pow(maximum / minimum, Math.Clamp(position, 0, 100) / 100d);
    }

    private double ExposureToSlider(double exposure)
    {
        var range = _exposureRange ?? new NumericRange(0.001, 1000, 0.001);
        var minimum = Math.Max(range.Minimum, 0.001);
        var maximum = Math.Max(range.Maximum, minimum);
        if (maximum <= minimum) return 0;
        return 100 * Math.Log(Math.Clamp(exposure, minimum, maximum) / minimum) / Math.Log(maximum / minimum);
    }

    private void ShowFrame(ImageFrame frame)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(frame.Data);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
        _lastFrame = frame;
        LiveBadge.Text = $"LIVE · {frame.Settings.PixelFormat ?? "RGB24"} · {frame.Settings.Bin}×";
        LiveBadge.Foreground = System.Windows.Media.Brushes.LightGreen;
        if (_previousFrameTimestamp is { } previous)
        {
            var elapsed = (frame.Timestamp - previous).TotalSeconds;
            if (elapsed > 0) { var instant = 1 / elapsed; _smoothedFps = _smoothedFps == 0 ? instant : _smoothedFps * 0.75 + instant * 0.25; FpsLabel.Text = $"{_smoothedFps:0.0} fps"; }
        }
        _previousFrameTimestamp = frame.Timestamp;
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
        ScreenshotButton.IsEnabled = AutoExposureButton.IsEnabled = false;
        FastPreviewButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        BinningCombo.IsEnabled = PixelFormatCombo.IsEnabled = false;
        ExposureSlider.IsEnabled = GainSlider.IsEnabled = QualitySlider.IsEnabled = false;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        CameraName.Text = "No camera selected";
        CameraModel.Text = "ZWO ASI camera";
        FrameInfo.Text = "MCP CAMERA BRIDGE";
        LiveBadge.Text = "NO FRAME";
        LiveBadge.Foreground = System.Windows.Media.Brushes.Gray;
        FpsLabel.Text = "— fps";
        _previousFrameTimestamp = null;
        _smoothedFps = 0;
        StatusMessage.Text = message;
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop(); _settingsTimer.Stop(); _statusTimer.Stop();
        base.OnClosed(e);
    }
}
