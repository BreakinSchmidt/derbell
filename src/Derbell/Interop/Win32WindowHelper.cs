using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Derbell.Interop;

public static class Win32WindowHelper
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Applies gaming-safe non-activating, toolwindow, and topmost extended styles to the window.
    /// </summary>
    public static void ApplyNonActivatingOverlayStyle(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;

        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Positions and shows the window on top without activating or stealing focus/cursor.
    /// </summary>
    public static void ShowTopmostWithoutActivation(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;

        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Hides the window without altering foreground window focus.
    /// </summary>
    public static void HideWithoutActivation(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>
    /// Parses a human-readable hotkey string such as "Ctrl+Shift+X" or "Alt+F9" into Win32 modifiers and virtual key code.
    /// </summary>
    public static bool TryParseHotkey(string hotkeySpec, out uint modifiers, out uint virtualKey)
    {
        modifiers = MOD_NOREPEAT;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeySpec))
            return false;

        string[] parts = hotkeySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string mod = parts[i].ToUpperInvariant();
            switch (mod)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        string keyPart = parts[^1];
        if (Enum.TryParse<Key>(keyPart, ignoreCase: true, out Key wpfKey))
        {
            int vk = KeyInterop.VirtualKeyFromKey(wpfKey);
            if (vk > 0)
            {
                virtualKey = (uint)vk;
                return true;
            }
        }

        return false;
    }
}
