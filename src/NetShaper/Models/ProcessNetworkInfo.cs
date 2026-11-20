using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetShaper.Models;

public class ProcessNetworkInfo : INotifyPropertyChanged
{
    private string _instances = "1";
    private string _name = string.Empty;
    private int _connsCount;
    private string _connsDisplay = "Idle";
    private double _speedBytes;
    private string _speedDisplay = "0 B/s";
    private string _dlLimit = "Unlimited";
    private string _ulLimit = "Unlimited";
    private string _executablePath = string.Empty;
    private bool _hasActiveSockets;
    private bool _hasRule;

    public string Instances
    {
        get => _instances;
        set => SetField(ref _instances, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public int ConnsCount
    {
        get => _connsCount;
        set
        {
            if (SetField(ref _connsCount, value))
            {
                ConnsDisplay = value > 0 ? $"{value} Active" : "Idle";
                HasActiveSockets = value > 0;
            }
        }
    }

    public string ConnsDisplay
    {
        get => _connsDisplay;
        set => SetField(ref _connsDisplay, value);
    }

    public double SpeedBytes
    {
        get => _speedBytes;
        set
        {
            if (SetField(ref _speedBytes, value))
            {
                SpeedDisplay = FormatSpeed(value);
            }
        }
    }

    public string SpeedDisplay
    {
        get => _speedDisplay;
        set => SetField(ref _speedDisplay, value);
    }

    public string DlLimit
    {
        get => _dlLimit;
        set
        {
            if (SetField(ref _dlLimit, value))
            {
                UpdateHasRule();
            }
        }
    }

    public string UlLimit
    {
        get => _ulLimit;
        set
        {
            if (SetField(ref _ulLimit, value))
            {
                UpdateHasRule();
            }
        }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetField(ref _executablePath, value);
    }

    public bool HasActiveSockets
    {
        get => _hasActiveSockets;
        set => SetField(ref _hasActiveSockets, value);
    }

    public bool HasRule
    {
        get => _hasRule;
        set => SetField(ref _hasRule, value);
    }

    private void UpdateHasRule()
    {
        HasRule = (!string.IsNullOrEmpty(DlLimit) && !DlLimit.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) ||
                  (!string.IsNullOrEmpty(UlLimit) && !UlLimit.Equals("Unlimited", StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        if (bytesPerSec > 0)
            return $"{bytesPerSec:F0} B/s";
        return "0 B/s";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
