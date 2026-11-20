using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetShaper.Models;

namespace NetShaper.Core;

/// <summary>
/// High-performance SNI transparent TLS/HTTP forwarder listening on 127.0.0.1:443 and 127.0.0.1:80.
/// Intercepts direct HTTPS socket traffic (such as Riot Client / Valorant patch chunk downloads),
/// extracts the SNI domain from TLS ClientHello without decrypting TLS, resolves the origin IP via DoH,
/// and applies Token Bucket backpressure bandwidth throttling transparently.
/// </summary>
public class TransparentSniInterceptor : IDisposable
{
    private readonly RuleManager _ruleManager;
    private readonly BandwidthRegulatorRegistry _regulators;
    private readonly ConcurrentDictionary<string, long> _totalAppBytesDl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _totalAppBytesUl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string IP, DateTime Expire)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(4) };

    private TcpListener? _listener443;
    private TcpListener? _listener80;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public bool IsActive => _isRunning;
    public event Action<string>? LogMessage;

    public TransparentSniInterceptor(RuleManager ruleManager, BandwidthRegulatorRegistry regulators)
    {
        _ruleManager = ruleManager;
        _regulators = regulators;
    }

    public void Start()
    {
        if (_isRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _listener443 = new TcpListener(IPAddress.Loopback, 443);
            _listener443.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener443.Start(100);

            _ = AcceptLoopAsync(_listener443, 443, ct);
            _isRunning = true;
            LogMessage?.Invoke("Transparent SNI Interceptor active on 127.0.0.1:443 (Direct Game/CDN Throttler)");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Transparent SNI (443) notice: {ex.Message}");
        }

        try
        {
            _listener80 = new TcpListener(IPAddress.Loopback, 80);
            _listener80.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener80.Start(100);

            _ = AcceptLoopAsync(_listener80, 80, ct);
        }
        catch { }
    }

    private async Task AcceptLoopAsync(TcpListener listener, int localPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, localPort, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, int defaultPort, CancellationToken ct)
    {
        using (client)
        {
            var clientStream = client.GetStream();
            byte[] initialBuf = new byte[4096];
            int read = 0;

            try
            {
                read = await clientStream.ReadAsync(initialBuf.AsMemory(0, initialBuf.Length), ct).ConfigureAwait(false);
                if (read <= 0) return;
            }
            catch { return; }

            // 1. Identify target hostname
            string? targetHost = null;
            if (defaultPort == 443)
            {
                targetHost = ExtractSniHostname(initialBuf, read);
            }
            else
            {
                string text = Encoding.ASCII.GetString(initialBuf, 0, read);
                foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHost = line.Substring(5).Trim().Split(':')[0];
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(targetHost)) return;

            // 2. Identify calling application & PID
            int sourcePort = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Port ?? 0;
            string appName = "Unknown";
            int pid = 0;

            for (int retry = 0; retry < 3; retry++)
            {
                var portMap = NetworkMonitor.GetPortToPidMap();
                if (portMap.TryGetValue(sourcePort, out pid) && pid > 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        appName = proc.ProcessName + ".exe";
                        break;
                    }
                    catch { }
                }
                await Task.Delay(15, ct).ConfigureAwait(false);
            }

            // 3. Resolve destination IP via DoH (bypassing local hosts loopback)
            string? remoteIp = await ResolveDoHAsync(targetHost, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(remoteIp))
            {
                // Fallback to hardcoded Cloudflare CDN IP if resolving Riot CDN
                if (targetHost.Contains("riotcdn.net", StringComparison.OrdinalIgnoreCase))
                {
                    remoteIp = "104.18.34.3";
                }
                else
                {
                    return;
                }
            }

            var (dlLimit, ulLimit) = _ruleManager.GetAppliedLimits(appName);
            string dlDesc = dlLimit > 0 ? ShaperRule.FormatBytesPerSec(dlLimit) : "Unlimited";
            string ulDesc = ulLimit > 0 ? ShaperRule.FormatBytesPerSec(ulLimit) : "Unlimited";
            LogMessage?.Invoke($"TRANSPARENT: '{appName}' (PID {pid}) -> {targetHost}:{defaultPort} [DL: {dlDesc} | UL: {ulDesc}]");

            // 4. Connect to remote destination
            using var destClient = new TcpClient { NoDelay = true };
            try
            {
                await destClient.ConnectAsync(remoteIp, defaultPort, ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            using var destStream = destClient.GetStream();
            // Send initial buffered handshake data
            await destStream.WriteAsync(initialBuf.AsMemory(0, read), ct).ConfigureAwait(false);

            // 5. Bi-directional stream piping with token-bucket rate limiting
            var ulTask = PipeStreamAsync(clientStream, destStream, "UL", appName, ct);
            var dlTask = PipeStreamAsync(destStream, clientStream, "DL", appName, ct);

            await Task.WhenAny(ulTask, dlTask).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(ulTask, dlTask), Task.Delay(500, ct)).ConfigureAwait(false);
        }
    }

    private async Task PipeStreamAsync(Stream src, Stream dst, string direction, string appName, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (dlLimit, ulLimit) = _ruleManager.GetAppliedLimits(appName);
                long currentLimit = direction == "DL" ? dlLimit : ulLimit;

                int maxRead = buffer.Length;
                if (currentLimit > 0)
                {
                    maxRead = (int)Math.Clamp(currentLimit / 20, 2048, 32768);
                }

                int read = await src.ReadAsync(buffer.AsMemory(0, maxRead), ct).ConfigureAwait(false);
                if (read == 0) break;

                if (currentLimit > 0)
                {
                    var bucket = _regulators.GetBucket(appName, direction);
                    await bucket.ThrottleAsync(read, currentLimit, ct).ConfigureAwait(false);
                }

                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                if (direction == "DL")
                {
                    _totalAppBytesDl.AddOrUpdate(appName, read, (_, cur) => cur + read);
                }
                else
                {
                    _totalAppBytesUl.AddOrUpdate(appName, read, (_, cur) => cur + read);
                }
            }
        }
        catch { }
    }

    public long GetAppBytesDl(string appName) => _totalAppBytesDl.TryGetValue(appName, out long b) ? b : 0;
    public long GetAppBytesUl(string appName) => _totalAppBytesUl.TryGetValue(appName, out long b) ? b : 0;

    /// <summary>
    /// Parses TLS 1.0 - 1.3 ClientHello byte stream to extract SNI (Server Name Indication).
    /// Returns the exact destination domain without decrypting TLS.
    /// </summary>
    private static string? ExtractSniHostname(byte[] data, int length)
    {
        if (length < 44 || data[0] != 0x16) return null; // 0x16 = Handshake record
        int pos = 5;
        if (pos >= length || data[pos] != 0x01) return null; // 0x01 = ClientHello

        pos += 4; // Skip Type (1) + Length (3)
        pos += 2 + 32; // Skip Version (2) + Random (32)
        if (pos >= length) return null;

        int sessLen = data[pos];
        pos += 1 + sessLen;
        if (pos + 2 >= length) return null;

        int cipherLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
        pos += 2 + cipherLen;
        if (pos >= length) return null;

        int compLen = data[pos];
        pos += 1 + compLen;
        if (pos + 2 >= length) return null;

        int extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
        pos += 2;
        int end = Math.Min(length, pos + extTotalLen);

        while (pos + 4 <= end)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2, 2));
            pos += 4;

            if (extType == 0) // Server Name Indication (SNI)
            {
                if (pos + 5 <= end)
                {
                    // Skip list length (2) + name type (1)
                    ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 3, 2));
                    if (pos + 5 + nameLen <= end)
                    {
                        return Encoding.ASCII.GetString(data, pos + 5, nameLen);
                    }
                }
            }

            pos += extLen;
        }

        return null;
    }

    private async Task<string?> ResolveDoHAsync(string domain, CancellationToken ct)
    {
        if (_dnsCache.TryGetValue(domain, out var cached) && cached.Expire > DateTime.UtcNow)
        {
            return cached.IP;
        }

        string[] dohEndpoints =
        {
            $"https://1.1.1.1/dns-query?name={domain}&type=A",
            $"https://dns.google/resolve?name={domain}&type=A"
        };

        foreach (var endpoint in dohEndpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Add("Accept", "application/dns-json");

                using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Answer", out var ansArr) && ansArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in ansArr.EnumerateArray())
                        {
                            if (elem.TryGetProperty("type", out var typeProp) && typeProp.GetInt32() == 1) // Type A
                            {
                                if (elem.TryGetProperty("data", out var dataProp))
                                {
                                    string ip = dataProp.GetString() ?? string.Empty;
                                    if (IPAddress.TryParse(ip, out _))
                                    {
                                        _dnsCache[domain] = (ip, DateTime.UtcNow.AddMinutes(5));
                                        return ip;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try { _cts?.Cancel(); } catch { }
        try { _listener443?.Stop(); } catch { }
        try { _listener80?.Stop(); } catch { }
        _cts?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
