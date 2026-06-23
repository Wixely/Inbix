using System.Collections.Concurrent;
using Inbix.Core.Options;
using Microsoft.Extensions.Options;

namespace Inbix.Smtp;

/// <summary>
/// Enforces SMTP abuse controls the SmtpServer library does not provide natively: a cap on
/// concurrent sessions and a per-IP connection rate limit (sliding 1-minute window). Active session
/// counts are maintained from the server's session lifecycle events; admission is checked at MAIL FROM.
/// </summary>
public sealed class SmtpConnectionGovernor
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly int _maxConcurrent;
    private readonly int _perIpPerMinute;
    private readonly Func<DateTimeOffset> _now;

    private int _active;
    private readonly ConcurrentDictionary<string, IpWindow> _perIp = new();

    public SmtpConnectionGovernor(IOptions<InbixOptions> options, Func<DateTimeOffset>? now = null)
    {
        var smtp = options.Value.Smtp;
        _maxConcurrent = smtp.MaxConcurrentSessions;
        _perIpPerMinute = smtp.MaxConnectionsPerMinutePerIp;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public int ActiveSessions => Volatile.Read(ref _active);

    /// <summary>Record a new session (for the concurrency cap). Raised from SessionCreated.</summary>
    public void SessionStarted() => Interlocked.Increment(ref _active);

    public void SessionEnded() => Interlocked.Decrement(ref _active);

    /// <summary>
    /// Decide whether to admit the transaction at MAIL FROM. Records the attempt for the per-IP
    /// rate window here (rather than at connect time) because the remote endpoint is reliably
    /// available by MAIL FROM.
    /// </summary>
    public bool TryAdmit(string? ip, out string reason)
    {
        if (_maxConcurrent > 0 && Volatile.Read(ref _active) > _maxConcurrent)
        {
            reason = $"too many concurrent sessions (max {_maxConcurrent})";
            return false;
        }

        if (_perIpPerMinute > 0 && !string.IsNullOrEmpty(ip))
        {
            RecordConnection(ip);
            if (CountRecent(ip) > _perIpPerMinute)
            {
                reason = $"connection rate limit exceeded for {ip} (max {_perIpPerMinute}/min)";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private void RecordConnection(string ip)
    {
        var window = _perIp.GetOrAdd(ip, _ => new IpWindow());
        lock (window.Gate)
        {
            window.Timestamps.Enqueue(_now());
            Trim(window);
        }
    }

    private int CountRecent(string ip)
    {
        if (!_perIp.TryGetValue(ip, out var window))
            return 0;

        lock (window.Gate)
        {
            Trim(window);
            if (window.Timestamps.Count == 0)
                _perIp.TryRemove(ip, out _);
            return window.Timestamps.Count;
        }
    }

    private void Trim(IpWindow window)
    {
        var cutoff = _now() - Window;
        while (window.Timestamps.Count > 0 && window.Timestamps.Peek() < cutoff)
            window.Timestamps.Dequeue();
    }

    private sealed class IpWindow
    {
        public readonly object Gate = new();
        public readonly Queue<DateTimeOffset> Timestamps = new();
    }
}
