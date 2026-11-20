using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace NetShaper.Core;

/// <summary>
/// Thread-safe Token Bucket rate limiter with cumulative debt scheduling for concurrent multi-socket pacing.
/// Prevents multiple concurrent streams from bursting in parallel and exceeding the aggregate speed limit.
/// </summary>
public class TokenBucket
{
    private readonly object _syncLock = new();
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket()
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        _tokens = 32768; // Initial burst allowance
    }

    /// <summary>
    /// Throttles async execution if the requested byte count exceeds available tokens.
    /// Distributes debt across all concurrent streams so aggregate rate strictly matches limitBytesPerSec.
    /// </summary>
    public async ValueTask ThrottleAsync(int byteCount, long limitBytesPerSec, CancellationToken ct = default)
    {
        if (limitBytesPerSec <= 0 || byteCount <= 0) return;

        double delayMs = 0;

        lock (_syncLock)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsed = (double)(nowTicks - _lastTimestamp) / Stopwatch.Frequency;
            _lastTimestamp = nowTicks;

            // Maximum burst cap: small buffer (max 50ms or 64KB) to prevent massive bursts
            double capacity = Math.Min(Math.Max(limitBytesPerSec * 0.05, 16384), 65536);
            _tokens = Math.Min(capacity, _tokens + elapsed * limitBytesPerSec);

            _tokens -= byteCount;

            if (_tokens < 0)
            {
                // Debt is accumulated across all parallel streams!
                double deficitSeconds = (-_tokens) / limitBytesPerSec;
                delayMs = deficitSeconds * 1000.0;

                // Clamp max debt to 1.5 seconds to prevent unbounded stalls under massive parallel requests
                if (delayMs > 1500)
                {
                    delayMs = 1500;
                    _tokens = -limitBytesPerSec * 1.5;
                }
            }
        }

        if (delayMs > 1.0)
        {
            int ms = (int)Math.Ceiling(delayMs);
            await Task.Delay(ms, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Central registry of directional (Download/Upload) token bucket regulators per application.
/// </summary>
public class BandwidthRegulatorRegistry
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    public TokenBucket GetBucket(string appName, string direction)
    {
        string normalizedApp = Path.GetFileNameWithoutExtension(appName).ToLowerInvariant();
        string key = $"{normalizedApp}:{direction.ToUpperInvariant()}";
        return _buckets.GetOrAdd(key, _ => new TokenBucket());
    }

    public void Clear() => _buckets.Clear();
}
