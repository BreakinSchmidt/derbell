using System.Drawing;
using System.Windows.Forms;
using Derbell.Models;

namespace Derbell.Interop;

public sealed record MonitorInfo(
    string DeviceName,
    string DisplayLabel,
    bool IsPrimary,
    Rectangle Bounds,
    Rectangle WorkingArea);

public static class MonitorHelper
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        Screen[] screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);

        for (int i = 0; i < screens.Length; i++)
        {
            Screen s = screens[i];
            string primaryTag = s.Primary ? " (Primary)" : string.Empty;
            string label = $"Monitor {i + 1}: {s.Bounds.Width}x{s.Bounds.Height}{primaryTag} [{s.DeviceName}]";
            list.Add(new MonitorInfo(
                s.DeviceName,
                label,
                s.Primary,
                s.Bounds,
                s.WorkingArea));
        }

        return list;
    }

    public static MonitorInfo GetTargetMonitor(string? targetDeviceName, IReadOnlyList<MonitorInfo>? monitors = null)
    {
        IReadOnlyList<MonitorInfo> all = monitors ?? GetMonitors();
        if (all.Count == 0)
        {
            var fallbackRect = new Rectangle(0, 0, 1920, 1080);
            return new MonitorInfo(@"\\.\DISPLAY1", "Default Display", true, fallbackRect, fallbackRect);
        }

        if (!string.IsNullOrWhiteSpace(targetDeviceName))
        {
            MonitorInfo? match = all.FirstOrDefault(m =>
                string.Equals(m.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    /// <summary>
    /// Computes the physical screen pixel rectangle for the popup on the specified monitor.
    /// </summary>
    public static Rectangle CalculatePopupBounds(AppConfig config, MonitorInfo monitor)
    {
        int width = Math.Clamp(config.Width, 200, Math.Max(200, monitor.Bounds.Width));
        int height = Math.Clamp(config.Height, 120, Math.Max(120, monitor.Bounds.Height));
        int margin = Math.Max(0, config.EdgeMargin);

        Rectangle area = monitor.WorkingArea.Width > 0 && monitor.WorkingArea.Height > 0
            ? monitor.WorkingArea
            : monitor.Bounds;

        int x;
        int y;

        switch (config.PositionPreset)
        {
            case PopupPositionPreset.TopLeft:
                x = area.Left + margin;
                y = area.Top + margin;
                break;

            case PopupPositionPreset.BottomLeft:
                x = area.Left + margin;
                y = area.Bottom - height - margin;
                break;

            case PopupPositionPreset.BottomRight:
                x = area.Right - width - margin;
                y = area.Bottom - height - margin;
                break;

            case PopupPositionPreset.Custom:
                x = monitor.Bounds.Left + config.CustomX;
                y = monitor.Bounds.Top + config.CustomY;
                break;

            case PopupPositionPreset.TopRight:
            default:
                x = area.Right - width - margin;
                y = area.Top + margin;
                break;
        }

        // Clamp inside the selected monitor bounds so it can never spawn off-screen
        int minX = monitor.Bounds.Left;
        int maxX = Math.Max(minX, monitor.Bounds.Right - width);
        int minY = monitor.Bounds.Top;
        int maxY = Math.Max(minY, monitor.Bounds.Bottom - height);

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        return new Rectangle(x, y, width, height);
    }
}
