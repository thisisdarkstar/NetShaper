using System.Diagnostics;
using System.IO;

namespace NetShaper.Core;

public static class AppLauncher
{
    public static bool LaunchThrottled(string exePath, int proxyPort, out string error)
    {
        error = string.Empty;
        if (!File.Exists(exePath))
        {
            error = $"Executable file not found: {exePath}";
            return false;
        }

        try
        {
            string fileName = Path.GetFileName(exePath).ToLowerInvariant();
            string procName = Path.GetFileNameWithoutExtension(exePath);

            // Apply hosts redirection for direct game/CDN downloads if applicable
            HostsRedirectManager.ApplyRedirectsForApp(fileName);

            // 1. Terminate any running unthrottled instances first so single-instance mutex does not abort the new throttled process
            try
            {
                var existing = Process.GetProcessesByName(procName);
                foreach (var p in existing)
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }

                // If target is Riot Client / RiotClientServices, cleanly stop all Riot companion processes
                if (fileName.Contains("riot"))
                {
                    foreach (var p in Process.GetProcesses().Where(pr => pr.ProcessName.ToLowerInvariant().Contains("riot")))
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }

            string workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDir,
                UseShellExecute = false
            };

            // Set standard HTTP/HTTPS proxy environment variables for libcurl, python, node, git, curl, etc.
            startInfo.EnvironmentVariables["HTTP_PROXY"] = $"http://127.0.0.1:{proxyPort}";
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = $"http://127.0.0.1:{proxyPort}";
            startInfo.EnvironmentVariables["http_proxy"] = $"http://127.0.0.1:{proxyPort}";
            startInfo.EnvironmentVariables["https_proxy"] = $"http://127.0.0.1:{proxyPort}";
            startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
            startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1,::1";

            // For Chromium-based browsers & Electron apps, pass proxy flags directly
            if (fileName.Contains("chrome") || fileName.Contains("edge") || fileName.Contains("brave") ||
                fileName.Contains("opera") || fileName.Contains("discord"))
            {
                if (fileName.Contains("chrome") || fileName.Contains("edge") || fileName.Contains("brave"))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), $"NetShaper_{Path.GetFileNameWithoutExtension(fileName)}");
                    Directory.CreateDirectory(tempDir);
                    startInfo.Arguments = $"--proxy-server=\"http://127.0.0.1:{proxyPort}\" --user-data-dir=\"{tempDir}\"";
                }
                else
                {
                    startInfo.Arguments = $"--proxy-server=\"http://127.0.0.1:{proxyPort}\"";
                }
            }
            else if (fileName.Contains("riotclientservices"))
            {
                // For RiotClientServices downloading Valorant: pass launch arguments if available
                startInfo.Arguments = "--launch-product=valorant --launch-patchline=live";
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
