using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NetShaper.Core;
using NetShaper.Models;

namespace NetShaper.Views;

public partial class SocketsInspectorWindow : Window
{
    private readonly string _appName;
    private readonly Socks5ProxyServer _proxyServer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCollection<ActiveSocketInfo> _sockets = new();

    private Dictionary<string, (long DL, long UL)> _prevSample = new();

    public SocketsInspectorWindow(Window owner, string appName, Socks5ProxyServer proxyServer)
    {
        InitializeComponent();
        Owner = owner;
        _appName = appName;
        _proxyServer = proxyServer;

        Title = $"Network Sockets Inspector: {Path.GetFileName(_appName)}";
        TitleLabel.Text = $"ACTIVE NET-ROUTES: {Path.GetFileName(_appName).ToUpperInvariant()}";
        SocketsGrid.ItemsSource = _sockets;

        Loaded += (_, _) => _ = RefreshLoopAsync(_cts.Token);
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);

                // Collect proxy active connections matching this app
                var currentProxy = _proxyServer.ActiveConnections.ToList();
                string targetBase = Path.GetFileNameWithoutExtension(_appName).ToLowerInvariant();

                var rows = new List<ActiveSocketInfo>();
                var matchedPorts = new HashSet<int>();

                foreach (var (connId, stats) in currentProxy)
                {
                    string statsApp = Path.GetFileNameWithoutExtension(stats.AppName).ToLowerInvariant();
                    if (statsApp != targetBase && !statsApp.Contains(targetBase) && !targetBase.Contains(statsApp))
                        continue;

                    matchedPorts.Add(stats.SourcePort);

                    long prevDl = 0, prevUl = 0;
                    if (_prevSample.TryGetValue(connId, out var prev))
                    {
                        prevDl = prev.DL;
                        prevUl = prev.UL;
                    }

                    long speedDl = Math.Max(0, stats.BytesDl - prevDl);
                    long speedUl = Math.Max(0, stats.BytesUl - prevUl);

                    var elapsed = DateTime.UtcNow - stats.StartTime;
                    string elapsedStr = elapsed.TotalMinutes >= 1
                        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                        : $"{elapsed.Seconds}s";

                    string sourceLbl = connId.Contains("->") ? connId.Split("->")[0] : $"127.0.0.1:{stats.SourcePort}";

                    rows.Add(new ActiveSocketInfo
                    {
                        RouteSource = sourceLbl,
                        TargetAddress = stats.TargetAddress,
                        LocalPort = stats.SourcePort,
                        LiveDlSpeed = ShaperRule.FormatBytesPerSec(speedDl),
                        LiveUlSpeed = ShaperRule.FormatBytesPerSec(speedUl),
                        ActiveDuration = elapsedStr,
                        IsShaped = true
                    });
                }

                // Update previous sample
                _prevSample = currentProxy.ToDictionary(k => k.Key, v => (v.Value.BytesDl, v.Value.BytesUl));

                // Query direct unshaped OS TCP connections for this app via iphlpapi
                var allConns = NetworkMonitor.GetActiveTcpConnections();
                int targetPid = 0;
                try
                {
                    var procs = Process.GetProcessesByName(targetBase);
                    if (procs.Length > 0) targetPid = procs[0].Id;
                }
                catch { }

                foreach (var conn in allConns)
                {
                    if (matchedPorts.Contains(conn.LocalEndPoint.Port)) continue;

                    bool belongsToApp = false;
                    if (targetPid > 0 && conn.OwningPid == targetPid)
                    {
                        belongsToApp = true;
                    }
                    else if (conn.OwningPid > 0)
                    {
                        try
                        {
                            using var p = Process.GetProcessById(conn.OwningPid);
                            if (p.ProcessName.Equals(targetBase, StringComparison.OrdinalIgnoreCase))
                                belongsToApp = true;
                        }
                        catch { }
                    }

                    if (belongsToApp)
                    {
                        rows.Add(new ActiveSocketInfo
                        {
                            RouteSource = conn.LocalEndPoint.ToString(),
                            TargetAddress = conn.RemoteEndPoint.Port > 0 ? conn.RemoteEndPoint.ToString() : "LISTENING",
                            LocalPort = conn.LocalEndPoint.Port,
                            LiveDlSpeed = "Direct",
                            LiveUlSpeed = "Direct",
                            ActiveDuration = "-",
                            IsShaped = false
                        });
                    }
                }

                // Dispatch to UI smoothly
                Dispatcher.Invoke(() =>
                {
                    _sockets.Clear();
                    foreach (var r in rows) _sockets.Add(r);
                    StatusLabel.Text = $"Active sockets: {_sockets.Count} online | {rows.Count(r => r.IsShaped)} shaped through proxy.";
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient query errors
            }
        }
    }
}
