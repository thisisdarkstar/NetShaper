using System.Diagnostics;
using System.IO;

namespace NetShaper.Core;

/// <summary>
/// Manages temporary hosts file loopback entries for games/apps (such as Riot Client / Valorant)
/// that connect directly to CDN domains and ignore proxy settings.
/// Automatically cleans up on process exit or rule removal.
/// </summary>
public static class HostsRedirectManager
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    private const string StartTag = "# NETSHAPER START";
    private const string EndTag = "# NETSHAPER END";
    private static readonly object FileLock = new();

    // Known CDN hostnames for games and apps that bypass standard proxies
    private static readonly Dictionary<string, string[]> AppKnownDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["riotclientservices"] = new[]
        {
            "valorant.dyn.riotcdn.net",
            "cf.dyn.riotcdn.net.cdn.cloudflare.net"
        },
        ["riot client"] = new[]
        {
            "valorant.dyn.riotcdn.net",
            "cf.dyn.riotcdn.net.cdn.cloudflare.net"
        },
        ["valorant"] = new[]
        {
            "valorant.dyn.riotcdn.net",
            "cf.dyn.riotcdn.net.cdn.cloudflare.net"
        }
    };

    static HostsRedirectManager()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreHostsFile();
    }

    public static void ApplyRedirectsForApp(string appName)
    {
        string norm = Path.GetFileNameWithoutExtension(appName).ToLowerInvariant();
        List<string> domainsToRedirect = new();

        foreach (var (key, domains) in AppKnownDomains)
        {
            if (norm.Contains(key) || key.Contains(norm))
            {
                domainsToRedirect.AddRange(domains);
            }
        }

        if (domainsToRedirect.Count == 0) return;

        lock (FileLock)
        {
            try
            {
                if (!File.Exists(HostsPath)) return;

                string content = File.ReadAllText(HostsPath);
                string cleaned = RemoveExistingBlock(content);

                var sb = new System.Text.StringBuilder(cleaned.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine(StartTag);
                foreach (var d in domainsToRedirect.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"127.0.0.1 {d}");
                }
                sb.AppendLine(EndTag);

                File.WriteAllText(HostsPath, sb.ToString());
                FlushDns();
            }
            catch { }
        }
    }

    public static void RemoveRedirectsForApp(string appName)
    {
        string norm = Path.GetFileNameWithoutExtension(appName).ToLowerInvariant();
        bool isRiot = norm.Contains("riot") || norm.Contains("valorant");
        if (isRiot)
        {
            RestoreHostsFile();
        }
    }

    public static void RestoreHostsFile()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(HostsPath)) return;
                string content = File.ReadAllText(HostsPath);
                if (content.Contains(StartTag))
                {
                    string cleaned = RemoveExistingBlock(content);
                    File.WriteAllText(HostsPath, cleaned);
                    FlushDns();
                }
            }
            catch { }
        }
    }

    private static string RemoveExistingBlock(string text)
    {
        int startIdx = text.IndexOf(StartTag, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return text;

        int endIdx = text.IndexOf(EndTag, startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0)
        {
            return text.Substring(0, startIdx).TrimEnd() + Environment.NewLine;
        }

        endIdx += EndTag.Length;
        return (text.Substring(0, startIdx).TrimEnd() + Environment.NewLine + text.Substring(endIdx).TrimStart()).TrimEnd() + Environment.NewLine;
    }

    public static void FlushDns()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch { }
    }
}
