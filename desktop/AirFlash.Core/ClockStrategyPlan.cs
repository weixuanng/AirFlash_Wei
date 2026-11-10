using System.Text.Json;
namespace AirFlash.Core;

/// <summary>
/// Which clock arrangement a standalone HomePod on HomePod software 27 is offered
/// on each connection attempt.
///
/// Published senders disagree about these speakers: some HomePods only play when
/// the sender supplies the clock, others only when the sender follows theirs, and
/// a HomePod mini 2 on 27.2 has been seen to refuse the arrangement that works on
/// a HomePod mini on 27.0. So the arrangements are tried in turn, one per attempt,
/// and the one that starts streaming is remembered for that receiver.
/// </summary>
public sealed class ClockStrategyPlan(string? path = null)
{
    /// <summary>The engine supplies the clock, exactly as an iOS sender does.</summary>
    public const string Sender = "sender";
    /// <summary>Follow the receiver's clock and send it no timing traffic at all.</summary>
    public const string FollowQuiet = "follow_quiet";
    /// <summary>Follow the receiver's clock and take part as an Apple timing peer.</summary>
    public const string FollowActive = "follow_active";
    public static readonly IReadOnlyList<string> Order = [Sender, FollowQuiet, FollowActive];

    private readonly object _sync = new();
    private Dictionary<string, int>? _turns;

    /// <summary>The arrangement to use for this receiver's next attempt.</summary>
    public string Current(string receiverId)
    {
        lock (_sync) return Order[Load().GetValueOrDefault(receiverId) % Order.Count];
    }

    /// <summary>The attempt never started streaming: move on to the next arrangement.</summary>
    public string Failed(string receiverId)
    {
        lock (_sync)
        {
            var turns = Load();
            var next = (turns.GetValueOrDefault(receiverId) + 1) % Order.Count;
            turns[receiverId] = next;
            return Order[next];
        }
    }

    /// <summary>The attempt is streaming: keep this arrangement across restarts.</summary>
    public void Worked(string receiverId)
    {
        lock (_sync)
        {
            if (path is null) return;
            var turns = Load();
            try
            {
                var saved = turns.ToDictionary(turn => turn.Key, turn => Order[turn.Value % Order.Count]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(saved));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, int> Load()
    {
        if (_turns is not null) return _turns;
        _turns = new(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return _turns;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
            foreach (var (receiver, strategy) in saved)
            {
                var index = Order.ToList().IndexOf(strategy);
                if (index >= 0) _turns[receiver] = index;
            }
        }
        // A damaged or unreadable file only costs the remembered choice.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return _turns;
    }
}
