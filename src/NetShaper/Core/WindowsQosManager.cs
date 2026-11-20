using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace NetShaper.Core;

/// <summary>
/// Manages Windows Kernel Policy-Based QoS (Quality of Service) policies via PowerShell/WMI.
/// Provides OS-level bandwidth throttling for direct TCP connections (like download managers).
/// </summary>
public static class WindowsQosManager
{
    static WindowsQosManager()
    {
        EnsureQosEnabled();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAllPolicies();
    }

    public static void EnsureQosEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\QoS");
            key?.SetValue("Do not use NLA", "1", RegistryValueKind.String);
        }
        catch { }
    }

    public static void ApplyPolicy(string appName, long limitBytesPerSec)
    {
        if (limitBytesPerSec <= 0) return;

        string exe = Path.GetFileName(appName).Trim().ToLowerInvariant();
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe += ".exe";

        string policyName = $"NetShaper_{Path.GetFileNameWithoutExtension(exe)}";
        long bitsPerSec = limitBytesPerSec * 8;

        Task.Run(() =>
        {
            try
            {
                // Remove existing policy first
                RunPowerShell($"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue");
                // Add new policy in ActiveStore
                RunPowerShell($"New-NetQosPolicy -Name '{policyName}' -AppPathNameMatchCondition '{exe}' -ThrottleRateActionBitsPerSecond {bitsPerSec} -PolicyStore ActiveStore -ErrorAction SilentlyContinue");
            }
            catch
            {
                // Ignore if permissions or feature not supported
            }
        });
    }

    public static void RemovePolicy(string appName)
    {
        string exe = Path.GetFileName(appName).Trim().ToLowerInvariant();
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe += ".exe";

        string policyName = $"NetShaper_{Path.GetFileNameWithoutExtension(exe)}";

        Task.Run(() =>
        {
            try
            {
                RunPowerShell($"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue");
            }
            catch { }
        });
    }

    public static void RemoveAllPolicies()
    {
        try
        {
            RunPowerShell("Get-NetQosPolicy | Where-Object { $_.Name -like 'NetShaper_*' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue");
        }
        catch { }
    }

    private static void RunPowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }
}
