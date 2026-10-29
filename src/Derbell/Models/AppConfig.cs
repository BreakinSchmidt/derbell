using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Derbell.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PopupPositionPreset
{
    TopRight,
    BottomRight,
    TopLeft,
    BottomLeft,
    Custom
}

public sealed class MonitoredCameraConfig
{
    public string EntityId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string PersonEventEntityId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(FriendlyName)
        ? EntityId
        : $"{FriendlyName} ({EntityId})";
}

public sealed class AppConfig
{
    public string HomeAssistantUrl { get; set; } = "http://homeassistant.local:8123";
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// List of cameras monitored for Nest person detection (camera_person).
    /// </summary>
    public List<MonitoredCameraConfig> Cameras { get; set; } = new();

    // Legacy single-camera properties retained for backward config migration
    public string CameraEntityId { get; set; } = string.Empty;
    public string CameraFriendlyName { get; set; } = "Nest Outdoor Cam";
    public string NestDeviceId { get; set; } = string.Empty;

    public string TargetMonitorDeviceName { get; set; } = string.Empty;
    public PopupPositionPreset PositionPreset { get; set; } = PopupPositionPreset.TopRight;
    public int EdgeMargin { get; set; } = 24;
    public int CustomX { get; set; } = 100;
    public int CustomY { get; set; } = 100;
    public int Width { get; set; } = 480;
    public int Height { get; set; } = 270;

    public int PersonAbsenceTimeoutSeconds { get; set; } = 20;
    public int ManualDismissCooldownSeconds { get; set; } = 15;

    public bool Muted { get; set; } = true;
    public bool PlayAlertChime { get; set; } = true;
    public int AlertChimeVolumePercent { get; set; } = 85;
    public bool ClickThroughVideo { get; set; } = false;
    public bool KeepStreamWarm { get; set; } = false;
    public string DismissHotkey { get; set; } = "Ctrl+Shift+X";
    public bool RunAtStartup { get; set; } = false;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<MonitoredCameraConfig> GetEnabledCameras()
    {
        MigrateLegacyCameraIfNeeded();
        return Cameras.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.EntityId)).ToList();
    }

    public void MigrateLegacyCameraIfNeeded()
    {
        if (Cameras.Count == 0 && !string.IsNullOrWhiteSpace(CameraEntityId))
        {
            Cameras.Add(new MonitoredCameraConfig
            {
                EntityId = CameraEntityId,
                FriendlyName = string.IsNullOrWhiteSpace(CameraFriendlyName) ? CameraEntityId : CameraFriendlyName,
                DeviceId = NestDeviceId,
                Enabled = true
            });
        }
    }

    public static string GetConfigFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "Derbell");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig Load()
    {
        try
        {
            string path = GetConfigFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                if (loaded != null)
                {
                    loaded.MigrateLegacyCameraIfNeeded();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to default config if file is corrupt or unreadable
        }

        return new AppConfig();
    }

    public void Save()
    {
        string path = GetConfigFilePath();
        string json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
        ApplyStartupRegistry(RunAtStartup);
    }

    public AppConfig Clone()
    {
        string json = JsonSerializer.Serialize(this, SerializerOptions);
        var cloned = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
        cloned.MigrateLegacyCameraIfNeeded();
        return cloned;
    }

    public static void ApplyStartupRegistry(bool enable)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "Derbell";
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(appName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(appName) != null)
                {
                    key.DeleteValue(appName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Ignore registry errors in restricted environments
        }
    }
}
