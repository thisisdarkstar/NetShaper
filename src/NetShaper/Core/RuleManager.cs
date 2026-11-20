using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using NetShaper.Models;

namespace NetShaper.Core;

public class RuleManager
{
    private readonly string _rulesFilePath;
    private readonly object _fileLock = new();
    private readonly ConcurrentDictionary<string, ShaperRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RulesChanged;

    public RuleManager()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "NetShaper");
        Directory.CreateDirectory(folder);
        _rulesFilePath = Path.Combine(folder, "shaper_rules.json");
        LoadRules();
    }

    public IReadOnlyDictionary<string, ShaperRule> Rules => _rules;

    public void LoadRules()
    {
        lock (_fileLock)
        {
            _rules.Clear();
            if (!File.Exists(_rulesFilePath)) return;

            try
            {
                string json = File.ReadAllText(_rulesFilePath);
                var doc = JsonDocument.Parse(json);

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string appKey = prop.Name;
                    var rule = new ShaperRule();

                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (prop.Value.TryGetProperty("DL", out var dlProp))
                            rule.DlLimit = dlProp.GetString() ?? "Unlimited";
                        if (prop.Value.TryGetProperty("UL", out var ulProp))
                            rule.UlLimit = ulProp.GetString() ?? "Unlimited";
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        // Upgrade legacy single-string rule format
                        rule.DlLimit = prop.Value.GetString() ?? "Unlimited";
                        rule.UlLimit = "Unlimited";
                    }

                    _rules[appKey] = rule;
                }
            }
            catch
            {
                // Ignore parse errors on corrupt file
            }
        }
    }

    public void SaveRules()
    {
        lock (_fileLock)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_rules, options);
                File.WriteAllText(_rulesFilePath, json);
            }
            catch
            {
                // Ignore write errors
            }
        }
        RulesChanged?.Invoke();
    }

    public void SetRule(string targetApp, string dlLimit, string ulLimit)
    {
        string key = targetApp.Trim().ToLowerInvariant();
        if (!key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key += ".exe";

        _rules[key] = new ShaperRule
        {
            DlLimit = string.IsNullOrWhiteSpace(dlLimit) ? "Unlimited" : dlLimit,
            UlLimit = string.IsNullOrWhiteSpace(ulLimit) ? "Unlimited" : ulLimit
        };

        SaveRules();
    }

    public bool RemoveRule(string appName)
    {
        string norm = Path.GetFileName(appName).Trim().ToLowerInvariant();
        var keysToRemove = _rules.Keys.Where(k => Path.GetFileName(k).Equals(norm, StringComparison.OrdinalIgnoreCase)).ToList();

        bool anyRemoved = false;
        foreach (var k in keysToRemove)
        {
            if (_rules.TryRemove(k, out _))
                anyRemoved = true;
        }

        if (anyRemoved)
        {
            SaveRules();
        }
        return anyRemoved;
    }

    public (long DlBytes, long UlBytes) GetAppliedLimits(string appName)
    {
        string norm = Path.GetFileName(appName).Trim().ToLowerInvariant();

        foreach (var (target, rule) in _rules)
        {
            string targetBase = Path.GetFileName(target).ToLowerInvariant();
            if (targetBase == norm || norm.Contains(targetBase) || targetBase.Contains(norm))
            {
                return (rule.DlBytesPerSec, rule.UlBytesPerSec);
            }
        }

        return (0, 0);
    }
}
