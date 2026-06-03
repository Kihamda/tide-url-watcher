using System.Runtime.InteropServices;
using Tide.Core;

namespace Tide.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int OpenCommand = 1001;
    private const int RefreshCommand = 1002;
    private const int PauseCommand = 1003;
    private const int PauseUntilNextCommand = 1004;
    private const int SettingsCommand = 1005;
    private const int QuitCommand = 1006;

    private readonly TideLogger _logger;
    private readonly SubclassProc _subclassProc;
    private nint _ownerHwnd;
    private nint _icon;
    private bool _visible;
    private Snapshot _snapshot = new();
    private RefreshServiceState? _state;

    public TrayIconService(TideLogger logger)
    {
        _logger = logger;
        _subclassProc = WindowSubclassProc;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? PauseNotificationsRequested;
    public event EventHandler? PauseUntilNextRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

    public bool IsVisible => _visible;

    public void Initialize(nint ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        try
        {
            _icon = LoadIcon(0, 32512);
            SetWindowSubclass(_ownerHwnd, _subclassProc, IconId, 0);
            var data = CreateNotifyData(Tooltip());
            _visible = Shell_NotifyIcon(NimAdd, ref data);
            _logger.Info(_visible ? "Tray icon initialized." : "Tray icon initialization returned false.");
        }
        catch (Exception exception)
        {
            _logger.Warn("Tray icon initialization failed.", exception);
        }
    }

    public void Update(Snapshot snapshot, RefreshServiceState? state = null)
    {
        _snapshot = snapshot;
        _state = state ?? _state;
        if (!_visible)
        {
            return;
        }

        var data = CreateNotifyData(Tooltip());
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_ownerHwnd != 0)
        {
            try
            {
                var data = CreateNotifyData(string.Empty);
                Shell_NotifyIcon(NimDelete, ref data);
                RemoveWindowSubclass(_ownerHwnd, _subclassProc, IconId);
            }
            catch
            {
                // Shutdown cleanup is best-effort.
            }
        }

        _visible = false;
    }

    private nint WindowSubclassProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = (uint)lParam;
            if (mouseMessage == WmLButtonUp)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            if (mouseMessage == WmRButtonUp)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_ownerHwnd == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        AppendMenu(menu, MfString, OpenCommand, "Tideを開く");
        AppendMenu(menu, MfString, RefreshCommand, "今すぐ更新");
        AppendMenu(menu, MfString, PauseCommand, PauseLabel());
        AppendMenu(menu, MfString, PauseUntilNextCommand, "次回更新まで停止");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, SettingsCommand, "設定");
        AppendMenu(menu, MfString, QuitCommand, "終了");

        GetCursorPos(out var point);
        SetForegroundWindow(_ownerHwnd);
        var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _ownerHwnd, 0);
        DestroyMenu(menu);
        ExecuteCommand(command);
    }

    private void ExecuteCommand(int command)
    {
        switch (command)
        {
            case OpenCommand:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case RefreshCommand:
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PauseCommand:
                PauseNotificationsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PauseUntilNextCommand:
                PauseUntilNextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SettingsCommand:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case QuitCommand:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private NotifyIconData CreateNotifyData(string tooltip) =>
        new()
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _ownerHwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

    private string Tooltip()
    {
        var unread = _snapshot.Stories.Count(story => !story.IsRead);
        var failed = _snapshot.Sources.Count(source => source.ConsecutiveFailureCount > 0);
        var next = _state?.NextRefreshAt is null ? string.Empty : $" 次回 {_state.NextRefreshAt.Value.LocalDateTime:t}";
        var failure = failed == 0 ? string.Empty : $" / 失敗 {failed}";
        return $"Tide - 未読 {unread}{failure}{next}";
    }

    private string PauseLabel() =>
        _snapshot.Settings.PauseNotificationsUntil is null
            ? "通知を一時停止"
            : $"通知停止中 ({_snapshot.Settings.PauseNotificationsUntil.Value.LocalDateTime:t}まで)";

    private delegate nint SubclassProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, uint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nuint wParam, nint lParam);
}
