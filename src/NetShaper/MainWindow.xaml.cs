using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NetShaper.Core;
using NetShaper.Models;
using NetShaper.Views;

namespace NetShaper;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    private readonly RuleManager _ruleManager = new();
    private readonly BandwidthRegulatorRegistry _regulators = new();
    private readonly Socks5ProxyServer _proxyServer;
    private readonly TransparentSniInterceptor _sniInterceptor;
    private readonly EtwNetworkMonitor _etwMonitor = new();
    private readonly SystemTrayManager _trayManager;
    private readonly CancellationTokenSource _cts = new();
    private bool _isExplicitExit;

    private readonly ObservableCollection<ProcessNetworkInfo> _processes = new();
    private readonly ObservableCollection<RuleDisplayItem> _rulesDisplay = new();

    // Cache process paths to avoid repeated, slow, blocking Win32 MainModule queries
    private static readonly ConcurrentDictionary<string, string> _exePathCache = new(StringComparer.OrdinalIgnoreCase);

    // Live speed rate delta tracking across ETW Kernel, SOCKS5 Proxy, and Win32 I/O
    private readonly ConcurrentDictionary<int, long> _prevEtwBytes = new();
    private readonly ConcurrentDictionary<string, (long DL, long UL)> _prevProxyAppBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ulong> _prevIoBytes = new();
    private long _lastMetricsTimestamp = Stopwatch.GetTimestamp();

    public record RuleDisplayItem(string App, string DlLimit, string UlLimit);

    public MainWindow()
    {
        InitializeComponent();

        _proxyServer = new Socks5ProxyServer(_ruleManager, _regulators);
        _proxyServer.LogMessage += OnLogReceived;
        _sniInterceptor = new TransparentSniInterceptor(_ruleManager, _regulators);
        _sniInterceptor.LogMessage += OnLogReceived;
        _etwMonitor.LogMessage += OnLogReceived;
        _trayManager = new SystemTrayManager(this);

        ProcessGrid.ItemsSource = _processes;
        RulesGrid.ItemsSource = _rulesDisplay;

        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
        StateChanged += OnWindowStateChanged;
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 1. Initialize and show System Tray icon
        _trayManager.Initialize();
        _trayManager.Show("NetShaper - Bandwidth Controller (Ready)");
        _trayManager.OpenWindowRequested += ShowAndRestoreWindow;
        _trayManager.ToggleInterceptionRequested += ToggleInterceptionFromTray;
        _trayManager.ExitRequested += ExitApplicationCleanly;

        // 2. Start SOCKS5 Proxy Engine
        if (_proxyServer.Start())
        {
            StatusSocksLabel.Text = $"SOCKS5 ENGINE ACTIVE: PORT {_proxyServer.Port}";
            Log($"SOCKS5 Proxy Engine active on 127.0.0.1:{_proxyServer.Port}");
        }
        else
        {
            StatusSocksLabel.Text = "SOCKS5 ENGINE ERROR";
            StatusSocksLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF375F"));
            Log("CRITICAL: SOCKS5 Proxy Throttling Engine failed to bind port!");
        }

        // 3. Start OS-wide ETW Kernel Network Monitor & Transparent SNI Interceptor
        _sniInterceptor.Start();
        _etwMonitor.Start();

        // 4. Check if proxy was already enabled
        if (WinInetProxyManager.IsProxyCurrentlyActive(out _))
        {
            ProxyToggle.IsChecked = true;
            UpdateProxyVisuals(true);
        }

        // 5. Refresh Rules Display
        RefreshRulesDisplay();
        _ruleManager.RulesChanged += () => Dispatcher.Invoke(RefreshRulesDisplay);

        // 6. Initial filter setup
        ApplyFilter();

        // 7. Start Process Metrics background loop
        _ = RunMetricsLoopAsync(_cts.Token);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && ProxyToggle.IsChecked == true)
        {
            Hide();
        }
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit && ProxyToggle.IsChecked == true)
        {
            // Minimize to system tray instead of exiting when actively intercepting/shaping traffic
            e.Cancel = true;
            Hide();
            _trayManager.UpdateTooltip($"NetShaper - Active in Background (Port {_proxyServer.Port})");
            ToastNotification.Show(this, "Active in System Tray", "NetShaper is actively shaping traffic in the background.", "#00F0FF");
            return;
        }

        _trayManager.Dispose();
        _cts.Cancel();
        _sniInterceptor.Stop();
        _etwMonitor.Stop();
        _proxyServer.Stop();
        WinInetProxyManager.RestoreDirectConnection();
        HostsRedirectManager.RestoreHostsFile();
        WindowsQosManager.RemoveAllPolicies();
    }

    private void ShowAndRestoreWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Focus();
        });
    }

    private void ToggleInterceptionFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ProxyToggle.IsChecked = !ProxyToggle.IsChecked;
        });
    }

    private void ExitApplicationCleanly()
    {
        Dispatcher.Invoke(() =>
        {
            _isExplicitExit = true;
            Close();
        });
    }

    private void Log(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ConsoleLogBox.AppendText($">> {message}\n");
            ConsoleLogBox.ScrollToEnd();
        });
    }

    private void OnLogReceived(string msg) => Log(msg);

    #region Proxy Global Interception

    private void OnProxyToggleChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
    {
        bool enable = e.NewValue;
        if (enable)
        {
            bool success = WinInetProxyManager.SetSystemProxy(true, _proxyServer.Port);
            if (success)
            {
                UpdateProxyVisuals(true);
                Log($"GLOBAL INTERCEPTOR: Rerouted Windows traffic through local proxy port {_proxyServer.Port}.");
                ToastNotification.Show(this, "Proxy Enabled", "Windows network is now routed through Shaper Proxy!", "#00FF88");
            }
            else
            {
                ProxyToggle.IsChecked = false;
                UpdateProxyVisuals(false);
                ToastNotification.Show(this, "Registry Error", "Failed to modify Windows proxy keys.", "#FF375F");
            }
        }
        else
        {
            WinInetProxyManager.SetSystemProxy(false);
            UpdateProxyVisuals(false);
            Log("GLOBAL INTERCEPTOR: Interception disabled. Direct connection routes restored.");
            ToastNotification.Show(this, "Proxy Disabled", "Direct route connections restored.", "#828292");
        }
    }

    private void UpdateProxyVisuals(bool active)
    {
        if (active)
        {
            GlowDot.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"));
            InterceptStateLabel.Text = "ACTIVE";
            InterceptStateLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"));
            StatusLabel.Text = "⚡ Traffic Interceptor active. Monitoring sockets.";
            _trayManager.UpdateTooltip($"NetShaper - Traffic Interceptor ACTIVE (Port {_proxyServer.Port})");
        }
        else
        {
            GlowDot.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#828292"));
            InterceptStateLabel.Text = "INACTIVE";
            InterceptStateLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#828292"));
            StatusLabel.Text = "🟢 Engine Ready. Direct routing active.";
            _trayManager.UpdateTooltip("NetShaper - Bandwidth Controller (Ready)");
        }
    }

    #endregion

    #region Policy Form & Rules DB

    private void RefreshRulesDisplay()
    {
        _rulesDisplay.Clear();
        foreach (var (app, rule) in _ruleManager.Rules)
        {
            _rulesDisplay.Add(new RuleDisplayItem(Path.GetFileName(app), rule.DlLimit, rule.UlLimit));
        }
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select target executable application",
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            ExeEntry.Text = dlg.FileName;
        }
    }

    private void OnLaunchThrottledClicked(object sender, RoutedEventArgs e)
    {
        string path = ExeEntry.Text.Trim();
        if (!File.Exists(path))
        {
            // Attempt to resolve from cache or active running processes
            string procName = Path.GetFileNameWithoutExtension(path);
            if (_exePathCache.TryGetValue(procName, out string? cached) && File.Exists(cached))
            {
                path = cached;
            }
            else
            {
                try
                {
                    var procs = Process.GetProcessesByName(procName);
                    if (procs.Length > 0 && procs[0].MainModule?.FileName is string fn && File.Exists(fn))
                    {
                        path = fn;
                        _exePathCache[procName] = fn;
                    }
                }
                catch { }
            }
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select application to launch throttled",
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
            ExeEntry.Text = path;
        }

        if (AppLauncher.LaunchThrottled(path, _proxyServer.Port, out string err))
        {
            string name = Path.GetFileName(path);
            Log($"THROTTLED LAUNCH: Process '{name}' started with full SOCKS/HTTP proxy shaper routing.");
            ToastNotification.Show(this, "App Route Active", $"Launched {name} through shaper port successfully!", "#0A84FF");
        }
        else
        {
            ToastNotification.Show(this, "Launch Error", err, "#FF375F");
        }
    }

    private void OnLimitCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        DlValEntry.IsEnabled = DlCheckBox.IsChecked == true;
        DlUnitCombo.IsEnabled = DlCheckBox.IsChecked == true;
        UlValEntry.IsEnabled = UlCheckBox.IsChecked == true;
        UlUnitCombo.IsEnabled = UlCheckBox.IsChecked == true;
    }

    private void OnApplyPolicyClicked(object sender, RoutedEventArgs e)
    {
        string exe = ExeEntry.Text.Trim();
        if (string.IsNullOrEmpty(exe))
        {
            ToastNotification.Show(this, "Incomplete Form", "Application executable name or path required.", "#FF375F");
            return;
        }

        string dlCap = "Unlimited";
        string ulCap = "Unlimited";

        if (DlCheckBox.IsChecked == true)
        {
            if (!double.TryParse(DlValEntry.Text.Trim(), out double dlVal) || dlVal <= 0)
            {
                ToastNotification.Show(this, "Invalid Value", "Download limit rate must be a positive number.", "#FF375F");
                return;
            }
            string unit = ((ComboBoxItem)DlUnitCombo.SelectedItem)?.Content?.ToString() ?? "MB/s";
            dlCap = $"{dlVal} {unit}";
        }

        if (UlCheckBox.IsChecked == true)
        {
            if (!double.TryParse(UlValEntry.Text.Trim(), out double ulVal) || ulVal <= 0)
            {
                ToastNotification.Show(this, "Invalid Value", "Upload limit rate must be a positive number.", "#FF375F");
                return;
            }
            string unit = ((ComboBoxItem)UlUnitCombo.SelectedItem)?.Content?.ToString() ?? "KB/s";
            ulCap = $"{ulVal} {unit}";
        }

        if (dlCap == "Unlimited" && ulCap == "Unlimited")
        {
            ToastNotification.Show(this, "Select Limit Option", "Please enable at least one speed capping option.", "#FF375F");
            return;
        }

        _ruleManager.SetRule(exe, dlCap, ulCap);
        string appName = Path.GetFileName(exe);

        // Apply hosts redirection for direct game/CDN downloads (e.g. Riot Client, Valorant)
        HostsRedirectManager.ApplyRedirectsForApp(appName);

        // Calculate rate limit in bytes
        long limitToApply = 0;
        if (dlCap != "Unlimited") limitToApply = ShaperRule.ParseLimitToBytes(dlCap);
        else if (ulCap != "Unlimited") limitToApply = ShaperRule.ParseLimitToBytes(ulCap);

        // Apply Windows QoS policy for outbound pacing if applicable
        if (limitToApply > 0)
        {
            WindowsQosManager.ApplyPolicy(appName, limitToApply);
        }

        // Forcibly reset any existing unthrottled direct connections so the application reconnects through proxy
        int resetCount = NetworkMonitor.ResetSocketsForApp(appName, _proxyServer.Port);
        if (resetCount > 0)
        {
            Log($"ACTIVE CONNECTION MIGRATION: Reset {resetCount} direct sockets for '{appName}' -> rerouting through throttled proxy.");
        }

        Log($"POLICY APPLIED: Capped '{appName}' (DL: {dlCap} | UL: {ulCap}) [Hybrid Engine + Pacing Active]");
        if (appName.Contains("fdm", StringComparison.OrdinalIgnoreCase) || appName.Contains("torrent", StringComparison.OrdinalIgnoreCase))
        {
            Log($"💡 Download Manager Tip: To throttle BitTorrent peer traffic, set SOCKS5 Proxy 127.0.0.1:{_proxyServer.Port} in FDM Settings -> Network.");
        }

        // Check if application is currently connected to local shaper proxy
        bool isRouted = _proxyServer.ActiveConnections.Values.Any(c => c.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
        string procName = Path.GetFileNameWithoutExtension(appName);
        bool isRunning = Process.GetProcessesByName(procName).Length > 0;

        if (isRunning && !isRouted)
        {
            Log($"⚡ TIP FOR '{appName}': Direct network detected (not using system proxy). Click '🚀 Run Throttled' to restart {appName} with active traffic shaping.");
            ToastNotification.Show(this, "Click 'Run Throttled'", $"To shape '{appName}', click 'Run Throttled' to route its traffic through the shaper.", "#00F0FF");
        }
        else
        {
            ToastNotification.Show(this, "Rules Database Updated", $"Applied limits for {appName} successfully!", "#00FF88");
        }
    }

    private void OnDeletePolicyClicked(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleDisplayItem item)
        {
            ToastNotification.Show(this, "Highlight Rule Row", "Select an active rule entry in the database list below to delete.", "#FF9F0A");
            return;
        }

        if (_ruleManager.RemoveRule(item.App))
        {
            HostsRedirectManager.RemoveRedirectsForApp(item.App);
            WindowsQosManager.RemovePolicy(item.App);
            Log($"POLICY REMOVED: Bandwidth rules for app '{item.App}' purged.");
            ToastNotification.Show(this, "Policy Deleted", $"Rules registry wiped for {item.App}.", "#FF375F");
        }
    }

    private void OnRulesDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleDisplayItem item) return;

        ExeEntry.Text = item.App;

        // Populate DL
        if (item.DlLimit != "Unlimited")
        {
            DlCheckBox.IsChecked = true;
            ParseAndSetLimit(item.DlLimit, DlValEntry, DlUnitCombo);
        }
        else
        {
            DlCheckBox.IsChecked = false;
        }

        // Populate UL
        if (item.UlLimit != "Unlimited")
        {
            UlCheckBox.IsChecked = true;
            ParseAndSetLimit(item.UlLimit, UlValEntry, UlUnitCombo);
        }
        else
        {
            UlCheckBox.IsChecked = false;
        }

        OnLimitCheckBoxClicked(this, new RoutedEventArgs());
        ToastNotification.Show(this, "Policy Loaded", "Loaded selected bandwidth values into editor.", "#0A84FF");
    }

    private static void ParseAndSetLimit(string limitStr, TextBox valBox, ComboBox unitCombo)
    {
        string[] units = { "MB/s", "KB/s", "Mbps", "Kbps" };
        for (int i = 0; i < units.Length; i++)
        {
            if (limitStr.EndsWith(units[i], StringComparison.OrdinalIgnoreCase))
            {
                valBox.Text = limitStr[..^units[i].Length].Trim();
                unitCombo.SelectedIndex = i;
                return;
            }
        }
    }

    #endregion

    #region Process Grid & Background Metrics Loop

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnActiveOnlyChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is ProcessNetworkInfo p)
        {
            ExeEntry.Text = !string.IsNullOrEmpty(p.ExecutablePath) ? p.ExecutablePath : p.Name;
        }
    }

    private void OnProcessDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (ProcessGrid.SelectedItem is ProcessNetworkInfo p)
        {
            var inspector = new SocketsInspectorWindow(this, p.Name, _proxyServer);
            inspector.Show();
        }
    }

    private void ApplyFilter()
    {
        string search = SearchBox.Text.Trim().ToLowerInvariant();
        bool activeOnly = ActiveOnlyCheckBox.IsChecked == true;

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_processes);
        if (view == null) return;

        view.Filter = obj =>
        {
            if (obj is not ProcessNetworkInfo p) return false;

            if (activeOnly && !p.HasActiveSockets && !p.HasRule && p.SpeedBytes <= 0)
                return false;

            if (!string.IsNullOrEmpty(search))
            {
                return p.Name.ToLowerInvariant().Contains(search) ||
                       p.ExecutablePath.ToLowerInvariant().Contains(search);
            }
            return true;
        };
    }

    private async Task RunMetricsLoopAsync(CancellationToken ct)
    {
        var existingDict = new Dictionary<string, ProcessNetworkInfo>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                // Run data fetching and speed calculation on background thread pool
                var (grouped, speedMap, totalSockets) = await Task.Run(() =>
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    double elapsed = (double)(nowTicks - _lastMetricsTimestamp) / Stopwatch.Frequency;
                    _lastMetricsTimestamp = nowTicks;
                    if (elapsed <= 0.1) elapsed = 1.0;

                    // 1. Get active TCP sockets directly from Windows iphlpapi
                    var tcpConns = NetworkMonitor.GetActiveTcpConnections();
                    var connsByPid = tcpConns
                        .Where(c => c.OwningPid > 0)
                        .GroupBy(c => c.OwningPid)
                        .ToDictionary(g => g.Key, g => g.Count());

                    // 2. Enumerate OS processes and compute live transfer rates
                    var procs = Process.GetProcesses();
                    var map = new Dictionary<string, (string Name, string Path, int Conns, int Instances)>(StringComparer.OrdinalIgnoreCase);
                    var speeds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                    foreach (var proc in procs)
                    {
                        try
                        {
                            string name = proc.ProcessName;
                            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                name += ".exe";

                            string key = name.ToLowerInvariant();
                            int conns = connsByPid.TryGetValue(proc.Id, out int c) ? c : 0;

                            // 2a. ETW Kernel Network telemetry (captures ALL TCP/UDP traffic per PID)
                            long currentEtw = _etwMonitor.GetTotalBytesForPid(proc.Id);
                            if (_prevEtwBytes.TryGetValue(proc.Id, out long prevEtw) && currentEtw > prevEtw)
                            {
                                double etwRate = (currentEtw - prevEtw) / elapsed;
                                speeds[key] = speeds.GetValueOrDefault(key) + etwRate;
                            }
                            _prevEtwBytes[proc.Id] = currentEtw;

                            // 2b. Fallback Win32 I/O transfer counters
                            ulong currentIo = 0;
                            try
                            {
                                if (GetProcessIoCounters(proc.Handle, out var counters))
                                {
                                    currentIo = counters.ReadTransferCount + counters.WriteTransferCount;
                                }
                            }
                            catch { }

                            if (_prevIoBytes.TryGetValue(proc.Id, out ulong prevIo) && currentIo > prevIo)
                            {
                                double ioRate = (currentIo - prevIo) / elapsed;
                                // Only supplement if ETW hasn't already recorded this activity
                                if (!speeds.ContainsKey(key) || speeds[key] < 10)
                                {
                                    speeds[key] = speeds.GetValueOrDefault(key) + ioRate;
                                }
                            }
                            _prevIoBytes[proc.Id] = currentIo;

                            // Cached path lookup - never blocks the system
                            string path = _exePathCache.GetOrAdd(key, _ =>
                            {
                                try { return proc.MainModule?.FileName ?? string.Empty; }
                                catch { return string.Empty; }
                            });

                            if (map.TryGetValue(key, out var val))
                            {
                                map[key] = (val.Name, string.IsNullOrEmpty(val.Path) ? path : val.Path, val.Conns + conns, val.Instances + 1);
                            }
                            else
                            {
                                map[key] = (name, path, conns, 1);
                            }
                        }
                        catch
                        {
                            // Exited process
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }

                    // 3. SOCKS Proxy & SNI Interceptor cumulative byte telemetry (captures all shaped connections)
                    foreach (var appKey in map.Keys)
                    {
                        long currentDl = _proxyServer.GetAppBytesDl(appKey) + _sniInterceptor.GetAppBytesDl(appKey);
                        long currentUl = _proxyServer.GetAppBytesUl(appKey) + _sniInterceptor.GetAppBytesUl(appKey);

                        if (_prevProxyAppBytes.TryGetValue(appKey, out var prevProxy))
                        {
                            long proxyDelta = Math.Max(0, (currentDl + currentUl) - (prevProxy.DL + prevProxy.UL));
                            if (proxyDelta > 0)
                            {
                                double proxyRate = proxyDelta / elapsed;
                                speeds[appKey] = Math.Max(speeds.GetValueOrDefault(appKey), proxyRate);
                            }
                        }
                        _prevProxyAppBytes[appKey] = (currentDl, currentUl);
                    }

                    return (map, speeds, tcpConns.Count);
                }, ct).ConfigureAwait(false);

                // 4. UI update on Dispatcher: update properties in-place WITHOUT re-filtering
                await Dispatcher.InvokeAsync(() =>
                {
                    var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool addedAny = false;

                    foreach (var (key, data) in grouped)
                    {
                        seenKeys.Add(key);
                        var (dlLimit, ulLimit) = _ruleManager.GetAppliedLimits(data.Name);

                        string dlStr = dlLimit > 0 ? ShaperRule.FormatBytesPerSec(dlLimit) : "Unlimited";
                        string ulStr = ulLimit > 0 ? ShaperRule.FormatBytesPerSec(ulLimit) : "Unlimited";
                        double speed = speedMap.GetValueOrDefault(key, 0);

                        if (existingDict.TryGetValue(key, out var item))
                        {
                            // Direct in-place updates (triggers fine-grained INotifyPropertyChanged bindings only)
                            if (item.Instances != (data.Instances > 1 ? $"{data.Instances}x" : "1"))
                                item.Instances = data.Instances > 1 ? $"{data.Instances}x" : "1";

                            if (item.ConnsCount != data.Conns)
                                item.ConnsCount = data.Conns;

                            if (Math.Abs(item.SpeedBytes - speed) > 1)
                                item.SpeedBytes = speed;

                            if (item.DlLimit != dlStr)
                                item.DlLimit = dlStr;

                            if (item.UlLimit != ulStr)
                                item.UlLimit = ulStr;

                            if (string.IsNullOrEmpty(item.ExecutablePath) && !string.IsNullOrEmpty(data.Path))
                                item.ExecutablePath = data.Path;
                        }
                        else
                        {
                            // Add new item
                            var newItem = new ProcessNetworkInfo
                            {
                                Name = data.Name,
                                ExecutablePath = data.Path,
                                Instances = data.Instances > 1 ? $"{data.Instances}x" : "1",
                                ConnsCount = data.Conns,
                                SpeedBytes = speed,
                                DlLimit = dlStr,
                                UlLimit = ulStr
                            };
                            existingDict[key] = newItem;
                            _processes.Add(newItem);
                            addedAny = true;
                        }
                    }

                    // Remove dead processes
                    var toRemove = existingDict.Keys.Where(k => !seenKeys.Contains(k)).ToList();
                    foreach (var rem in toRemove)
                    {
                        var item = existingDict[rem];
                        existingDict.Remove(rem);
                        _processes.Remove(item);
                    }

                    StatusProcsLabel.Text = $"Scanning metrics... {_processes.Count} running | {totalSockets} active network sockets online.";

                    // Only refresh filter if a new item arrived
                    if (addedAny)
                    {
                        ApplyFilter();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient errors
            }
        }
    }

    #endregion
}
