using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetShaper.Core;

/// <summary>
/// Monitors real-time network throughput across all system processes using Windows Kernel Event Tracing (ETW).
/// Captures all TCP and UDP packet payload bytes (direct sockets, torrents, browsers, SOCKS) per Process ID.
/// </summary>
public class EtwNetworkMonitor : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly ConcurrentDictionary<int, long> _totalBytesPerPid = new();
    private bool _isRunning;

    public bool IsActive => _isRunning;

    public event Action<string>? LogMessage;

    public void Start()
    {
        if (_isRunning) return;

        // TraceEventSession kernel provider requires Administrator elevation
        if (!TraceEventSession.IsElevated().GetValueOrDefault())
        {
            LogMessage?.Invoke("ETW Kernel Monitor: Process not elevated. Operating in user-mode proxy monitoring.");
            return;
        }

        try
        {
            string sessionName = "NetShaperKernelNetworkSession";

            // Cleanup any previous session if left from an unexpected exit
            try
            {
                using var old = TraceEventSession.GetActiveSession(sessionName);
                old?.Stop(true);
            }
            catch { }

            _session = new TraceEventSession(sessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            // TCP Traffic
            _session.Source.Kernel.TcpIpRecv += data =>
            {
                if (data.ProcessID > 0 && data.size > 0)
                {
                    _totalBytesPerPid.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                }
            };

            _session.Source.Kernel.TcpIpSend += data =>
            {
                if (data.ProcessID > 0 && data.size > 0)
                {
                    _totalBytesPerPid.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                }
            };

            // UDP Traffic (covers BitTorrent uTP, QUIC, DNS, WebRTC)
            _session.Source.Kernel.UdpIpRecv += data =>
            {
                if (data.ProcessID > 0 && data.size > 0)
                {
                    _totalBytesPerPid.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                }
            };

            _session.Source.Kernel.UdpIpSend += data =>
            {
                if (data.ProcessID > 0 && data.size > 0)
                {
                    _totalBytesPerPid.AddOrUpdate(data.ProcessID, data.size, (_, prev) => prev + data.size);
                }
            };

            _isRunning = true;
            _processingThread = new Thread(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch { }
            })
            {
                IsBackground = true,
                Name = "NetShaper_ETW_Thread"
            };
            _processingThread.Start();

            LogMessage?.Invoke("ETW Kernel Network Monitor active. Capturing OS-wide socket transfers.");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"ETW Kernel Monitor notice: {ex.Message} (falling back to proxy & socket telemetry)");
            _isRunning = false;
        }
    }

    public long GetTotalBytesForPid(int pid)
    {
        return _totalBytesPerPid.TryGetValue(pid, out long bytes) ? bytes : 0;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try
        {
            _session?.Stop();
            _session?.Dispose();
        }
        catch { }
        _session = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
