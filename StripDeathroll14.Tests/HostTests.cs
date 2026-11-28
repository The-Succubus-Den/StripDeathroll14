using Xunit;

namespace StripDeathroll14.Tests;

/// <summary>A table to drive GameHost: tagged wording so every line's kind is visible, and a log of what was sent.</summary>
internal sealed class Table
{
    public static readonly Messages Tagged = new()
    {
        Opening = "[RS] {round} {roll}",
        Call = "[CALL] {name}, {roll}",
        Won = "[WIN] {name} {number} {round} ask",
        WonAndPicked = "[WIN] {name} {number} {round}",
        Stripped = "[STRIP] {name} {piece} {left}",
        StrippedOut = "[STRIP] {name} {piece} out",
        GameOver = "[OVER] {name}",
        WrongRoll = "[WRONG] {name} {number} {max} {roll}",
    };

    public bool AutoPost;
    public HashSet<CallKind> Held = [];
    public readonly List<(string Line, bool Manual)> Sent = [];
    public readonly List<string> Echoed = [];
    public bool Manual; // set while the host presses Post or Ask for reroll
    public readonly DeathrollGame Game = new();
    public readonly GameHost Host;
    public Action<string>? OnSend;

    public Table(params string[] names)
    {
        Host = new GameHost(Game, () => new HostSettings(AutoPost, Held, Tagged, Venue.Private, CallChannel.Venue, false),
            (_, line) => { OnSend?.Invoke(line); Sent.Add((line, Manual)); }, Echoed.Add);
        foreach (var n in names) Assert.True(Game.Add(n));
    }

    public Player P(string name) => Game.Find(name)!;
    public void Roll(string name, int value, int? max) => Host.Receive(P(name), value, max);
    public void Post() { Manual = true; Host.PostProposed(); Manual = false; }
    public void Ask() { Manual = true; Host.AskForReroll(); Manual = false; }
    public List<string> SentLines => Sent.Select(s => s.Line).ToList();

    /// <summary>Round 1 opened and Ann (highest) up with a fresh roll.</summary>
    public static Table InTurns(bool auto = true, params CallKind[] held)
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox") { AutoPost = auto, Held = [.. held] };
        t.Host.StartRound();
        t.Roll("Ann Lee", 900, null);
        t.Roll("Bo Ray", 500, null);
        t.Roll("Cy Fox", 100, null);
        return t;
    }
}

public class HostTests
{
    [Fact]
    public void NoCallToTheWinnerWhileTheRoundWaitsForItsPick()
    {
        var t = Table.InTurns();
        t.Roll("Ann Lee", 69, null);
        Assert.Equal(Phase.Pick, t.Game.Phase);
        var sent = t.Sent.Count;
        t.Host.CallCurrent(); // Rewind to previous call
        Assert.Equal(sent, t.Sent.Count);
        Assert.Empty(t.Host.Proposed);
    }

    [Fact]
    public void RestartingALaterRoundCallsTheStarterFreshAndDropsTheOldCall()
    {
        var t = Table.InTurns(auto: false);
        t.Roll("Ann Lee", 69, null);
        t.Host.Pick(t.P("Cy Fox"), Piece.Top); // round 2: Bo (after Ann) starts
        t.Roll("Bo Ray", 300, null);            // Cy up, to 300
        Assert.Equal(["[CALL] Cy Fox, /random 300"], t.Host.Proposed);
        t.Host.StartRound();                    // restart round 2
        Assert.Equal(Phase.Turns, t.Game.Phase);
        Assert.Equal(["[CALL] Bo Ray, /random"], t.Host.Proposed);
    }

    [Fact]
    public void RemovingThePlayerWhoIsUpCallsTheNextOneToTheSameNumber()
    {
        var t = Table.InTurns();
        t.Roll("Ann Lee", 400, null); // Bo up, to 400
        t.Host.RemovePlayer(t.P("Bo Ray"));
        Assert.Equal("Cy Fox", t.Game.Current!.Name);
        Assert.Equal("[CALL] Cy Fox, /random 400", t.SentLines[^1]);
    }

