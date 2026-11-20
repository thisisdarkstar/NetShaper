using System.Text.Json.Serialization;

namespace NetShaper.Models;

public class ShaperRule
{
    [JsonPropertyName("DL")]
    public string DlLimit { get; set; } = "Unlimited";

    [JsonPropertyName("UL")]
    public string UlLimit { get; set; } = "Unlimited";

    [JsonIgnore]
    public long DlBytesPerSec => ParseLimitToBytes(DlLimit);

    [JsonIgnore]
    public long UlBytesPerSec => ParseLimitToBytes(UlLimit);

    public static long ParseLimitToBytes(string limitStr)
    {
        if (string.IsNullOrWhiteSpace(limitStr) || limitStr.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
            return 0;

        try
        {
            string s = limitStr.Trim();
            if (s.EndsWith("MB/s", StringComparison.OrdinalIgnoreCase))
            {
                double val = double.Parse(s[..^4].Trim());
                return (long)(val * 1024 * 1024);
            }
            if (s.EndsWith("KB/s", StringComparison.OrdinalIgnoreCase))
            {
                double val = double.Parse(s[..^4].Trim());
                return (long)(val * 1024);
            }
            if (s.EndsWith("Mbps", StringComparison.OrdinalIgnoreCase))
            {
                double val = double.Parse(s[..^4].Trim());
                return (long)(val * 1_000_000 / 8);
            }
            if (s.EndsWith("Kbps", StringComparison.OrdinalIgnoreCase))
            {
                double val = double.Parse(s[..^4].Trim());
                return (long)(val * 1_000 / 8);
            }
        }
        catch
        {
            // Ignore parse errors, default to 0 (unlimited)
        }
        return 0;
    }

    public static string FormatBytesPerSec(long bytes)
    {
        if (bytes <= 0) return "Unlimited";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB/s";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB/s";
        return $"{bytes} B/s";
    }
}
