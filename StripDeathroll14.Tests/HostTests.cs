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
    // A roll the host enters ends the wait exactly as the player's own roll would: the Echo reminders stop and the next
    // player's wait starts fresh (its first reminder in the channel, after the full quiet time).
    [Fact]
    public void ARollTheHostEntersEndsTheWaitLikeARealRoll()
    {
        var t = Table.InTurns();
        var clock = new ReminderClock();
        Assert.Equal(["Ann Lee"], t.Game.Waiting.Select(p => p.Name));
        Assert.False(clock.Due(t.Game.WaitingKey, 0, 30_000));
        Assert.True(clock.Due(t.Game.WaitingKey, 30_000, 30_000));   // first: channel
        Assert.True(clock.Due(t.Game.WaitingKey, 40_000, 30_000));   // then Echo, 10 s apart
        Assert.StartsWith("Added", t.Host.AddRoll(t.P("Ann Lee"), 500, null));
        Assert.Equal(["Bo Ray"], t.Game.Waiting.Select(p => p.Name));
        Assert.False(clock.Due(t.Game.WaitingKey, 45_000, 30_000));  // Ann's wait is over: a new one for Bo
        Assert.True(clock.NextGoesToChat);
        Assert.False(clock.Due(t.Game.WaitingKey, 50_000, 30_000));  // no 10 s Echo beat carried over from Ann
        Assert.False(clock.Due(t.Game.WaitingKey, 74_999, 30_000));
        Assert.True(clock.Due(t.Game.WaitingKey, 75_000, 30_000));
        Assert.True(clock.LastWentToChat);

        // Opening: entering one missing roll moves the wait on to whoever is still missing theirs.
        var o = new Table("Ann Lee", "Bo Ray", "Cy Fox");
        o.Host.StartRound();
        var c = new ReminderClock();
        Assert.False(c.Due(o.Game.WaitingKey, 0, 30_000));
        Assert.True(c.Due(o.Game.WaitingKey, 30_000, 30_000));
        Assert.StartsWith("Added", o.Host.AddRoll(o.P("Bo Ray"), 400, null));
        Assert.Equal(["Ann Lee", "Cy Fox"], o.Game.Waiting.Select(p => p.Name));
        Assert.False(c.Due(o.Game.WaitingKey, 31_000, 30_000));
        Assert.True(c.NextGoesToChat);
    }

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
        Assert.Contains("went to chat", on.Host.LastAlert);
        on.Roll("Ann Lee", 6, 10);
        Assert.Contains("went to chat", on.Host.LastAlert);
        Assert.Single(on.SentLines, l => l.StartsWith("[WRONG]"));
    }

    [Fact]
    public void AnOpeningRerollCallGoesOnceTheyRollRight()
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox");
        t.Host.StartRound();
        t.Roll("Bo Ray", 50, 100);
        Assert.Equal(["[RS] 1 /random", "[WRONG] Bo Ray 50 100 /random"], t.Host.Proposed); // the round-start line stays
        t.Roll("Bo Ray", 800, null);
        Assert.Equal(["[RS] 1 /random"], t.Host.Proposed);
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

    [Fact]
    public void EditsAndSeatChangesDoNotBuyASecondWrongRollCallInTheSameTurn()
    {
        var t = Table.InTurns(auto: true);              // Ann up, fresh /random
        t.Roll("Ann Lee", 50, 100);
        Assert.Single(t.SentLines, l => l.StartsWith("[WRONG]"));
        Assert.True(t.Host.EditRoll(1, 501, null));     // Bo's opening 500 -> 501: replayed, Ann still up with her wrong roll waiting
        t.Game.Add("Di Moon");
        t.Host.RemovePlayer(t.P("Cy Fox"));
        t.Game.Move(t.P("Bo Ray"), 1);
        Assert.Equal(t.P("Ann Lee"), t.Game.Current);
        t.Host.AcceptPending();                          // settle it the other way round: the turn moves on...
        Assert.True(t.Host.DeleteRoll(t.Game.RollLog.Count - 1)); // ...and the delete brings Ann's same turn back
        Assert.Equal(t.P("Ann Lee"), t.Game.Current);
        t.Roll("Ann Lee", 60, 100);
        Assert.Single(t.SentLines, l => l.StartsWith("[WRONG]"));
        Assert.Contains("went to chat", t.Host.LastAlert);
    }

    [Fact]
    public void AnOpeningWrongRollIsCalledOncePerPlayerEvenWithOthersInBetween()
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox") { AutoPost = true };
        t.Host.StartRound();
        t.Roll("Ann Lee", 5, 10);
        t.Roll("Bo Ray", 5, 10);
        t.Roll("Ann Lee", 6, 10);                        // Bo's call in between used to let Ann have a second one
        Assert.Equal(["[WRONG] Ann Lee 5 10 /random", "[WRONG] Bo Ray 5 10 /random"], t.SentLines.Where(l => l.StartsWith("[WRONG]")));
        t.Host.StartRound();                             // a restart starts the opening over: a new call is due
        t.Roll("Ann Lee", 7, 10);
        Assert.Equal("[WRONG] Ann Lee 7 10 /random", t.SentLines[^1]);
    }

    [Fact]
    public void OpeningRerollCallsWaitSideBySideAndTheAlertTellsTheTruth()
    {
        var t = new Table("Ann Lee", "Bo Ray", "Cy Fox");  // Auto-post off
        t.Host.StartRound();
        t.Roll("Ann Lee", 5, 10);
        t.Roll("Bo Ray", 6, 10);                          // used to replace Ann's call (and the round-start line)
        Assert.Equal(["[RS] 1 /random", "[WRONG] Ann Lee 5 10 /random", "[WRONG] Bo Ray 6 10 /random"], t.Host.Proposed);
        t.Host.Proposed.Clear();                          // the host discards them
        t.Roll("Ann Lee", 7, 10);
        Assert.Contains("discarded, not posted", t.Host.LastAlert); // not "already made": nothing reached chat
        Assert.Empty(t.Host.Proposed);                    // still once per turn
    }

    [Fact]
    public void AnEditKeepsTheWrongRollAlertAndRewordsItsWaitingCall()
    {
        var t = Table.InTurns(auto: false);
        t.Roll("Ann Lee", 400, null);                     // Bo up, to 400
        t.Roll("Bo Ray", 50, 100);                        // wrong
        Assert.Equal(["[WRONG] Bo Ray 50 100 /random 400"], t.Host.Proposed);
        var alert = t.Host.LastAlert;
        Assert.True(t.Host.EditRoll(0, 901, null));       // Ann's opening 900 -> 901: changes nothing
        Assert.Equal(alert, t.Host.LastAlert);
        Assert.True(t.Host.EditRoll(3, 300, null));       // Ann's 400 was really 300: Bo's call now names /random 300
        Assert.Equal(["[WRONG] Bo Ray 50 100 /random 300"], t.Host.Proposed);
        Assert.Contains("expected /random 300", t.Host.LastAlert);
        Assert.Contains("waits for Post", t.Host.LastAlert);
    }

    [Fact]
    public void DeletingTheWrongRollDoesNotCallThePlayerAgain()
    {
        var t = Table.InTurns(auto: true);
        var sent = t.Sent.Count;
        t.Roll("Ann Lee", 5, 10);
        Assert.True(t.Host.DeleteRoll(t.Game.RollLog.Count - 1));
        Assert.Equal(["[WRONG] Ann Lee 5 10 /random"], t.SentLines.Skip(sent)); // no second "Ann, roll /random"
        Assert.Null(t.Host.LastAlert);
    }

    [Fact]
    public void AReturningPlayerKeepsWhatTheyLostAndAFinishedGameStaysFinished()
    {
        var t = Table.InTurns(auto: true);
        t.Roll("Ann Lee", 1, null);
        t.Host.Pick(t.P("Bo Ray"), Piece.Top);
        t.Host.RemovePlayer(t.P("Bo Ray"));
        Assert.True(t.Game.Add("Bo Ray"));                // back in the same game: still without the top
        Assert.Equal([Piece.Bottom, Piece.Shoes], t.P("Bo Ray").Worn);
        Assert.Equal(1, t.P("Ann Lee").Wins);
        t.Host.RemovePlayer(t.P("Ann Lee"));
        Assert.True(t.Game.Add("Ann Lee"));
        Assert.Equal(1, t.P("Ann Lee").Wins);

        var g = new Table("Ann Lee", "Bo Ray") { AutoPost = true };
        g.Host.StartRound();
        g.Roll("Ann Lee", 900, null);
        g.Roll("Bo Ray", 100, null);
        foreach (var piece in DeathrollGame.AllPieces)
        {
            if (g.Game.Current?.Name == "Bo Ray") g.Roll("Bo Ray", 500, g.Game.Limit);
            g.Roll("Ann Lee", 1, g.Game.Limit);
            g.Host.Pick(g.P("Bo Ray"), piece);
        }

        Assert.Equal(Phase.GameOver, g.Game.Phase);
        g.Host.RemovePlayer(g.P("Ann Lee"));              // the champion leaves: the game stays over (no restart from here)
        Assert.Equal(Phase.GameOver, g.Game.Phase);
        Assert.Equal("Ann Lee", g.Game.Champion);
        Assert.False(g.Game.CanStartRound);
    }

    [Fact]
    public void DebugModeOffRemovesTheDummiesThroughTheHost()
    {
        var t = new Table("Ann Lee", "Bo Ray") { AutoPost = true };
        t.Game.AddDummy();
        t.Host.StartRound();
        t.Roll("Ann Lee", 100, null);
        t.Roll("Bo Ray", 200, null);
        t.Roll("Dummy Alpha", 900, null);                 // the dummy starts
        Assert.Equal("Dummy Alpha", t.Game.Current!.Name);
        t.Host.RemoveDummies();
        Assert.Equal("[CALL] Ann Lee, /random", t.SentLines[^1]); // the turn passed on, and the call went with it
    }

    // ---- fuzz: random hosts, random rolls, random edits; the rules are checked after every step ----

    private static readonly string[] Pool = ["Ann Lee", "Bo Ray", "Cy Fox", "Di Moon", "Ed Hale", "Flo Kay", "Gus Ward"];

    [Fact]
    public void RandomGamesKeepEveryRule()
    {
        var steps = 0;
        var failures = new Dictionary<string, string>();
        for (var seed = 0; seed < 4000; seed++)
        {
            var rng = new Random(seed);
            var t = new Table(Pool.Take(rng.Next(2, 6)).ToArray()) { AutoPost = rng.Next(2) == 0 };
            foreach (var k in Enum.GetValues<CallKind>()) if (rng.Next(3) == 0) t.Held.Add(k);
            t.OnSend = line => CheckAutoSend(t, line);
            var log = new List<string>();
            var start = 0;                   // which start of the round (restarts, new rounds, new games), counted by the test itself
            var wrongCalls = new HashSet<string>();
            var wrongSent = new HashSet<string>();
            for (var i = 0; i < 250; i++, steps++)
            {
                var g = t.Game;
                var (game, round, echoed, sent) = (g.GameNumber, g.Round, t.Echoed.Count, t.Sent.Count);
                var turn = TurnKey(g, start);
                var proposedBefore = t.Host.Proposed.ToList();
                var pendingName = g.Pending?.Player.Name;
                var (turnIdBefore, waitBefore) = (g.TurnId, g.WaitingKey);
                var what = Step(t, rng);
                log.Add(what);
                if (what.StartsWith("start/restart") || what.StartsWith("pick") || what == "new game" || what.StartsWith("clear players") || g.Round != round || g.GameNumber != game) start++;
                try
                {
                    // USER RULE: one wrong-roll call per turn (per player in the opening), whatever is edited, added, removed or moved
                    static string Who(string wrongLine) => string.Join(' ', wrongLine.Split(' ').Skip(1).Take(2));
                    var waitedBefore = proposedBefore.Where(l => l.StartsWith("[WRONG]")).Select(Who).ToHashSet(); // a waiting call reworded by an edit is the same call
                    var newCalls = t.Host.Proposed.Where(l => l.StartsWith("[WRONG]")).Select(Who).Where(w => !waitedBefore.Contains(w))
                        .Concat(t.Sent.Skip(sent).Where(s => !s.Manual && s.Line.StartsWith("[WRONG]")).Select(s => Who(s.Line))).Distinct();
                    foreach (var who in newCalls)
                        Assert.True(wrongCalls.Add($"{turn}/{who}"), $"a second wrong-roll call for {who} in turn {turn}");
                    foreach (var s in t.Sent.Skip(sent).Where(s => s.Line.StartsWith("[WRONG]")))
                        wrongSent.Add($"{turn}/{Who(s.Line)}");
                    if (what == "ask" && t.Sent.Skip(sent).Any(s => s.Manual && s.Line.StartsWith("[WRONG]")))
                        wrongCalls.Add($"{turn}/{pendingName}"); // Ask always sends (even after the automatic call) and counts as the turn's call
                    // the alert tells the truth about that call
                    var turnNow = TurnKey(g, start);
                    foreach (var e in t.Echoed.Skip(echoed).Where(e => e.Contains("reroll call")))
                    {
                        var who = e[..e.IndexOf(" rolled", StringComparison.Ordinal)];
                        var k = $"{turnNow}/{who}";
                        var waiting = t.Host.Proposed.Any(l => l.StartsWith($"[WRONG] {who} "));
                        if (e.Contains("went to chat")) Assert.True(wrongSent.Contains(k), $"alert says {who}'s reroll call went to chat, none did in {turnNow}");
                        else if (e.Contains("waits for Post")) Assert.True(waiting, $"alert says {who}'s reroll call waits for Post, it does not");
                        else if (e.Contains("discarded")) Assert.True(wrongCalls.Contains(k) && !wrongSent.Contains(k) && !waiting, $"alert says {who}'s reroll call was discarded: {k} host {g.TurnId}; test calls [{string.Join(", ", wrongCalls.Where(c => c.EndsWith(who)))}] sent [{string.Join(", ", wrongSent.Where(c => c.EndsWith(who)))}] waiting {waiting}; log {string.Join("; ", g.RollLog.Where(r => r.Round == g.Round).Select(r => $"{r.Player} {r.Value}/{r.Max} {r.Note}"))}");
                        else Assert.True(!wrongCalls.Contains(k), $"alert says no reroll call was made for {who}, one was in {turnNow}");
                    }
                    // listing, moving or removing someone who is not playing the turn leaves the turn and its wait alone
                    if (what.StartsWith("add ") && !what.StartsWith("add roll") || what.StartsWith("move "))
                    {
                        Assert.True(turnIdBefore == g.TurnId, $"{what} changed the turn: {turnIdBefore} -> {g.TurnId}");
                        Assert.True(waitBefore == g.WaitingKey, $"{what} changed the reminder wait: {waitBefore} -> {g.WaitingKey}");
                    }
                    CheckPieces(g);
                    CheckGame(t.Game);
                    CheckProposed(t);
                    CheckReplayIsStable(t);
                }
                catch (Exception e)
                {
                    // one example of each kind of break (the text up to the first ':'), then the next seed
                    var kind = e.Message.Split(':')[0];
                    failures.TryAdd(kind, $"seed {seed}, step {i}: {e.Message}\n  last steps: {string.Join(" | ", log.TakeLast(12))}");
                    break;
                }
            }
        }
        Assert.True(steps > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of break:\n" + string.Join("\n", failures.Values));
    }

    private static string Step(Table t, Random rng)
    {
        var g = t.Game;
        var active = g.Active.ToList();
        switch (rng.Next(107))
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
                if (!could) return "start (not allowed)";
                CheckRoundStart(g);
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
            case < 98: t.Host.NewGame(); return "new game"; // the window allows it at any time (Ctrl+click)
            case < 101 when active.Count > 0 && g.CanEditRolls:
            {
                var p = active[rng.Next(active.Count)];
                int? max = g.Phase == Phase.Turns && rng.Next(4) > 0 ? g.Limit : null;
                var value = rng.Next(0, (max ?? DeathrollGame.OpeningMax) + 1);
                t.Host.AddRoll(p, value, max);
                return $"add roll {p.Name} {value}/{max?.ToString() ?? "-"}";
            }
            case < 103 when rng.Next(2) == 0 && g.Players.Count > 1:
            {
                var p = g.Players[rng.Next(g.Players.Count)];
                var to = rng.Next(g.Players.Count);
                g.MoveTo(p, to);
                return $"move {p.Name} to {to}";
            }
            case < 103: t.Host.Proposed.Clear(); return "discard";
            case < 104: g.ClearHistory(); return "clear history";
            case < 106 when rng.Next(3) > 0:
                if (rng.Next(2) == 0) { g.AddDummy(); return "add dummy"; }
                t.Host.RemoveDummies();
                return "remove dummies";
            case < 106:
            {
                t.Host.ClearPlayers();
                foreach (var n in Pool.OrderBy(_ => rng.Next()).Take(rng.Next(2, 5))) g.Add(n);
                return "clear players + add some back";
            }
            default: return "idle";
        }
    }

    /// <summary>The turn as the test sees it, worked out on its own: which start of the round, rolls counted so far, who is up.</summary>
    private static string TurnKey(DeathrollGame g, int start) => g.Phase == Phase.Opening ? $"{g.GameNumber}/{g.Round}/{start}/o"
        : $"{g.GameNumber}/{g.Round}/{start}/{g.Phase}/{g.RollLog.Count(r => r.Round == g.Round && r.Note is RollNote.Counted or RollNote.Accepted or RollNote.Won)}/{g.Current?.Name}";

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
                    var ok = g.Phase == Phase.Turns && g.Pending?.Player.Name == name && line.EndsWith(" " + t.Host.RollCommand(g.Limit))
                          || g.Phase == Phase.Opening && g.Find(name) is { Opening: null, Out: false } && line.EndsWith(" " + t.Host.RollCommand(null));
                    Assert.True(ok, $"stale wrong-roll call waiting in {g.Phase}: {line}");
                    break;
                case CallKind.Win when line.EndsWith(" ask"):
                    Assert.True(g.Phase == Phase.Pick && line.StartsWith($"[WIN] {g.Winner?.Name} "), $"stale win line waiting in {g.Phase}: {line}");
                    break;
            }
        }
    }

    /// <summary>Pieces and wins follow the game's results: a player who left and came back has lost what they lost.</summary>
    private static void CheckPieces(DeathrollGame g)
    {
        if (g.Phase == Phase.GameOver) return; // the history of a finished game may be cleared before New game
        foreach (var p in g.Players)
        {
            var lost = g.History.Where(h => h.Target == p.Name).Select(h => h.Piece).ToList();
            Assert.True(lost.Count == lost.Distinct().Count(), $"{p.Name} lost a piece twice in one game");
            Assert.True(DeathrollGame.AllPieces.Except(lost).Order().SequenceEqual(p.Worn.Order()),
                $"{p.Name} wears {string.Join("+", p.Worn)} but the results took {string.Join("+", lost)}");
            Assert.True(g.History.Count(h => h.Winner == p.Name) == p.Wins, $"{p.Name} has {p.Wins} wins, the results say {g.History.Count(h => h.Winner == p.Name)}");
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
        var (alert, turn, wait) = (t.Host.LastAlert, g.TurnId, g.WaitingKey);
        Assert.True(t.Host.EditRoll(i, r.Value, r.Max));
        Assert.True(alert == t.Host.LastAlert, $"an edit that changed nothing changed the alert: {alert ?? "none"} -> {t.Host.LastAlert ?? "none"}");
        Assert.True(turn == g.TurnId, $"an edit that changed nothing changed the turn: {turn} -> {g.TurnId}");
        Assert.True(wait == g.WaitingKey, $"an edit that changed nothing changed the reminder wait: {wait} -> {g.WaitingKey}");
        var logNow = string.Join("; ", g.RollLog.Select((x, k) => $"#{k} r{x.Round} {x.Player} {x.Value}/{x.Max?.ToString() ?? "-"} {x.Note}{(x.Accepted ? " acc" : "")}").Where(s => s.Contains($" r{g.Round} ")));
        Assert.True(before == Snap(), $"replay disagrees with live play (edited #{i}):\n   live   {before}\n   replay {Snap()}\n   log {logNow}");
        Assert.Equal(sent, t.Sent.Count);
        Assert.Equal(proposed, t.Host.Proposed);
    }

    private sealed record State(Phase Phase, string? Current, int? Limit, string? Winner, int WinningRoll, string? Pending, int? PendingValue, string Openings);
}
