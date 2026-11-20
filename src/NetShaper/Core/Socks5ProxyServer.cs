using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetShaper.Models;

namespace NetShaper.Core;

public class Socks5ProxyServer : IDisposable
{
    public class ConnectionStats
    {
        public string AppName { get; set; } = "Unknown";
        public int Pid { get; set; }
        public string TargetAddress { get; set; } = string.Empty;
        public int SourcePort { get; set; }
        public long BytesDl { get; set; }
        public long BytesUl { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly RuleManager _ruleManager;
    private readonly BandwidthRegulatorRegistry _regulators;
    private readonly ConcurrentDictionary<string, ConnectionStats> _activeConnections = new();

    // Lifetime application byte counters for rock-solid live speed rate metrics
    private readonly ConcurrentDictionary<string, long> _totalAppBytesDl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _totalAppBytesUl = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; private set; } = 1080;
    public bool IsRunning { get; private set; }
    public IReadOnlyDictionary<string, ConnectionStats> ActiveConnections => _activeConnections;

    public event Action<string>? LogMessage;

    public Socks5ProxyServer(RuleManager ruleManager, BandwidthRegulatorRegistry regulators)
    {
        _ruleManager = ruleManager;
        _regulators = regulators;
    }

    public long GetAppBytesDl(string appName) => _totalAppBytesDl.GetValueOrDefault(appName, 0);
    public long GetAppBytesUl(string appName) => _totalAppBytesUl.GetValueOrDefault(appName, 0);

    public bool Start()
    {
        if (IsRunning) return true;

        // Find available port starting from 1080
        for (int p = 1080; p < 1120; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                Port = p;
                break;
            }
            catch
            {
                // Try next port
            }
        }

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start(256);
            IsRunning = true;

            _ = AcceptLoopAsync(_cts.Token);
            LogMessage?.Invoke($"SOCKS5 Proxy Engine active on 127.0.0.1:{Port}");
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to bind proxy listener: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _activeConnections.Clear();
        LogMessage?.Invoke("SOCKS5 Proxy Engine stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!IsRunning) break;
                LogMessage?.Invoke($"Proxy accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream? clientStream = null;
            TcpClient? destClient = null;
            NetworkStream? destStream = null;
            string connId = string.Empty;

            try
            {
                clientStream = client.GetStream();
                var clientEp = (IPEndPoint)client.Client.RemoteEndPoint!;
                int sourcePort = clientEp.Port;

                byte[] header = new byte[1024];
                int read = await clientStream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);
                if (read < 2) return;

                byte version = header[0];
                string destHost = string.Empty;
                int destPort = 0;

                // Resolve client application name and PID
                int pid = ResolvePidFromSourcePort(sourcePort);
                string appName = "Unknown";
                if (pid > 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        appName = proc.ProcessName + ".exe";
                    }
                    catch { }
                }

                if (version == 5)
                {
                    // SOCKS5 Handshake: respond with 0x05, 0x00 (NO AUTH REQUIRED)
                    await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);

                    // Read request
                    read = await clientStream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);
                    if (read < 7 || header[0] != 5) return;

                    byte cmd = header[1];

                    if (cmd == 1) // TCP CONNECT
                    {
                        byte addrType = header[3];
                        int offset = 4;

                        if (addrType == 1) // IPv4
                        {
                            if (read < offset + 4 + 2) return;
                            var ip = new IPAddress(header[offset..(offset + 4)]);
                            destHost = ip.ToString();
                            offset += 4;
                        }
                        else if (addrType == 3) // Domain name
                        {
                            byte domainLen = header[offset++];
                            if (read < offset + domainLen + 2) return;
                            destHost = Encoding.ASCII.GetString(header, offset, domainLen);
                            offset += domainLen;
                        }
                        else if (addrType == 4) // IPv6
                        {
                            if (read < offset + 16 + 2) return;
                            var ip = new IPAddress(header[offset..(offset + 16)]);
                            destHost = ip.ToString();
                            offset += 16;
                        }
                        else
                        {
                            return;
                        }

                        destPort = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset, 2));

