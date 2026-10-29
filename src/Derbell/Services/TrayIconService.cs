using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Derbell.Models;

namespace Derbell.Services;

public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _snoozeStatusItem;
    private readonly ToolStripMenuItem _showCameraMenu;
    private readonly ToolStripMenuItem _startWithWindowsItem;

    private HaConnectionState _connectionState = HaConnectionState.Disconnected;
    private bool _isSnoozed;
    private IntPtr _currentIconHandle = IntPtr.Zero;

    public event Action<MonitoredCameraConfig?>? ShowCameraRequested;
    public event Action? HideCameraRequested;
    public event Action? OpenSettingsRequested;
    public event Action<TimeSpan?>? SnoozeRequested;
    public event Action<bool>? ToggleRunAtStartupRequested;
    public event Action? ExitRequested;

    public TrayIconService(AppConfig initialConfig)
    {
        var contextMenu = new ContextMenuStrip();

        _statusMenuItem = new ToolStripMenuItem("Home Assistant: Disconnected")
        {
            Enabled = false
        };
        contextMenu.Items.Add(_statusMenuItem);

        _snoozeStatusItem = new ToolStripMenuItem("Alerts: Active")
        {
            Enabled = false
        };
        contextMenu.Items.Add(_snoozeStatusItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        _showCameraMenu = new ToolStripMenuItem("Show Camera Now (Test Popup)");
        _showCameraMenu.Font = new Font(_showCameraMenu.Font, FontStyle.Bold);
        _showCameraMenu.Click += (_, _) =>
        {
            if (_showCameraMenu.DropDownItems.Count == 0)
            {
                ShowCameraRequested?.Invoke(null);
            }
        };
        contextMenu.Items.Add(_showCameraMenu);

        var hideNowItem = new ToolStripMenuItem("Hide Popup");
        hideNowItem.Click += (_, _) => HideCameraRequested?.Invoke();
        contextMenu.Items.Add(hideNowItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var snoozeMenu = new ToolStripMenuItem("Snooze / Pause Popups");
        var snooze15 = new ToolStripMenuItem("Snooze for 15 Minutes");
        snooze15.Click += (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromMinutes(15));
        var snooze30 = new ToolStripMenuItem("Snooze for 30 Minutes");
        snooze30.Click += (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromMinutes(30));
        var snooze60 = new ToolStripMenuItem("Snooze for 1 Hour");
        snooze60.Click += (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromHours(1));
        var snoozeIndef = new ToolStripMenuItem("Pause Until Resumed");
        snoozeIndef.Click += (_, _) => SnoozeRequested?.Invoke(TimeSpan.MaxValue);
        var resumeItem = new ToolStripMenuItem("Resume Popups Now");
        resumeItem.Click += (_, _) => SnoozeRequested?.Invoke(null);

        snoozeMenu.DropDownItems.Add(snooze15);
        snoozeMenu.DropDownItems.Add(snooze30);
        snoozeMenu.DropDownItems.Add(snooze60);
        snoozeMenu.DropDownItems.Add(snoozeIndef);
        snoozeMenu.DropDownItems.Add(new ToolStripSeparator());
        snoozeMenu.DropDownItems.Add(resumeItem);
        contextMenu.Items.Add(snoozeMenu);

        contextMenu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();
        contextMenu.Items.Add(settingsItem);

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = initialConfig.RunAtStartup,
            CheckOnClick = true
        };
        _startWithWindowsItem.CheckedChanged += (_, _) =>
            ToggleRunAtStartupRequested?.Invoke(_startWithWindowsItem.Checked);
        contextMenu.Items.Add(_startWithWindowsItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit Derbell");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "Derbell — Nest Person Popup",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();

        SyncConfig(initialConfig);
        UpdateVisualState(_connectionState, null, _isSnoozed);
    }

    public void SyncConfig(AppConfig config)
    {
        if (_startWithWindowsItem.Checked != config.RunAtStartup)
        {
            _startWithWindowsItem.Checked = config.RunAtStartup;
        }

        _showCameraMenu.DropDownItems.Clear();
        IReadOnlyList<MonitoredCameraConfig> enabled = config.GetEnabledCameras();
        if (enabled.Count > 1)
        {
            foreach (MonitoredCameraConfig cam in enabled)
            {
                MonitoredCameraConfig captured = cam;
                var item = new ToolStripMenuItem(captured.DisplayLabel);
                item.Click += (_, _) => ShowCameraRequested?.Invoke(captured);
                _showCameraMenu.DropDownItems.Add(item);
            }
        }
    }

    public void UpdateVisualState(HaConnectionState state, string? detail, bool isSnoozed)
    {
        _connectionState = state;
        _isSnoozed = isSnoozed;

        string stateLabel = state switch
        {
            HaConnectionState.Connected => "Connected",
            HaConnectionState.Connecting => "Connecting...",
            HaConnectionState.AuthFailed => "Auth Failed (Check Token)",
            _ => "Disconnected"
        };

        _statusMenuItem.Text = $"Home Assistant: {stateLabel}";
        _snoozeStatusItem.Text = isSnoozed ? "Alerts: Snoozed / Paused" : "Alerts: Active (Nest camera_person)";

        string tooltip = $"Derbell — HA: {stateLabel}" + (isSnoozed ? " (Snoozed)" : "");
        if (tooltip.Length > 63)
        {
            tooltip = tooltip[..63];
        }
        _notifyIcon.Text = tooltip;

        Color badgeColor = isSnoozed
            ? Color.FromArgb(245, 158, 11)
            : state switch
            {
                HaConnectionState.Connected => Color.FromArgb(34, 197, 94),
                HaConnectionState.Connecting => Color.FromArgb(59, 130, 246),
                _ => Color.FromArgb(239, 68, 68)
            };

        SetTrayIcon(CreateCameraTrayIcon(badgeColor));
    }

    private void SetTrayIcon(Icon newIcon)
    {
        Icon? oldIcon = _notifyIcon.Icon;
        IntPtr oldHandle = _currentIconHandle;

        _notifyIcon.Icon = newIcon;
        _currentIconHandle = newIcon.Handle;

        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
        {
            DestroyIcon(oldHandle);
        }
    }

    private static Icon CreateCameraTrayIcon(Color statusColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var housingBrush = new SolidBrush(Color.FromArgb(30, 41, 59)))
            using (var housingPen = new Pen(Color.FromArgb(148, 163, 184), 2f))
            {
                g.FillEllipse(housingBrush, 2, 2, 26, 26);
                g.DrawEllipse(housingPen, 2, 2, 26, 26);
            }

            using (var lensBrush = new SolidBrush(Color.FromArgb(15, 23, 42)))
            using (var lensRing = new Pen(Color.FromArgb(56, 189, 248), 2f))
            {
                g.FillEllipse(lensBrush, 8, 8, 14, 14);
                g.DrawEllipse(lensRing, 8, 8, 14, 14);
            }

            using (var statusBrush = new SolidBrush(statusColor))
            using (var statusBorder = new Pen(Color.FromArgb(15, 23, 42), 2f))
            {
                g.FillEllipse(statusBrush, 19, 19, 11, 11);
                g.DrawEllipse(statusBorder, 19, 19, 11, 11);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
