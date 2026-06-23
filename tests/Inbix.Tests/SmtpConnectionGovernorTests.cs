using Inbix.Core.Options;
using Inbix.Smtp;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public class SmtpConnectionGovernorTests
{
    private static SmtpConnectionGovernor Build(int maxConcurrent, int perIpPerMinute, Func<DateTimeOffset> clock)
    {
        var options = Options.Create(new InbixOptions
        {
            Smtp = { MaxConcurrentSessions = maxConcurrent, MaxConnectionsPerMinutePerIp = perIpPerMinute }
        });
        return new SmtpConnectionGovernor(options, clock);
    }

    [Fact]
    public void Admits_Up_To_Concurrency_Cap_Then_Rejects()
    {
        var now = DateTimeOffset.UnixEpoch;
        var g = Build(maxConcurrent: 2, perIpPerMinute: 0, () => now);

        g.SessionStarted(); // active 1
        g.SessionStarted(); // active 2
        Assert.True(g.TryAdmit("a", out _));   // 2 is not > 2

        g.SessionStarted(); // active 3
        Assert.False(g.TryAdmit("a", out var reason)); // 3 > 2
        Assert.Contains("concurrent", reason);

        g.SessionEnded(); // back to 2
        Assert.True(g.TryAdmit("a", out _));
    }

    [Fact]
    public void Concurrency_Disabled_When_Zero()
    {
        var now = DateTimeOffset.UnixEpoch;
        var g = Build(maxConcurrent: 0, perIpPerMinute: 0, () => now);
        for (var i = 0; i < 100; i++) g.SessionStarted();
        Assert.True(g.TryAdmit("a", out _));
    }

    [Fact]
    public void Enforces_Per_Ip_Rate_Limit_Within_Window()
    {
        var now = DateTimeOffset.UnixEpoch;
        var g = Build(maxConcurrent: 0, perIpPerMinute: 3, () => now);

        // Each TryAdmit records the attempt; the first 3 are admitted, the 4th exceeds the limit.
        Assert.True(g.TryAdmit("1.2.3.4", out _));
        Assert.True(g.TryAdmit("1.2.3.4", out _));
        Assert.True(g.TryAdmit("1.2.3.4", out _));
        Assert.False(g.TryAdmit("1.2.3.4", out var reason)); // 4 > 3
        Assert.Contains("rate limit", reason);

        // A different IP is unaffected.
        Assert.True(g.TryAdmit("5.6.7.8", out _));
    }

    [Fact]
    public void Rate_Window_Slides_So_Old_Attempts_Expire()
    {
        var now = DateTimeOffset.UnixEpoch;
        var g = Build(maxConcurrent: 0, perIpPerMinute: 2, () => now);

        Assert.True(g.TryAdmit("1.2.3.4", out _));
        Assert.True(g.TryAdmit("1.2.3.4", out _));
        Assert.False(g.TryAdmit("1.2.3.4", out _)); // 3 > 2 within the same instant

        now = now.AddSeconds(61); // window slides past the earlier attempts
        Assert.True(g.TryAdmit("1.2.3.4", out _));
    }
}
