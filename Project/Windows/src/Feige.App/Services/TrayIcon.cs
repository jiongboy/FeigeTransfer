using System.Runtime.InteropServices;

namespace Feige.App.Services;

/// <summary>
/// Notification-area icon. WinUI has no tray API, so this owns a message-only window on the UI thread
/// and talks to Shell_NotifyIcon directly. Left click shows the window; right click opens a small menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B, WmNull = 0;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint MfString = 0, MfGrayed = 1, MfSeparator = 0x800;
    private const uint TpmRightButton = 2, TpmReturnCommand = 0x100;
    private const int CommandShow = 1, CommandExit = 2;
    private static readonly nint HwndMessage = -3;

    private readonly Action _show;
    private readonly Action _exit;
    private readonly WndProc _wndProc;
    private readonly string _className = "FeigeTray" + Environment.ProcessId;
    private readonly uint _taskbarCreated;
    private readonly nint _window;
    private readonly nint _icon;
    private string _tip = "飞鸽快传";
    private int _unread;
    private bool _added;

    public TrayIcon(string iconPath, Action show, Action exit)
    {
        _show = show;
        _exit = exit;
        _wndProc = WindowProc;
        nint instance = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref windowClass) == 0) throw new InvalidOperationException("tray class " + Marshal.GetLastWin32Error());
        _window = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        if (_window == 0) throw new InvalidOperationException("tray window " + Marshal.GetLastWin32Error());
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadImageW(0, iconPath, 1, 16, 16, 0x10);
        Add();
    }

    /// <summary>Updates the tooltip with the unread total.</summary>
    public void SetUnread(int count)
    {
        _unread = count;
        _tip = count > 0 ? $"飞鸽快传 · {count} 条未读" : "飞鸽快传";
        Update(NimModify);
    }

    private void Add() => _added = Update(NimAdd);

    private bool Update(uint action)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tip,
        };
        return Shell_NotifyIconW(action, ref data);
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            uint mouse = (uint)(lParam & 0xffff);
            if (mouse == WmLButtonUp) _show();
            else if (mouse is WmRButtonUp or WmContextMenu) ShowMenu();
            return 0;
        }
        if (message == _taskbarCreated && _taskbarCreated != 0) Add(); // Explorer restarted.
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MfString | MfGrayed, 0, _unread > 0 ? $"{_unread} 条未读消息" : "没有未读消息");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CommandShow, "显示主窗口");
            AppendMenuW(menu, MfString, CommandExit, "退出飞鸽快传");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            int command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand, point.X, point.Y, 0, _window, 0);
            PostMessageW(_window, WmNull, 0, 0);
            if (command == CommandShow) _show();
            else if (command == CommandExit) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added) Update(NimDelete);
        _added = false;
        if (_icon != 0) DestroyIcon(_icon);
        if (_window != 0) DestroyWindow(_window);
        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