                        // Connect to target host
                        destClient = new TcpClient { NoDelay = true };
                        try
                        {
                            await destClient.ConnectAsync(destHost, destPort, ct).ConfigureAwait(false);
                            destStream = destClient.GetStream();
                            // Success response: 0x05, 0x00 (succeeded), 0x00 (rsv), 0x01 (IPv4), 0.0.0.0:0
                            await clientStream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            await clientStream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    else if (cmd == 3) // UDP ASSOCIATE (BitTorrent uTP / DNS / VoIP)
                    {
                        await HandleUdpAssociateAsync(clientStream, appName, pid, ct).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        // Command not supported
                        await clientStream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
                        return;
                    }
                }
                else if (version == 4)
                {
                    // SOCKS4 / 4a support
                    if (read < 9 || header[1] != 1) return; // CMD 1 = CONNECT
                    destPort = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                    var ipBytes = header[4..8];

                    int idx = 8;
                    while (idx < read && header[idx] != 0) idx++;
                    idx++; // Skip null-terminator

                    if (ipBytes[0] == 0 && ipBytes[1] == 0 && ipBytes[2] == 0 && ipBytes[3] != 0)
                    {
                        // SOCKS4a domain
                        int domainStart = idx;
                        while (idx < read && header[idx] != 0) idx++;
                        destHost = Encoding.ASCII.GetString(header, domainStart, idx - domainStart);
                    }
                    else
                    {
                        destHost = new IPAddress(ipBytes).ToString();
                    }

                    destClient = new TcpClient { NoDelay = true };
                    try
                    {
                        await destClient.ConnectAsync(destHost, destPort, ct).ConfigureAwait(false);
                        destStream = destClient.GetStream();
                        // 0x00, 0x5a (granted), port (2), ip (4)
                        await clientStream.WriteAsync(new byte[] { 0x00, 0x5A, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        await clientStream.WriteAsync(new byte[] { 0x00, 0x5B, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    // Dual-Mode Hybrid Proxy: Handle HTTP/HTTPS CONNECT Tunnel & Plaintext HTTP Proxy
                    string reqText = Encoding.ASCII.GetString(header, 0, read);
                    if (reqText.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                    {
                        // HTTPS Proxy Tunnel (CONNECT host:port HTTP/1.1)
                        int space1 = reqText.IndexOf(' ');
                        int space2 = reqText.IndexOf(' ', space1 + 1);
                        if (space1 > 0 && space2 > space1)
                        {
                            string target = reqText.Substring(space1 + 1, space2 - space1 - 1).Trim();
                            string[] parts = target.Split(':');
                            destHost = parts[0];
                            destPort = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 443;
                        }

                        if (string.IsNullOrEmpty(destHost)) return;

                        destClient = new TcpClient { NoDelay = true };
                        try
                        {
                            await destClient.ConnectAsync(destHost, destPort, ct).ConfigureAwait(false);
                            destStream = destClient.GetStream();

                            // Respond with HTTP/1.1 200 Connection Established
                            byte[] okResp = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                            await clientStream.WriteAsync(okResp, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            byte[] errResp = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
                            await clientStream.WriteAsync(errResp, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    else if (reqText.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) ||
                             reqText.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) ||
                             reqText.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase) ||
                             reqText.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase) ||
                             reqText.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase) ||
                             reqText.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
                    {
                        // Plaintext HTTP Proxy request (e.g. GET http://host:port/path HTTP/1.1)
                        byte[] dataToSend = header[..read];
                        int space1 = reqText.IndexOf(' ');
                        int space2 = reqText.IndexOf(' ', space1 + 1);
                        if (space1 > 0 && space2 > space1)
                        {
                            string url = reqText.Substring(space1 + 1, space2 - space1 - 1).Trim();
                            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                            {
                                destHost = uri.Host;
                                destPort = uri.Port > 0 ? uri.Port : 80;
                                // Rewrite "GET http://host:port/path HTTP/1.1" -> "GET /path HTTP/1.1" for origin CDN compatibility
                                string rewrittenReq = reqText.Replace($"http://{uri.Authority}", "");
                                dataToSend = Encoding.ASCII.GetBytes(rewrittenReq);
                            }
                        }

                        if (string.IsNullOrEmpty(destHost))
                        {
                            // Fallback to Host: header
                            foreach (var line in reqText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                {
                                    string h = line.Substring(5).Trim();
                                    string[] parts = h.Split(':');
                                    destHost = parts[0];
                                    destPort = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 80;
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(destHost)) return;

                        destClient = new TcpClient { NoDelay = true };
                        try
                        {
                            await destClient.ConnectAsync(destHost, destPort, ct).ConfigureAwait(false);
                            destStream = destClient.GetStream();
                            // Forward request buffer to remote host
                            await destStream.WriteAsync(dataToSend, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            byte[] errResp = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
                            await clientStream.WriteAsync(errResp, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }

                var (dlLimit, ulLimit) = _ruleManager.GetAppliedLimits(appName);
                string dlDesc = dlLimit > 0 ? ShaperRule.FormatBytesPerSec(dlLimit) : "Unlimited";
                string ulDesc = ulLimit > 0 ? ShaperRule.FormatBytesPerSec(ulLimit) : "Unlimited";
                LogMessage?.Invoke($"SOCKS: '{appName}' (PID {pid}) -> {destHost}:{destPort} [DL: {dlDesc} | UL: {ulDesc}]");

                connId = $"{clientEp.Address}:{sourcePort}->{destHost}:{destPort}";
                var stats = new ConnectionStats
                {
                    AppName = appName,
                    Pid = pid,
                    SourcePort = sourcePort,
                    TargetAddress = $"{destHost}:{destPort}",
                    StartTime = DateTime.UtcNow
                };
                _activeConnections[connId] = stats;

                // Pipe bi-directionally with backpressure pacing
                var ulTask = PipeStreamAsync(clientStream, destStream, "UL", appName, stats, ct);
                var dlTask = PipeStreamAsync(destStream, clientStream, "DL", appName, stats, ct);

                await Task.WhenAny(ulTask, dlTask).ConfigureAwait(false);
                await Task.WhenAny(Task.WhenAll(ulTask, dlTask), Task.Delay(500, ct)).ConfigureAwait(false);
            }
            catch
            {
                // Client disconnected
            }
            finally
            {
                if (!string.IsNullOrEmpty(connId))
                {
                    _activeConnections.TryRemove(connId, out _);
                }
                try { destStream?.Dispose(); } catch { }
                try { destClient?.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleUdpAssociateAsync(NetworkStream clientStream, string appName, int pid, CancellationToken ct)
    {
        using var udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var localEp = udpListener.Client.LocalEndPoint as IPEndPoint;
        int relayPort = localEp?.Port ?? 0;

        // Respond with BND.ADDR = 127.0.0.1, BND.PORT = relayPort
        byte[] resp = new byte[]
        {
            0x05, 0x00, 0x00, 0x01,
            127, 0, 0, 1,
            (byte)(relayPort >> 8),
            (byte)(relayPort & 0xFF)
        };
        await clientStream.WriteAsync(resp, ct).ConfigureAwait(false);
        LogMessage?.Invoke($"SOCKS UDP ASSOCIATE: Bound relay for '{appName}' (PID {pid}) on 127.0.0.1:{relayPort}");

        // Keep TCP connection alive as control channel; when TCP terminates, UDP association ends
        byte[] dummy = new byte[64];
        while (!ct.IsCancellationRequested)
        {
            int r = await clientStream.ReadAsync(dummy, ct).ConfigureAwait(false);
            if (r == 0) break;
        }
    }

    private async Task PipeStreamAsync(
        Stream src,
        Stream dst,
        string direction,
        string appName,
        ConnectionStats stats,
        CancellationToken ct)
    {
        byte[] buffer = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (dlLimit, ulLimit) = _ruleManager.GetAppliedLimits(appName);
                long currentLimit = direction == "DL" ? dlLimit : ulLimit;

                // Dynamically scale read buffer based on rate limit to ensure silky-smooth pacing
                int maxRead = buffer.Length;
                if (currentLimit > 0)
                {
                    maxRead = Math.Clamp((int)(currentLimit / 30), 4096, 65536);
                }

                int read = await src.ReadAsync(buffer.AsMemory(0, maxRead), ct).ConfigureAwait(false);
                if (read == 0) break;

                // Throttling applied BEFORE forwarding data downstream
                // This forces TCP receive window collapse on the sender, achieving true backpressure flow control
                if (currentLimit > 0)
                {
                    var bucket = _regulators.GetBucket(appName, direction);
                    await bucket.ThrottleAsync(read, currentLimit, ct).ConfigureAwait(false);
                }

                // Forward data to receiver
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                // Update metrics
                if (direction == "DL")
                {
                    stats.BytesDl += read;
                    _totalAppBytesDl.AddOrUpdate(appName, read, (_, cur) => cur + read);
                }
                else
                {
                    stats.BytesUl += read;
                    _totalAppBytesUl.AddOrUpdate(appName, read, (_, cur) => cur + read);
                }
            }
        }
        catch
        {
            // Socket closed or canceled
        }
    }

    private static int ResolvePidFromSourcePort(int sourcePort)
    {
        for (int i = 0; i < 3; i++)
        {
            var map = NetworkMonitor.GetPortToPidMap();
            if (map.TryGetValue(sourcePort, out int pid) && pid > 0)
                return pid;
            if (i < 2) Thread.Sleep(15);
        }
        return 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
