using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetShaper.Models;

public class ActiveSocketInfo : INotifyPropertyChanged
{
    private string _routeSource = string.Empty;
    private string _targetAddress = string.Empty;
    private int _localPort;
    private string _liveDlSpeed = "0 B/s";
    private string _liveUlSpeed = "0 B/s";
    private string _activeDuration = "-";
    private bool _isShaped;

    public string RouteSource
    {
        get => _routeSource;
        set => SetField(ref _routeSource, value);
    }

    public string TargetAddress
    {
        get => _targetAddress;
        set => SetField(ref _targetAddress, value);
    }

    public int LocalPort
    {
        get => _localPort;
        set => SetField(ref _localPort, value);
    }

    public string LiveDlSpeed
    {
        get => _liveDlSpeed;
        set => SetField(ref _liveDlSpeed, value);
    }

    public string LiveUlSpeed
    {
        get => _liveUlSpeed;
        set => SetField(ref _liveUlSpeed, value);
    }

    public string ActiveDuration
    {
        get => _activeDuration;
        set => SetField(ref _activeDuration, value);
    }

    public bool IsShaped
    {
        get => _isShaped;
        set => SetField(ref _isShaped, value);
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
