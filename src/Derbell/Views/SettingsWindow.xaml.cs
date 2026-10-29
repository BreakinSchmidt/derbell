using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Derbell.Interop;
using Derbell.Models;
using Derbell.Services;

namespace Derbell.Views;

public partial class SettingsWindow : Window
{
    private readonly HomeAssistantClient _haClient;
    private readonly PopupWindow _popupWindow;
    private readonly ObservableCollection<MonitoredCameraConfig> _cameraItems = new();
    private AppConfig _workingConfig;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();

    public event Action<AppConfig>? ConfigSaved;
    public event Action<AppConfig>? TestPopupRequested;

    public SettingsWindow(AppConfig currentConfig, HomeAssistantClient haClient, PopupWindow popupWindow)
    {
        InitializeComponent();
        _workingConfig = currentConfig.Clone();
        _haClient = haClient;
        _popupWindow = popupWindow;

        CamerasChecklist.ItemsSource = _cameraItems;

        _haClient.ConnectionStateChanged += OnConnectionStateChanged;
        _popupWindow.PlacementSaved += OnPopupPlacementSaved;

        PopulatePresets();
        PopulateMonitors();
        LoadConfigIntoFields(_workingConfig);
        UpdateConnectionBadge(_haClient.ConnectionState, _haClient.LastError);

        if (_haClient.ConnectionState == HaConnectionState.Connected)
        {
            _ = LoadCamerasFromHomeAssistantAsync();
        }
    }

    private void PopulatePresets()
    {
        PositionPresetCombo.ItemsSource = Enum.GetValues<PopupPositionPreset>();
    }

    private void PopulateMonitors()
    {
        _monitors = MonitorHelper.GetMonitors();
        MonitorCombo.ItemsSource = _monitors;
        MonitorCombo.DisplayMemberPath = nameof(MonitorInfo.DisplayLabel);
        MonitorCombo.SelectedValuePath = nameof(MonitorInfo.DeviceName);

        MonitorInfo selected = MonitorHelper.GetTargetMonitor(_workingConfig.TargetMonitorDeviceName, _monitors);
        MonitorCombo.SelectedValue = selected.DeviceName;
    }

    private void LoadConfigIntoFields(AppConfig cfg)
    {
        HaUrlTextBox.Text = cfg.HomeAssistantUrl;
        AccessTokenBox.Password = cfg.AccessToken;

        _cameraItems.Clear();
        cfg.MigrateLegacyCameraIfNeeded();
        foreach (MonitoredCameraConfig cam in cfg.Cameras)
        {
            _cameraItems.Add(new MonitoredCameraConfig
            {
                EntityId = cam.EntityId,
                FriendlyName = string.IsNullOrWhiteSpace(cam.FriendlyName) ? cam.EntityId : cam.FriendlyName,
                DeviceId = cam.DeviceId,
                PersonEventEntityId = cam.PersonEventEntityId,
                Enabled = cam.Enabled
            });
        }

        PositionPresetCombo.SelectedItem = cfg.PositionPreset;
        EdgeMarginBox.Text = cfg.EdgeMargin.ToString();
        WidthBox.Text = cfg.Width.ToString();
        HeightBox.Text = cfg.Height.ToString();
        CustomXBox.Text = cfg.CustomX.ToString();
        CustomYBox.Text = cfg.CustomY.ToString();

        AbsenceTimeoutBox.Text = cfg.PersonAbsenceTimeoutSeconds.ToString();
        CooldownBox.Text = cfg.ManualDismissCooldownSeconds.ToString();
        HotkeyBox.Text = cfg.DismissHotkey;

        MutedCheckBox.IsChecked = cfg.Muted;
        PlayChimeCheckBox.IsChecked = cfg.PlayAlertChime;
        ChimeVolumeSlider.Value = Math.Clamp(cfg.AlertChimeVolumePercent, 5, 100);
        if (ChimeVolumeText != null)
        {
            ChimeVolumeText.Text = $"{(int)ChimeVolumeSlider.Value}%";
        }
        ClickThroughCheckBox.IsChecked = cfg.ClickThroughVideo;
        KeepWarmCheckBox.IsChecked = cfg.KeepStreamWarm;
        RunAtStartupCheckBox.IsChecked = cfg.RunAtStartup;
    }

