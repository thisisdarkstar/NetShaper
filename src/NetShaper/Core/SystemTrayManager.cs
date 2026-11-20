using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace NetShaper.Core;

/// <summary>
/// Manages native Windows System Tray (Taskbar Notification Area) icon and interactions via Shell_NotifyIcon.
/// Keeps NetShaper accessible while active in background without any WinForms dependencies.
/// </summary>
public class SystemTrayManager : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 102;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Window _window;
    private IntPtr _hwnd;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _isAdded;
    private ContextMenu? _contextMenu;

    public event Action? OpenWindowRequested;
    public event Action? ToggleInterceptionRequested;
    public event Action? ExitRequested;

    public bool IsVisible => _isAdded;

    public SystemTrayManager(Window window)
    {
        _window = window;
    }

    public void Initialize()
    {
        var helper = new WindowInteropHelper(_window);
        _hwnd = helper.EnsureHandle();

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        // Load application icon directly from executable or standard system application icon
        try
        {
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                ExtractIconEx(exePath, 0, out _, out _hIcon, 1);
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            }
        }
        catch { }

        BuildContextMenu();
    }

    public void Show(string tooltip = "NetShaper - Bandwidth Controller (Active)")
    {
        if (_hwnd == IntPtr.Zero) Initialize();

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

        if (!_isAdded)
        {
            _isAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_isAdded) return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Hide()
    {
        if (!_isAdded) return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _isAdded = false;
    }

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenu
        {
            Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#111115")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1D1D22")),
            BorderThickness = new Thickness(1),
            FontSize = 12
        };

        var openItem = new MenuItem
        {
            Header = "⚡ Open NetShaper",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00F0FF"))
        };
        openItem.Click += (_, _) => OpenWindowRequested?.Invoke();

        var toggleItem = new MenuItem
        {
            Header = "🟢 Toggle Traffic Interception",
            Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00FF88"))
        };
        toggleItem.Click += (_, _) => ToggleInterceptionRequested?.Invoke();

        var exitItem = new MenuItem
        {
            Header = "❌ Exit NetShaper",
            Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF375F"))
        };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(toggleItem);
        _contextMenu.Items.Add(new Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1D1D22")) });
        _contextMenu.Items.Add(exitItem);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int action = lParam.ToInt32();
            if (action == WM_LBUTTONUP || action == WM_LBUTTONDBLCLK)
            {
                OpenWindowRequested?.Invoke();
                handled = true;
            }
            else if (action == WM_RBUTTONUP)
            {
                if (_contextMenu != null)
                {
                    SetForegroundWindow(_hwnd);
                    _contextMenu.IsOpen = true;
                }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Hide();
        if (_hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_hIcon); } catch { }
            _hIcon = IntPtr.Zero;
        }
    }
}
