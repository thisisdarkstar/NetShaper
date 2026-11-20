using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetShaper.Core;

public static class WinInetProxyManager
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;
    private const uint WM_SETTINGCHANGE = 0x001a;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static readonly object _lock = new();
    private static bool _isHooked;

    static WinInetProxyManager()
    {
        // Safe process exit hooks
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreDirectConnection();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreDirectConnection();
    }

    /// <summary>
    /// Checks on startup if a dead proxy was left behind by a previous application crash, and repairs it.
    /// </summary>
    public static bool SanitizeProxyOnStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false);
            if (key == null) return false;

            object? enabled = key.GetValue("ProxyEnable");
            object? server = key.GetValue("ProxyServer");

            if (enabled is int intVal && intVal == 1 && server is string serverStr)
            {
                if (serverStr.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverStr.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    // Previous run crashed while proxy was active. Restore direct routing now.
                    SetSystemProxy(false);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore access errors on startup
        }
        return false;
    }

    /// <summary>
    /// Enables or disables the system-wide WinINet SOCKS proxy in the Windows Registry and sets User Environment Variables.
    /// </summary>
    public static bool SetSystemProxy(bool enabled, int port = 1080)
    {
        lock (_lock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true);
                if (key == null) return false;

                if (enabled)
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", $"http=127.0.0.1:{port};https=127.0.0.1:{port};socks=127.0.0.1:{port}", RegistryValueKind.String);
                    key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                    _isHooked = true;

                    // Set User Environment Variables for curl, python, git, and game launchers
                    try
                    {
                        Environment.SetEnvironmentVariable("HTTP_PROXY", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("HTTPS_PROXY", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("http_proxy", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("https_proxy", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("NO_PROXY", "localhost,127.0.0.1,::1", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("no_proxy", "localhost,127.0.0.1,::1", EnvironmentVariableTarget.User);

                        // Also set in current process
                        Environment.SetEnvironmentVariable("HTTP_PROXY", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable("HTTPS_PROXY", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable("http_proxy", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable("https_proxy", $"http://127.0.0.1:{port}", EnvironmentVariableTarget.Process);
                    }
                    catch { }
                }
                else
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    _isHooked = false;

                    // Remove User Environment Variables
                    try
                    {
                        Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("ALL_PROXY", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("http_proxy", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("https_proxy", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("all_proxy", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("NO_PROXY", null, EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("no_proxy", null, EnvironmentVariableTarget.User);

                        Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.Process);
                    }
                    catch { }
                }

                // Notify Windows OS, Internet Explorer, Edge, and WinINet callers of the proxy change
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

                // Broadcast WM_SETTINGCHANGE so Explorer, CMD, PowerShell, and new processes pick up environment
                try
                {
                    SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 1000, out _);
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void RestoreDirectConnection()
    {
        if (_isHooked)
        {
            SetSystemProxy(false);
        }
    }

    public static bool IsProxyCurrentlyActive(out string? activeServer)
    {
        activeServer = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, false);
            if (key == null) return false;

            object? enabled = key.GetValue("ProxyEnable");
            if (enabled is int intVal && intVal == 1)
            {
                activeServer = key.GetValue("ProxyServer") as string;
                return true;
            }
        }
        catch { }
        return false;
    }
}