    private AppConfig BuildConfigFromFields()
    {
        var cfg = _workingConfig.Clone();
        cfg.HomeAssistantUrl = HaUrlTextBox.Text.Trim();
        cfg.AccessToken = AccessTokenBox.Password.Trim();

        cfg.Cameras = _cameraItems
            .Where(c => !string.IsNullOrWhiteSpace(c.EntityId))
            .Select(c => new MonitoredCameraConfig
            {
                EntityId = c.EntityId.Trim(),
                FriendlyName = string.IsNullOrWhiteSpace(c.FriendlyName) ? c.EntityId.Trim() : c.FriendlyName.Trim(),
                DeviceId = c.DeviceId,
                PersonEventEntityId = c.PersonEventEntityId,
                Enabled = c.Enabled
            })
            .ToList();

        // Keep legacy fields synced with the first enabled camera
        MonitoredCameraConfig? firstEnabled = cfg.Cameras.FirstOrDefault(c => c.Enabled) ?? cfg.Cameras.FirstOrDefault();
        if (firstEnabled != null)
        {
            cfg.CameraEntityId = firstEnabled.EntityId;
            cfg.CameraFriendlyName = firstEnabled.FriendlyName;
            cfg.NestDeviceId = firstEnabled.DeviceId;
        }

        if (MonitorCombo.SelectedItem is MonitorInfo mon)
        {
            cfg.TargetMonitorDeviceName = mon.DeviceName;
        }

        if (PositionPresetCombo.SelectedItem is PopupPositionPreset preset)
        {
            cfg.PositionPreset = preset;
        }

        if (int.TryParse(EdgeMarginBox.Text, out int margin))
            cfg.EdgeMargin = Math.Clamp(margin, 0, 500);

        if (int.TryParse(WidthBox.Text, out int width))
            cfg.Width = Math.Clamp(width, 200, 3840);

        if (int.TryParse(HeightBox.Text, out int height))
            cfg.Height = Math.Clamp(height, 120, 2160);

        if (int.TryParse(CustomXBox.Text, out int customX))
            cfg.CustomX = customX;

        if (int.TryParse(CustomYBox.Text, out int customY))
            cfg.CustomY = customY;

        if (int.TryParse(AbsenceTimeoutBox.Text, out int timeout))
            cfg.PersonAbsenceTimeoutSeconds = Math.Clamp(timeout, 5, 600);

        if (int.TryParse(CooldownBox.Text, out int cooldown))
            cfg.ManualDismissCooldownSeconds = Math.Clamp(cooldown, 0, 600);

        cfg.DismissHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+Shift+X" : HotkeyBox.Text.Trim();
        cfg.Muted = MutedCheckBox.IsChecked == true;
        cfg.PlayAlertChime = PlayChimeCheckBox.IsChecked == true;
        cfg.AlertChimeVolumePercent = (int)Math.Clamp(ChimeVolumeSlider.Value, 5, 100);
        cfg.ClickThroughVideo = ClickThroughCheckBox.IsChecked == true;
        cfg.KeepStreamWarm = KeepWarmCheckBox.IsChecked == true;
        cfg.RunAtStartup = RunAtStartupCheckBox.IsChecked == true;

        return cfg;
    }

    private void OnConnectionStateChanged(HaConnectionState state, string? error)
    {
        Dispatcher.BeginInvoke(() => UpdateConnectionBadge(state, error));
    }

    private void UpdateConnectionBadge(HaConnectionState state, string? error)
    {
        switch (state)
        {
            case HaConnectionState.Connected:
                ConnectionStatusText.Text = "HA: Connected";
                ConnectionStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
                break;
            case HaConnectionState.Connecting:
                ConnectionStatusText.Text = "HA: Connecting...";
                ConnectionStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
                break;
            case HaConnectionState.AuthFailed:
                ConnectionStatusText.Text = "HA: Auth Failed";
                ConnectionStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                break;
            default:
                ConnectionStatusText.Text = "HA: Disconnected";
                ConnectionStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
                break;
        }
    }

    private async void ConnectDiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectDiscoverButton.IsEnabled = false;
        DiscoveryFeedbackText.Text = "Connecting to Home Assistant and discovering Nest cameras...";

