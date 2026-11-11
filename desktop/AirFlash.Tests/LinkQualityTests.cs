using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LinkQualityTests
{
    private static TransportMetrics Link(double uptime, long sent, long resent = 0, long late = 0, long epoch = 7)
        => AdaptiveLatencyTests.Link(uptime, sent, resent, late, epoch);
    [Fact]
    public void GradeIsUnknownUntilThereIsEnoughToJudge()
    {
        var quality = new LinkQuality();
        quality.Observe(Link(1000, 125)); quality.Observe(Link(2000, 250)); quality.Observe(Link(3000, 375));
        Assert.Equal(LinkGrade.Unknown, quality.Grade);
        quality.Observe(Link(4000, 500));
        Assert.Equal(LinkGrade.Good, quality.Grade);
        Assert.False(quality.Observe(Link(4000, 500))); // the same report again changes nothing
        Assert.False(quality.Observe(null));
    }
    [Fact]
    public void LossAndLateResendsLowerTheGradeAndAgeOut()
    {
        var quality = new LinkQuality();
        long sent = 0, resent = 0, late = 0; double uptime = 0;
        void Tick(long lost = 0, long tooLate = 0) { sent += 125; resent += lost; late += tooLate; uptime += 1000; quality.Observe(Link(uptime, sent, resent, late)); }
        for (var i = 0; i < 5; i++) Tick();
        Assert.Equal(LinkGrade.Good, quality.Grade);
        Tick(lost: 4); // 4 of 500 recent packets: 0.8 %
        Assert.Equal(LinkGrade.Fair, quality.Grade);
        Tick(tooLate: 1); Tick(tooLate: 1);
        Assert.Equal(LinkGrade.Poor, quality.Grade);
        for (var i = 0; i < 10; i++) Tick();
        Assert.Equal(LinkGrade.Good, quality.Grade);
        Assert.Equal(0, quality.LossPercent);
        for (var i = 0; i < 3; i++) Tick(lost: 20); // 60 of 1250: 4.8 %
        Assert.Equal(LinkGrade.Poor, quality.Grade);
    }
    [Fact]
    public void ANewStreamStartsTheJudgementAgain()
    {
        var quality = new LinkQuality();
        for (var i = 1; i <= 6; i++) quality.Observe(Link(i * 1000, i * 125, resent: i * 10));
        Assert.Equal(LinkGrade.Poor, quality.Grade);
        quality.Observe(Link(1000, 125, epoch: 8));
        Assert.Equal(LinkGrade.Unknown, quality.Grade);
        // Members without counters are no evidence either way.
        Assert.False(quality.Observe(new TransportMetrics(2000, 0, 0, 0, [], SessionEpoch: 8)));
    }
}