    [Fact]
    public void RemovingTheLastOpeningHoldoutStartsTheTurnsWithACall()
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox") { AutoPost = true };
        t.Host.StartRound();
        t.Roll("Ann Lee", 10, null);
        t.Roll("Bo Ray", 20, null);
        t.Host.RemovePlayer(t.P("Cy Fox"));
        Assert.Equal(Phase.Turns, t.Game.Phase);
        Assert.Equal("[CALL] Bo Ray, /random", t.SentLines[^1]);
    }

    [Fact]
    public void RemovingTheWinnerDropsTheirWinLine()
    {
        var t = Table.InTurns(auto: false);
        t.Roll("Ann Lee", 1, null);
        Assert.StartsWith("[WIN] Ann Lee", t.Host.Proposed[0]);
        t.Host.RemovePlayer(t.P("Ann Lee"));
        Assert.Equal(Phase.Setup, t.Game.Phase);
        Assert.Empty(t.Host.Proposed);
    }

    [Fact]
    public void DeletingTheWrongRollTakesItsRerollCallAway()
    {
        var t = Table.InTurns(auto: false);
        t.Roll("Ann Lee", 50, 100); // wrong: fresh /random expected
        Assert.Equal(["[WRONG] Ann Lee 50 100 /random"], t.Host.Proposed);
        Assert.True(t.Host.DeleteRoll(t.Game.RollLog.Count - 1));
        Assert.Null(t.Game.Pending);
        Assert.Empty(t.Host.Proposed);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void AHeldWinIsNotCaughtUpAtThePickAndHoldsNothingElse()
    {
        var t = Table.InTurns(auto: true, CallKind.Win);
        t.Roll("Ann Lee", 0, null);
        Assert.Single(t.Host.Proposed);
        t.Host.Pick(t.P("Bo Ray"), Piece.Top);
        Assert.Equal(["[STRIP] Bo Ray top 2 pieces", "[CALL] Bo Ray, /random"], t.SentLines.TakeLast(2));
        Assert.Empty(t.Host.Proposed);
    }

    [Fact]
    public void AHeldStripWaitsWhileTheCallGoes()
    {
        var t = Table.InTurns(auto: true, CallKind.Strip);
        t.Roll("Ann Lee", 0, null);
        t.Host.Pick(t.P("Cy Fox"), Piece.Shoes);
        Assert.Equal("[CALL] Bo Ray, /random", t.SentLines[^1]);
        Assert.Equal(["[STRIP] Cy Fox shoes 2 pieces"], t.Host.Proposed);
    }

    [Fact]
    public void WithAutoPostOffTheCatchUpWinStillLeadsThePickBatch()
    {
        var t = Table.InTurns(auto: false);
        t.Roll("Ann Lee", 666, null);
        t.Host.Pick(t.P("Bo Ray"), Piece.Top);
        Assert.Equal(["[WIN] Ann Lee 666 1", "[STRIP] Bo Ray top 2 pieces", "[CALL] Bo Ray, /random"], t.Host.Proposed);
    }

    [Fact]
    public void TheWrongRollAlertSaysWhetherTheCallWentOrWaits()
    {
        var off = Table.InTurns(auto: false);
        off.Roll("Ann Lee", 5, 10);
        Assert.Contains("waits for Post", off.Host.LastAlert);
        var held = Table.InTurns(auto: true, CallKind.WrongRoll);
        held.Roll("Ann Lee", 5, 10);
        Assert.Contains("waits for Post", held.Host.LastAlert);
        var on = Table.InTurns(auto: true);
        on.Roll("Ann Lee", 5, 10);
        Assert.Contains("Called them to reroll", on.Host.LastAlert);
        on.Roll("Ann Lee", 6, 10);
        Assert.Contains("already made this turn", on.Host.LastAlert);
        Assert.Single(on.SentLines, l => l.StartsWith("[WRONG]"));
    }

    [Fact]
    public void AnOpeningRerollCallGoesOnceTheyRollRight()
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox");
        t.Host.StartRound();
        t.Roll("Bo Ray", 50, 100);
        Assert.Equal(["[WRONG] Bo Ray 50 100 /random"], t.Host.Proposed);
        t.Roll("Bo Ray", 800, null);
        Assert.Empty(t.Host.Proposed);
    }

    [Fact]
    public void AskForRerollSendsOnceAndEmptiesTheWaitingCopy()
    {
        var t = Table.InTurns(auto: true, CallKind.WrongRoll);
        t.Roll("Ann Lee", 5, 10);
        Assert.DoesNotContain(t.SentLines, l => l.StartsWith("[WRONG]"));
        t.Ask();
        Assert.Equal(["[WRONG] Ann Lee 5 10 /random"], t.Sent.Where(s => s.Manual).Select(s => s.Line));
        Assert.Empty(t.Host.Proposed);
        t.Post();
        Assert.Single(t.Sent, s => s.Manual);
    }

    // ---- fuzz: random hosts, random rolls, random edits; the rules are checked after every step ----

    private static readonly string[] Pool = ["Ann Lee", "Bo Ray", "Cy Fox", "Di Moon", "Ed Hale", "Flo Kay", "Gus Ward"];

    [Fact]
    public void RandomGamesKeepEveryRule()
    {
        var steps = 0;
        for (var seed = 0; seed < 4000; seed++)
        {
            var rng = new Random(seed);
            var t = new Table(Pool.Take(rng.Next(2, 6)).ToArray()) { AutoPost = rng.Next(2) == 0 };
            foreach (var k in Enum.GetValues<CallKind>()) if (rng.Next(3) == 0) t.Held.Add(k);
            t.OnSend = line => CheckAutoSend(t, line);
            var log = new List<string>();
            for (var i = 0; i < 250; i++, steps++)
            {
                var what = Step(t, rng);
                log.Add(what);
                try
                {
                    CheckGame(t.Game);
                    CheckProposed(t);
                    CheckReplayIsStable(t);
                }
                catch (Exception e)
                {
                    throw new Exception($"seed {seed}, step {i}: {e.Message}\n  last steps: {string.Join(" | ", log.TakeLast(12))}", e);
                }
            }
        }
        Assert.True(steps > 0);
    }

    private static string Step(Table t, Random rng)
    {
        var g = t.Game;
        var active = g.Active.ToList();
        switch (rng.Next(100))
        {
            case < 45 when active.Count > 0:
            {
                // mostly the player expected, sometimes anyone; mostly the right number, sometimes not
                var p = rng.Next(4) > 0 && g.Phase == Phase.Turns && g.Current is { } c ? c
                    : rng.Next(4) > 0 && g.Phase == Phase.Opening && active.FirstOrDefault(x => x.Opening is null) is { } o ? o
                    : active[rng.Next(active.Count)];
                int? max = g.Phase == Phase.Turns ? g.Limit : null;
                if (rng.Next(6) == 0) max = rng.Next(3) == 0 ? null : rng.Next(2, 1000);
                var top = max ?? DeathrollGame.OpeningMax;
                var value = rng.Next(8) == 0 ? new[] { 0, 1, 69, 666 }.Where(v => v <= top).ElementAt(rng.Next(new[] { 0, 1, 69, 666 }.Count(v => v <= top))) : rng.Next(0, top + 1);
                t.Host.Receive(p, value, max);
                return $"roll {p.Name} {value}/{max?.ToString() ?? "-"}";
            }
            case < 52: t.Host.AcceptPending(); return "accept";
            case < 56: t.Ask(); return "ask";
            case < 68 when g.Phase == Phase.Pick && active.Count > 0:
            {
                var target = active[rng.Next(active.Count)];
                var piece = target.Worn[rng.Next(target.Worn.Count)];
                var (lives, winner, round) = (target.Lives, g.Winner!, g.Round);
                t.Host.Pick(target, piece);
                Assert.Equal(lives - 1, target.Lives);
                Assert.Equal(new RoundRecord(round, winner.Name, g.History[^1].WinningRoll, target.Name, piece, lives - 1), g.History[^1]);
                if (g.Phase == Phase.Turns) // round 2 on: the player after the winner starts, with a fresh roll
                {
                    Assert.Equal(g.NextActive(winner), g.Current);
                    Assert.Null(g.Limit);
                }
                return $"pick {target.Name} {piece}";
            }
            case < 74:
            {
                var could = g.CanStartRound;
                t.Host.StartRound();
                if (could) CheckRoundStart(g);
                return "start/restart";
            }
            case < 77 when g.Players.Count > 0:
            {
                var p = g.Players[rng.Next(g.Players.Count)];
                t.Host.RemovePlayer(p);
                return $"remove {p.Name}";
            }
            case < 80:
            {
                var name = Pool[rng.Next(Pool.Length)];
                g.Add(name);
                return $"add {name}";
            }
            case < 86 when g.CanEditRolls:
            {
                var editable = Enumerable.Range(0, g.RollLog.Count).Where(g.IsEditable).ToList();
                if (editable.Count == 0) return "edit (none)";
                var i = editable[rng.Next(editable.Count)];
                var r = g.RollLog[i];
                if (rng.Next(3) == 0) { t.Host.DeleteRoll(i); return $"delete #{i}"; }
                var max = r.Max;
                var value = rng.Next(0, (max ?? DeathrollGame.OpeningMax) + 1);
                t.Host.EditRoll(i, value, max);
                return $"edit #{i} -> {value}";
            }
            case < 89: t.Host.CallCurrent(); return "rewind";
            case < 93: t.Post(); return "post";
            case < 95 when g.Players.Count > 1:
            {
                var p = g.Players[rng.Next(g.Players.Count)];
                g.Move(p, rng.Next(2) == 0 ? -1 : 1);
                return $"move {p.Name}";
            }
            case < 97:
                t.AutoPost = !t.AutoPost;
                return $"autopost {t.AutoPost}";
            case < 98 when g.Phase is Phase.GameOver or Phase.Setup: t.Host.NewGame(); return "new game";
            default: return "idle";
        }
    }

    /// <summary>USER RULE: round 1 opens with everyone rolling; from round 2 a (re)start begins with the player after the last winner.</summary>
    private static void CheckRoundStart(DeathrollGame g)
    {
        if (g.Round == 1) { Assert.Equal(Phase.Opening, g.Phase); return; }
        var last = g.History.LastOrDefault(h => h.Round < g.Round);
        if (last is null || g.Players.FirstOrDefault(p => p.Name == last.Winner) is not { } w) return; // winner gone: whoever followed them
        Assert.Equal(Phase.Turns, g.Phase);
        Assert.Equal(g.NextActive(w), g.Current);
        Assert.Null(g.Limit);
    }

    private static string Kind(string line) => line[..(line.IndexOf(']') + 1)];

    private static CallKind KindOf(string line) => Kind(line) switch
    {
        "[RS]" => CallKind.RoundStart,
        "[CALL]" => CallKind.Call,
        "[WIN]" => CallKind.Win,
        "[STRIP]" => CallKind.Strip,
        "[OVER]" => CallKind.GameOver,
        "[WRONG]" => CallKind.WrongRoll,
        _ => throw new Exception($"untagged line sent: {line}"),
    };

    /// <summary>Auto-post sends only what it is set to send, and a call it sends is for whoever is up, with their roll.</summary>
    private static void CheckAutoSend(Table t, string line)
    {
        if (t.Manual) return;
        Assert.True(t.AutoPost, $"sent with Auto-post off: {line}");
        Assert.False(t.Held.Contains(KindOf(line)), $"held kind sent: {line}");
        if (KindOf(line) == CallKind.Call) Assert.Equal(ExpectedCall(t), line);
    }

    private static string? ExpectedCall(Table t) =>
        t.Game.Phase == Phase.Turns && t.Game.Current is { } c ? Lines.Call(c.Name, t.Host.RollCommand(t.Game.Limit), Table.Tagged) : null;

    /// <summary>What waits for Post is never stale: posting it now would still be right.</summary>
    private static void CheckProposed(Table t)
    {
        var g = t.Game;
        foreach (var line in t.Host.Proposed)
        {
            switch (KindOf(line))
            {
                case CallKind.RoundStart:
                    Assert.True(g.Phase == Phase.Opening, $"round-start line waiting in {g.Phase}: {line}");
                    break;
                case CallKind.Call:
                    Assert.True(ExpectedCall(t) == line, $"stale call waiting: {line} (expected {ExpectedCall(t) ?? "none"})");
                    break;
                case CallKind.WrongRoll:
                    var name = line["[WRONG] ".Length..].Split(' ').Take(2) is var parts ? string.Join(' ', parts) : "";
                    var ok = g.Phase == Phase.Turns && g.Pending?.Player.Name == name
                          || g.Phase == Phase.Opening && g.Find(name) is { Opening: null, Out: false };
                    Assert.True(ok, $"stale wrong-roll call waiting in {g.Phase}: {line}");
                    break;
                case CallKind.Win when line.EndsWith(" ask"):
                    Assert.True(g.Phase == Phase.Pick && line.StartsWith($"[WIN] {g.Winner?.Name} "), $"stale win line waiting in {g.Phase}: {line}");
                    break;
            }
        }
    }

    private static void CheckGame(DeathrollGame g)
    {
        var active = g.Active.Count();
        Assert.All(g.Players, p => Assert.InRange(p.Lives, 0, DeathrollGame.StartLives));
        Assert.Equal(g.Players.Count, g.Players.Select(p => p.Name).Distinct().Count());
        if (g.Limit is { } l) Assert.True(l >= 2, $"limit {l}");
        if (g.Pending is { } pd) Assert.True(g.Phase == Phase.Turns && pd.Player == g.Current, "a pending roll outside its turn");
        switch (g.Phase)
        {
            case Phase.Opening:
                Assert.True(active >= 2, "opening with fewer than 2");
                Assert.Contains(g.Active, p => p.Opening is null);
                Assert.Null(g.Current);
                break;
            case Phase.Turns:
                Assert.True(active >= 2, "turns with fewer than 2");
                Assert.NotNull(g.Current);
                Assert.False(g.Current!.Out, "an out player is up");
                Assert.Contains(g.Current, g.Players);
                break;
            case Phase.Pick:
                Assert.True(active >= 2, "pick with fewer than 2");
                Assert.NotNull(g.Winner);
                Assert.Contains(g.Winner!, g.Players);
                break;
            case Phase.GameOver:
                Assert.NotNull(g.Champion); // players may be listed after the game is over (for the next one); the champion stays
                break;
        }

        // every piece lost this game is one result line
        Assert.Equal(g.History.Count, g.History.Select(h => h.Round).Distinct().Count());
    }

    /// <summary>Editing a roll to the number it already has changes nothing: replaying the round agrees with live play.</summary>
    private static void CheckReplayIsStable(Table t)
    {
        var g = t.Game;
        var editable = Enumerable.Range(0, g.RollLog.Count).Where(g.IsEditable).ToList();
        if (editable.Count == 0) return;
        State Snap() => new(g.Phase, g.Current?.Name, g.Limit, g.Winner?.Name, g.WinningRoll, g.Pending?.Player.Name, g.Pending?.Value,
            string.Join(",", g.Players.Select(p => $"{p.Name}:{p.Opening}")));
        var before = Snap();
        var i = editable[0];
        var r = g.RollLog[i];
        var proposed = t.Host.Proposed.ToList();
        var sent = t.Sent.Count;
        Assert.True(t.Host.EditRoll(i, r.Value, r.Max));
        var logNow = string.Join("; ", g.RollLog.Select((x, k) => $"#{k} r{x.Round} {x.Player} {x.Value}/{x.Max?.ToString() ?? "-"} {x.Note}{(x.Accepted ? " acc" : "")}").Where(s => s.Contains($" r{g.Round} ")));
        Assert.True(before == Snap(), $"replay disagrees with live play (edited #{i}):\n   live   {before}\n   replay {Snap()}\n   log {logNow}");
        Assert.Equal(sent, t.Sent.Count);
        Assert.Equal(proposed, t.Host.Proposed);
    }

    private sealed record State(Phase Phase, string? Current, int? Limit, string? Winner, int WinningRoll, string? Pending, int? PendingValue, string Openings);
}
