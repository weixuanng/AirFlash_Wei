namespace AirFlash.Core;

public enum LinkGrade { Unknown, Poor, Fair, Good }

/// <summary>Cumulative packet counts across every speaker in the session.</summary>
/// <param name="Sent">Audio packets sent.</param>
/// <param name="Resent">Packets a speaker asked for again and got.</param>
/// <param name="Late">Packets a speaker asked for again that could no longer be
/// supplied in time. These are the ones heard as a gap.</param>
public readonly record struct LinkTotals(long Sent, long Resent, long Late)
{
    public static LinkTotals? From(TransportMetrics metrics)
    {
        if (metrics.Members.Count == 0 || metrics.Members.Any(m => m.PacketsSent is null)) return null;
        return new(metrics.Members.Sum(m => m.PacketsSent ?? 0), metrics.Members.Sum(m => m.RetransmitsSent ?? 0),
            metrics.Members.Sum(m => (m.RetransmitExpired ?? 0) + (m.RetransmitMissing ?? 0)));
    }
    /// <summary>The change since <paramref name="earlier"/>, or null when a counter ran backwards (a new stream).</summary>
    public LinkTotals? Since(LinkTotals earlier) => Sent >= earlier.Sent && Resent >= earlier.Resent && Late >= earlier.Late
        ? new(Sent - earlier.Sent, Resent - earlier.Resent, Late - earlier.Late) : null;
}

/// <summary>
/// How well audio is reaching the speaker, judged from what the speaker asks to
/// have sent again over roughly the last ten seconds. It is the only evidence of
/// Wi-Fi loss the sender has: a stream can look perfect from this end while the
/// speaker is missing packets.
/// </summary>
public sealed class LinkQuality
{
    private const int Window = 10, Warmup = 3;
    private readonly Queue<LinkTotals> _samples = new();
    private LinkTotals? _last;
    private double? _uptime;
    private long? _epoch;
    public LinkGrade Grade { get; private set; }
    /// <summary>Share of recent packets the speaker had to ask for again, in percent.</summary>
    public double LossPercent { get; private set; }

    public void Reset()
    {
        _samples.Clear(); _last = null; _uptime = null; _epoch = null; Grade = LinkGrade.Unknown; LossPercent = 0;
    }
    /// <summary>Feed one metrics report. Returns true when the grade changed.</summary>
    public bool Observe(TransportMetrics? metrics)
    {
        var before = Grade;
        if (metrics?.SessionUptimeMs is not { } uptime || LinkTotals.From(metrics) is not { } totals) return false;
        if (metrics.SessionEpoch != _epoch || _uptime is { } last && uptime < last) { Reset(); _epoch = metrics.SessionEpoch; }
        else if (uptime == _uptime) return false; // the same report, published again
        _uptime = uptime;
        if (_last is { } earlier && totals.Since(earlier) is { } delta)
        {
            _samples.Enqueue(delta);
            while (_samples.Count > Window) _samples.Dequeue();
        }
        _last = totals;
        var sent = _samples.Sum(s => s.Sent);
        if (_samples.Count < Warmup || sent <= 0) { Grade = LinkGrade.Unknown; LossPercent = 0; return Grade != before; }
        var late = _samples.Sum(s => s.Late);
        LossPercent = 100.0 * (_samples.Sum(s => s.Resent) + late) / sent;
        Grade = LossPercent >= 3 || _samples.Count(s => s.Late > 0) >= 2 ? LinkGrade.Poor
            : LossPercent >= 0.5 || late > 0 ? LinkGrade.Fair : LinkGrade.Good;
        return Grade != before;
    }
}