        try
        {
            AppConfig tempConfig = BuildConfigFromFields();
            _haClient.UpdateConfigAndReconnect(tempConfig);

            for (int i = 0; i < 25; i++)
            {
                if (_haClient.ConnectionState is HaConnectionState.Connected or HaConnectionState.AuthFailed)
                    break;
                await Task.Delay(200);
            }

            if (_haClient.ConnectionState != HaConnectionState.Connected)
            {
                DiscoveryFeedbackText.Text = $"Connection did not succeed: {_haClient.LastError ?? "Check URL and Access Token"}";
                return;
            }

            await LoadCamerasFromHomeAssistantAsync();
        }
        catch (Exception ex)
        {
            DiscoveryFeedbackText.Text = $"Discovery error: {ex.Message}";
        }
        finally
        {
            ConnectDiscoverButton.IsEnabled = true;
        }
    }

    private async Task LoadCamerasFromHomeAssistantAsync()
    {
        try
        {
            IReadOnlyList<HaCameraDiscoveryItem> discovered = await _haClient.GetDiscoveredCamerasAsync();
            var existingByEntity = _cameraItems.ToDictionary(c => c.EntityId, StringComparer.OrdinalIgnoreCase);
            bool anyPreviouslyConfigured = existingByEntity.Count > 0;

            _cameraItems.Clear();

            foreach (HaCameraDiscoveryItem disc in discovered)
            {
                bool isEnabled = existingByEntity.TryGetValue(disc.EntityId, out MonitoredCameraConfig? prev)
                    ? prev.Enabled
                    : !anyPreviouslyConfigured; // Default to checked on first discovery

                _cameraItems.Add(new MonitoredCameraConfig
                {
                    EntityId = disc.EntityId,
                    FriendlyName = disc.FriendlyName,
                    DeviceId = disc.DeviceId,
                    PersonEventEntityId = disc.PersonEventEntityId,
                    Enabled = isEnabled
                });

                existingByEntity.Remove(disc.EntityId);
            }

            // Preserve any manually added cameras that weren't in the discovery payload
            foreach (MonitoredCameraConfig manual in existingByEntity.Values)
            {
                _cameraItems.Add(manual);
            }

            DiscoveryFeedbackText.Text =
                $"Discovered {discovered.Count} camera(s) — built-in Nest Person Detection (camera_person) linked automatically.";
        }
        catch (Exception ex)
        {
            DiscoveryFeedbackText.Text = $"Could not load cameras: {ex.Message}";
        }
    }

    private void SelectAllCameras_Click(object sender, RoutedEventArgs e)
    {
        foreach (MonitoredCameraConfig cam in _cameraItems)
        {
            cam.Enabled = true;
        }
        CamerasChecklist.Items.Refresh();
    }

    private void ClearAllCameras_Click(object sender, RoutedEventArgs e)
    {
        foreach (MonitoredCameraConfig cam in _cameraItems)
        {
            cam.Enabled = false;
        }
        CamerasChecklist.Items.Refresh();
    }

    private void AddManualCamera_Click(object sender, RoutedEventArgs e)
    {
        string raw = ManualCameraEntityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!raw.StartsWith("camera.", StringComparison.OrdinalIgnoreCase))
        {
            raw = "camera." + raw;
        }

        if (_cameraItems.Any(c => string.Equals(c.EntityId, raw, StringComparison.OrdinalIgnoreCase)))
        {
            ManualCameraEntityBox.Clear();
            return;
        }

        string friendly = raw["camera.".Length..].Replace('_', ' ');
        _cameraItems.Add(new MonitoredCameraConfig
        {
            EntityId = raw,
            FriendlyName = friendly,
            Enabled = true
        });

        ManualCameraEntityBox.Clear();
    }

    private void RefreshMonitorsButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateMonitors();
    }

    private void PositionPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool isCustom = PositionPresetCombo.SelectedItem is PopupPositionPreset.Custom;
        CustomXBox.IsEnabled = isCustom;
        CustomYBox.IsEnabled = isCustom;
    }

    private void PreviewPlacementButton_Click(object sender, RoutedEventArgs e)
    {
        AppConfig previewCfg = BuildConfigFromFields();
        _popupWindow.ShowPlacementPreview(previewCfg);
    }

    private void OnPopupPlacementSaved(MonitorInfo monitor, int relX, int relY)
    {
        Dispatcher.Invoke(() =>
        {
            PopulateMonitors();
            MonitorCombo.SelectedValue = monitor.DeviceName;
            PositionPresetCombo.SelectedItem = PopupPositionPreset.Custom;
            CustomXBox.Text = relX.ToString();
            CustomYBox.Text = relY.ToString();
        });
    }

    private void TestPopupButton_Click(object sender, RoutedEventArgs e)
    {
        AppConfig cfg = BuildConfigFromFields();
        TestPopupRequested?.Invoke(cfg);
    }

    private void ChimeVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ChimeVolumeText != null)
        {
            ChimeVolumeText.Text = $"{(int)e.NewValue}%";
        }
    }

    private void PreviewChimeButton_Click(object sender, RoutedEventArgs e)
    {
        ChimeSoundService.PlayRelaxingChime((int)ChimeVolumeSlider.Value);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _workingConfig = BuildConfigFromFields();
        _workingConfig.Save();
        ConfigSaved?.Invoke(_workingConfig);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _haClient.ConnectionStateChanged -= OnConnectionStateChanged;
        _popupWindow.PlacementSaved -= OnPopupPlacementSaved;
        base.OnClosed(e);
    }
}
