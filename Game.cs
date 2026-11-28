using System.Text.RegularExpressions;

namespace StripDeathroll14;

/// <summary>Where the game is played. Saved as a number: never renumber, only append.</summary>
public enum Venue
{
    Private = 0, // yell
    Public = 1,  // party, or alliance when in one
}

/// <summary>Where the rules and ad texts are posted. Saved as a number: never renumber, only append.</summary>
public enum TextChannel
{
    Say = 0,
    Yell = 1,
    Party = 2,
}

/// <summary>Where the host's calls and results go. Saved as a number: never renumber, only append.</summary>
public enum CallChannel
{
    Venue = 0, // yell in a private room; party, or alliance when in one, in public
    Say = 1,
    Yell = 2,
    Party = 3,
    Alliance = 4, // always /a; the game refuses it when you are not in an alliance
}

/// <summary>What a player wears; the winner's pick takes one off.</summary>
public enum Piece
{
    Top = 0,
    Bottom = 1,
    Shoes = 2,
}

/// <summary>The kinds of game line Auto-post can send; each can be held back for Post. Saved as a number: never renumber, only append.</summary>
public enum CallKind
{
    RoundStart = 0,
    Call = 1,
    Win = 2,
    Strip = 3, // the strip line and the strip-and-out line
    GameOver = 4,
    WrongRoll = 5,
}

public enum Phase { Setup, Opening, Turns, Pick, GameOver }

/// <summary>What the world overlay draws under a player.</summary>
public enum Marker { None, Seated, Opening, Turn, Starter, Winner, Next }

public enum RollOutcome
{
    Ignored,          // not a player, out, or nothing expected
    OpeningRecorded,
    OpeningWrongMax,  // not recorded: the player rerolls a bare /random
    AlreadyRolled,
    OrderSet,         // last opening roll in: turns start
    OutOfTurn,
    WrongMax,         // held as Pending until the host accepts it or the player rerolls
    Next,
    Won,
}

/// <summary>What became of one roll, for the history.</summary>
public enum RollNote
{
    Opening,
    OpeningWrongNumber, // not counted
    OpeningRepeat,      // ignored
    OutOfTurn,          // ignored
    WrongNumber,        // waiting for the host, or not counted if they rerolled
    Accepted,           // a wrong number the host accepted
    Counted,
    Won,
    Void,               // no longer counts after a host edit: the round was already won, or the player left
}

public sealed class Player
{
    public string Name { get; set; } = "";
    public List<Piece> Worn { get; } = [.. DeathrollGame.AllPieces];
    public int Lives => Worn.Count;
    public int Wins { get; set; }
    public int? Opening { get; set; }
    public bool IsDummy { get; init; }
    public bool Out => Lives <= 0;
}

public sealed record RoundRecord(int Round, string Winner, int WinningRoll, string Target, Piece Piece, int TargetLivesLeft);

/// <summary>
/// One game's results and rolls: a finished game kept by New game, or a snapshot of the game in play.
/// Champion: the last one standing, null while the game is unfinished or when it was abandoned.
/// </summary>
public sealed record GameRecord(int Number, IReadOnlyList<RoundRecord> History, IReadOnlyList<RollRecord> RollLog, string? Champion = null);

/// <summary>Accepted: the host took this wrong-number roll (a replay after an edit takes it again at the same point).</summary>
public sealed record RollRecord(int Round, string Player, int Value, int? Max, RollNote Note, bool Accepted = false);

public readonly record struct PendingRoll(Player Player, int Value, int? Max, int LogIndex);

/// <summary>Pure Strip Deathroll state: no Dalamud types, so the test project compiles it as is.</summary>
public sealed class DeathrollGame
{
    public static readonly Piece[] AllPieces = [Piece.Top, Piece.Bottom, Piece.Shoes];
    public static readonly int StartLives = AllPieces.Length;
    public const int OpeningMax = 999; // what a bare /random rolls up to

    // Debug-mode stand-ins; valid "Forename Surname" names so every code path treats them like players.
    public static readonly string[] DummyNames =
        ["Dummy Alpha", "Dummy Bravo", "Dummy Charlie", "Dummy Delta", "Dummy Echo", "Dummy Foxtrot", "Dummy Golf", "Dummy Hotel",
         "Dummy India", "Dummy Juliett", "Dummy Kilo", "Dummy Lima", "Dummy Mike", "Dummy November", "Dummy Oscar", "Dummy Papa",
         "Dummy Quebec", "Dummy Romeo", "Dummy Sierra", "Dummy Tango", "Dummy Uniform", "Dummy Victor", "Dummy Whiskey", "Dummy Xray"];

    private int turnSerial;
    private Player? roundStarter; // who starts the current round's turns without opening rolls; null = the round opens with rolls

    // What else happened in the round in play besides its rolls, so a replay after an edit re-runs the round as it went:
    // the seating it started with, then each accept and seat change at the point it was made (after RollsIn of its rolls).
    private enum RoundEventKind { Accept, Add, Remove, MoveTo }
    private sealed record RoundEvent(int RollsIn, RoundEventKind Kind, Player Player, int Index = 0);
    private readonly List<RoundEvent> roundEvents = [];
    private Player[] roundSeating = [];
    private Player? roundStarterAtStart;
    private bool replaying;

    /// <summary>A round is in play: its rolls can still be edited, so what changes in it is noted for the replay.</summary>
    private bool RoundInPlay => Round > 0 && Phase is Phase.Opening or Phase.Turns or Phase.Pick;

    /// <summary>How many rolls of the round in play are logged so far (during a replay: re-run so far).</summary>
    private int RollsIn => RollLog.Count - RoundStart;

    private void Note(RoundEventKind kind, Player p, int index = 0)
    {
        if (RoundInPlay && !replaying) roundEvents.Add(new RoundEvent(RollsIn, kind, p, index));
    }

    /// <summary>A fresh round (not a replay): remember how it starts, and forget the last round's seat changes.</summary>
    private void FreshRound()
    {
        roundEvents.Clear();
        roundSeating = [.. Players];
        roundStarterAtStart = roundStarter;
    }

    public List<Player> Players { get; } = [];
    public List<RoundRecord> History { get; } = [];
    public List<RollRecord> RollLog { get; } = [];

    /// <summary>The game in play is Game N; New game files it under PastGames and starts Game N+1.</summary>
    public int GameNumber { get; private set; } = 1;

    /// <summary>Earlier games, oldest first. Kept until the host clears the history (or the plugin reloads).</summary>
    public List<GameRecord> PastGames { get; } = [];

    /// <summary>Every game with something in it, oldest first: the past games, then the game in play.</summary>
    public IEnumerable<GameRecord> AllGames =>
        History.Count == 0 && RollLog.Count == 0 ? PastGames : PastGames.Append(Snapshot());

    private GameRecord Snapshot() => new(GameNumber, History.ToList(), RollLog.ToList(), Champion);

    /// <summary>The last one standing once the game is over; null before. Kept by name, so a player listed or moved afterwards changes nothing.</summary>
    public string? Champion => Phase == Phase.GameOver ? champion : null;
    private string? champion;

    /// <summary>
    /// Names the turn by what it is, not by how the state got there: the round (and which start of it), how many rolls of
    /// it have counted, and who is up. So a wrong roll, a roll edit or replay that leaves the turn where it was, and a
    /// player listed, removed or moved elsewhere at the table all keep it; the turn moving, a restart, a new round or game
    /// change it. In the opening it is the whole opening (the wrong-roll guard adds the player).
    /// </summary>
    public string TurnId => Phase switch
    {
        Phase.Opening => $"{GameNumber}/{Round}/{roundSerial}/o",
        Phase.Turns => $"{GameNumber}/{Round}/{roundSerial}/t/{CountedThisRound}/{Current?.Name}",
        _ => $"{GameNumber}/{Round}/{roundSerial}/{Phase}",
    };

    private int CountedThisRound => RollLog.Skip(RoundStart).Count(r => r.Note is RollNote.Counted or RollNote.Accepted or RollNote.Won);
    private int roundSerial; // one per start of a round (a restart included) and per new game or cleared table; a replay keeps it

    /// <summary>
    /// The current game's lines can go too once it is over, or between rounds when nobody listed has a result in it:
    /// the results are what a listed player's pieces and wins come from (and what a returning player gets back).
    /// </summary>
    public bool CanClearCurrentHistory =>
        Phase == Phase.GameOver || Phase == Phase.Setup && !Players.Any(p => History.Any(h => h.Winner == p.Name || h.Target == p.Name));

    /// <summary>
    /// Drops the past games, and the current game's results and rolls when no round is in play. Players, pieces and the
    /// round number are untouched. With nothing left, numbering starts again at Game 1.
    /// </summary>
    public void ClearHistory()
    {
        PastGames.Clear();
        if (CanClearCurrentHistory)
        {
            if (Phase == Phase.Setup) Round = 0; // its rounds are gone: the next one is round 1 again, opening with rolls
            History.Clear();
            RollLog.Clear();
        }

        if (History.Count == 0 && RollLog.Count == 0) GameNumber = 1;
    }
    public Phase Phase { get; private set; } = Phase.Setup;
    public int Round { get; private set; }
    public Player? Current { get; private set; }
    /// <summary>Who started the turns this round: the highest opening roll, or the player after last round's winner. Highlighted, the list is never reordered.</summary>
    public Player? Starter { get; private set; }
    /// <summary>The number the current player rolls to; null = a fresh /random (the player starting the turns).</summary>
    public int? Limit { get; private set; }
    public Player? Winner { get; private set; }
    public int WinningRoll { get; private set; }
    public PendingRoll? Pending { get; private set; }
    public bool OpeningTie { get; private set; }

    public IEnumerable<Player> Active => Players.Where(p => !p.Out);

    /// <summary>Who the game is waiting on: everyone without an opening roll, or the current player.</summary>
    public IEnumerable<Player> Waiting => Phase switch
    {
        Phase.Opening => Active.Where(p => p.Opening is null),
        Phase.Turns when Pending is null && Current is not null => [Current],
        _ => [],
    };

    /// <summary>Changes whenever the game moves on; null when no roll is awaited. Drives the reminder clock.</summary>
    public string? WaitingKey => Phase switch
    {
        // By progress, like TurnId: the next opening roll in or the turn moving starts a new wait; a player listed, moved or
        // removed, or an edit that leaves the turn where it was, does not (so its first reminder is not sent to chat twice).
        Phase.Opening => $"{TurnId}/{RollLog.Skip(RoundStart).Count(r => r.Note == RollNote.Opening)}",
        Phase.Turns when Pending is null && Current is not null => TurnId,
        _ => null,
    };

    /// <summary>The world marker for <paramref name="p"/>: who is up, who owes an opening roll, who won; none once out.</summary>
    public Marker MarkerFor(Player p) => p.Out ? Marker.None : Phase switch
    {
        Phase.Opening => p.Opening is null ? Marker.Opening : Marker.Seated,
        Phase.Turns => p == Current ? Marker.Turn : Current is not null && p == NextActive(Current) ? Marker.Next : p == Starter ? Marker.Starter : Marker.Seated,
        Phase.Pick or Phase.GameOver => p == Winner ? Marker.Winner : Marker.Seated,
        _ => Marker.Seated,
    };

    public static bool IsEnding(int roll) => roll is 0 or 1 or 69 or 666; // /random rolls 0 to N

    /// <summary>The player's most recent roll this game, any round, counted or not; null before their first.</summary>
    public RollRecord? LastRoll(Player p) => RollLog.LastOrDefault(r => r.Player == p.Name);

    /// <summary>Rolls that changed nothing: shown greyed.</summary>
    public static bool NotCounted(RollNote n) => n is RollNote.OpeningRepeat or RollNote.OpeningWrongNumber or RollNote.OutOfTurn or RollNote.WrongNumber or RollNote.Void;

    public Player? Find(string rawName) => Players.FirstOrDefault(p => Names.Matches(p.Name, rawName));

    public bool Add(string name, bool dummy = false)
    {
        name = name.Trim();
        if (!Names.IsValid(name) || Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        var p = new Player { Name = name, IsDummy = dummy };
        // Back in the same game (removed, or the list cleared, then listed again): what they lost stays lost, wins stay won.
        foreach (var h in History)
        {
            if (string.Equals(h.Target, name, StringComparison.OrdinalIgnoreCase)) p.Worn.Remove(h.Piece);
            if (string.Equals(h.Winner, name, StringComparison.OrdinalIgnoreCase)) p.Wins++;
        }
        Players.Add(p);
        Note(RoundEventKind.Add, p);
        return true;
    }

    public bool AddDummy() => DummyNames.Any(n => Add(n, true));

    public void RemoveDummies()
    {
        foreach (var d in Players.Where(p => p.IsDummy).ToList()) Remove(d);
    }

    /// <summary>Empties the player list; the round in play (if any) stops. History and the roll log stay until New game.</summary>
    public void ClearPlayers()
    {
        Players.Clear();
        roundEvents.Clear();
        Current = Winner = Starter = roundStarter = null;
        Pending = null;
        Limit = null;
        turnSerial++;
        roundSerial++;
        if (Phase != Phase.Setup) Phase = Phase.Setup;
    }

    public void Remove(Player p)
    {
        if (!Players.Contains(p)) return;
        Note(RoundEventKind.Remove, p);
        if (roundStarter == p) roundStarter = Players.Count > 1 ? NextActive(p) : null; // a replay after an edit still has a starter
        if (Current == p) Current = Players.Count > 1 ? NextActive(p) : null;
        if (Starter == p) Starter = null;
        if (Winner == p && Phase == Phase.Pick) { Winner = null; Phase = Phase.Setup; } // the round stops; a finished game stays finished
        if (Pending?.Player == p) Pending = null;
        Players.Remove(p);
        turnSerial++;
        if (Phase == Phase.Opening && Active.Any() && Active.All(x => x.Opening is not null)) SetOrder();
        if (Phase is Phase.Opening or Phase.Turns or Phase.Pick && Active.Count() < 2) { Phase = Phase.Setup; Current = Winner = null; Pending = null; } // the round stops
    }

    public void Move(Player p, int delta)
    {
        var i = Players.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Players.Count) return;
        (Players[i], Players[j]) = (Players[j], Players[i]); // the same as moving p to seat j
        Note(RoundEventKind.MoveTo, p, j);
    }

    /// <summary>Moves p to seat index (the others shift up or down one), for dragging a seat around the circle.</summary>
    public void MoveTo(Player p, int index)
    {
        if (!Players.Remove(p)) return;
        index = Math.Clamp(index, 0, Players.Count);
        Players.Insert(index, p);
        Note(RoundEventKind.MoveTo, p, index);
    }

    public bool CanStartRound => Phase is Phase.Setup or Phase.Opening or Phase.Turns && Active.Count() >= 2;

    /// <summary>
    /// Starts the next round, or restarts the round in play (or a stopped one with no result) under the same number with
    /// its rolls dropped. Round 1 opens with everyone rolling; from round 2 the player next to the previous round's winner
    /// starts with a fresh /random, a restart included.
    /// </summary>
    public void StartRound()
    {
        if (!CanStartRound) return;
        if (Round > 0 && History.All(h => h.Round != Round)) RollLog.RemoveAll(r => r.Round == Round);
        else Round++;
        roundStarter = Round > 1 ? StarterAfterLastWinner() ?? roundStarter : null; // winner gone: Remove already moved roundStarter on
        roundSerial++;
        FreshRound(); // a new (or restarted) round: everyone listed now plays it from its start
        BeginRound();
    }

    /// <summary>The next active player clockwise from the last round's winner; null when there is none or they left.</summary>
    private Player? StarterAfterLastWinner() =>
        History.LastOrDefault(h => h.Round < Round) is { } last && Players.FirstOrDefault(p => p.Name == last.Winner) is { } w ? NextActive(w) : null;

    /// <summary>The state at the start of the round: opening rolls, or turns from roundStarter with a fresh /random.</summary>
    private void BeginRound()
    {
        foreach (var p in Players) p.Opening = null;
        Current = Winner = Starter = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
        WinningRoll = 0;
        turnSerial++;
        if (roundStarter is { Out: false } s && Players.Contains(s))
        {
            Starter = Current = s;
            Phase = Phase.Turns;
        }
        else
        {
            Phase = Phase.Opening;
        }
    }

    /// <summary>Everyone back to 3 pieces and round 0; the game so far (if it had any rolls) is kept as Game N in PastGames.</summary>
    public void NewGame()
    {
        if (History.Count > 0 || RollLog.Count > 0)
        {
            PastGames.Add(Snapshot());
            GameNumber++;
        }

        foreach (var p in Players)
        {
            p.Worn.Clear();
            p.Worn.AddRange(AllPieces);
            p.Wins = 0;
            p.Opening = null;
        }

        History.Clear();
        RollLog.Clear();
        roundEvents.Clear();
        Round = 0;
        Phase = Phase.Setup;
        Current = Winner = Starter = roundStarter = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
        WinningRoll = 0;
        turnSerial++;
        roundSerial++;
    }

    /// <summary>The host may fix this round's rolls (and add missed ones) from its start until the next round begins.</summary>
    public bool CanEditRolls => Round > 0 && Phase is Phase.Opening or Phase.Turns or Phase.Pick;

    /// <summary>True when <paramref name="index"/> is a roll of the round in play, so it may be edited or deleted.</summary>
    public bool IsEditable(int index) => CanEditRolls && index >= 0 && index < RollLog.Count && RollLog[index].Round == Round;

    /// <summary>A number a real roll could produce: 0 to N, N at least 2; max null is a bare /random (0-999).</summary>
    public static bool IsPossible(int value, int? max) => max is null or >= 2 && value >= 0 && value <= (max ?? OpeningMax);

    /// <summary>Changes one roll of the round in play and works the round out again from its start. False when not allowed.</summary>
    public bool EditRoll(int index, int value, int? max)
    {
        if (!IsEditable(index) || !IsPossible(value, max)) return false;
        RollLog[index] = RollLog[index] with { Value = value, Max = max };
        Replay();
        return true;
    }

    /// <summary>Drops one roll of the round in play and works the round out again from its start.</summary>
    public bool DeleteRoll(int index)
    {
        if (!IsEditable(index)) return false;
        var at = index - RoundStart;
        RollLog.RemoveAt(index);
        for (var i = 0; i < roundEvents.Count; i++) // what happened after the deleted roll came in now has one roll fewer before it
            if (roundEvents[i].RollsIn > at) roundEvents[i] = roundEvents[i] with { RollsIn = roundEvents[i].RollsIn - 1 };
        Replay();
        return true;
    }

    /// <summary>Where the round in play starts in the roll log: its rolls are always the last ones.</summary>
    private int RoundStart => RollLog.FindLastIndex(r => r.Round != Round) + 1;

    /// <summary>
    /// Re-runs the round in play from its start over its logged rolls, in order: every roll is judged again, and the
    /// host's accepts and seat changes (players added, removed or moved) are made again at the point they were made,
    /// after the same rolls, with the seating the round started with. So the turn, the limit and a win follow an edit,
    /// and an edit that changes nothing changes nothing. Each roll keeps one log line; a roll that no longer counts
    /// (after the win, or out of turn now) stays, marked as such, so a second edit can bring it back.
    /// </summary>
    private void Replay()
    {
        var first = RoundStart;
        var inputs = RollLog.Skip(first).ToList();
        RollLog.RemoveRange(first, inputs.Count);
        var events = roundEvents.ToList();
        var seatsNow = Players.ToList();
        replaying = true;
        try
        {
            Players.Clear();
            Players.AddRange(roundSeating);
            roundStarter = roundStarterAtStart;
            BeginRound();
            var e = 0;
            for (var k = 0; ; k++)
            {
                for (; e < events.Count && events[e].RollsIn <= k; e++) Redo(events[e]);
                if (k == inputs.Count) break;
                var r = inputs[k];
                var before = RollLog.Count;
                OnRoll(Players.FirstOrDefault(x => x.Name == r.Player), r.Value, r.Max);
                if (RollLog.Count == before) RollLog.Add(r with { Note = RollNote.Void, Accepted = false });
            }
        }
        finally
        {
            replaying = false;
        }

        if (!Players.SequenceEqual(seatsNow)) { Players.Clear(); Players.AddRange(seatsNow); } // never: the same changes in the same order
    }

    private void Redo(RoundEvent ev)
    {
        switch (ev.Kind)
        {
            case RoundEventKind.Accept when Pending?.Player == ev.Player: AcceptPending(); break;
            case RoundEventKind.Add when !Players.Contains(ev.Player): ev.Player.Opening = null; Players.Add(ev.Player); break; // as new as when they joined
            case RoundEventKind.Remove: Remove(ev.Player); break;
            case RoundEventKind.MoveTo: MoveTo(ev.Player, ev.Index); break;
        }
    }

    /// <summary>A roll read from chat (or simulated for a dummy). <paramref name="max"/> is null for a bare /random.</summary>
    public RollOutcome OnRoll(Player? p, int value, int? max)
    {
        if (p is null || p.Out) return RollOutcome.Ignored;

        if (Phase == Phase.Opening)
        {
            if (p.Opening is not null) { Log(p, value, max, RollNote.OpeningRepeat); return RollOutcome.AlreadyRolled; }
            if (max is not (null or OpeningMax)) { Log(p, value, max, RollNote.OpeningWrongNumber); return RollOutcome.OpeningWrongMax; }
            p.Opening = value;
            Log(p, value, max, RollNote.Opening);
            if (Active.All(x => x.Opening is not null)) { SetOrder(); return RollOutcome.OrderSet; }
            return RollOutcome.OpeningRecorded;
        }

        if (Phase != Phase.Turns) return RollOutcome.Ignored;
        if (p != Current) { Log(p, value, max, RollNote.OutOfTurn); return RollOutcome.OutOfTurn; }
        if (Limit is null ? max is not (null or OpeningMax) : max != Limit)
        {
            Pending = new PendingRoll(p, value, max, Log(p, value, max, RollNote.WrongNumber));
            return RollOutcome.WrongMax;
        }

        return Apply(value, Log(p, value, max, RollNote.Counted), RollNote.Counted);
    }

    /// <summary>The host counts the waiting wrong-number roll (noted, so a replay accepts it again at the same point).</summary>
    public RollOutcome AcceptPending()
    {
        if (Pending is not { } pending || Phase != Phase.Turns || pending.Player != Current) return RollOutcome.Ignored;
        Note(RoundEventKind.Accept, pending.Player);
        RollLog[pending.LogIndex] = RollLog[pending.LogIndex] with { Accepted = true };
        return Apply(pending.Value, pending.LogIndex, RollNote.Accepted);
    }

    /// <summary>The winner names who strips (themselves included) and which piece. False when the pick is not allowed.</summary>
    public bool Pick(Player target, Piece piece)
    {
        if (Phase != Phase.Pick || Winner is null || target.Out || !Players.Contains(target) || !target.Worn.Contains(piece)) return false;
        Winner.Wins++;
        target.Worn.Remove(piece);
        History.Add(new RoundRecord(Round, Winner.Name, WinningRoll, target.Name, piece, target.Lives));
        if (Active.Count() <= 1)
        {
            champion = Active.FirstOrDefault()?.Name;
            Phase = Phase.GameOver;
            return true;
        }
        // Round 2 on: no opening roll; the next player in the list after the winner starts with a fresh /random.
        // The piece is already off, so a player who just went out is skipped (NextActive ignores Out).
        roundStarter = NextActive(Winner);
        Round++;
        roundSerial++;
        FreshRound();
        BeginRound();
        return true;
    }

    /// <summary>Pick with the first piece the target still wears.</summary>
    public bool Pick(Player target) => target.Worn.Count > 0 && Pick(target, target.Worn[0]);

    private int Log(Player p, int value, int? max, RollNote note)
    {
        RollLog.Add(new RollRecord(Round, p.Name, value, max, note));
        return RollLog.Count - 1;
    }

    private RollOutcome Apply(int value, int logIndex, RollNote note)
    {
        Pending = null;
        turnSerial++;
        var ending = IsEnding(value);
        RollLog[logIndex] = RollLog[logIndex] with { Note = ending ? RollNote.Won : note };
        if (ending)
        {
            Winner = Current;
            WinningRoll = value;
            Phase = Phase.Pick;
            return RollOutcome.Won;
        }

        Limit = Math.Max(value, 2);
        Current = NextActive(Current!);
        return RollOutcome.Next;
    }

    // The highest opening roll starts; the turn then goes down the list as the host arranged it (never reordered).
    private void SetOrder()
    {
        var top = Active.MaxBy(p => p.Opening)!; // MaxBy keeps the first of equal keys: list order breaks a tie
        OpeningTie = Active.Count(p => p.Opening == top.Opening) > 1;
        Starter = Current = top;
        Limit = null; // the starting player rolls a fresh /random
        turnSerial++;
        Phase = Phase.Turns;
    }

    /// <summary>The next player in turn order after from (wrapping, skipping anyone out), or null if nobody else is in.</summary>
    public Player? NextActive(Player from)
    {
        var i = Players.IndexOf(from);
        for (var step = 1; step <= Players.Count; step++)
        {
            var p = Players[(i + step) % Players.Count];
            if (!p.Out && p != from) return p;
        }

        return null;
    }
}

/// <summary>Fires a reminder after a quiet interval, at most MaxPerTurn times for one wait; any change of key restarts it.</summary>
public sealed class ReminderClock
{
    public const int MaxPerTurn = 3;
    private string? key;
    private string? lastKey; // the wait before this one, and how many reminders it had: coming back to it is not a new wait
    private int lastSent;
    private long dueAt;
    private long? pausedLeft; // ms on the clock while paused; a new wait while paused starts full and stays paused

    public int Sent { get; private set; }
    public bool Paused => pausedLeft is not null;

    /// <summary>
    /// Only the first reminder of a wait goes to chat; the rest of that wait goes to the host's Echo. A new wait (the
    /// next player up, or the next opening roll in) starts with a chat reminder again.
    /// </summary>
    public bool NextGoesToChat => Sent == 0;

    /// <summary>True right after Due fired the first reminder of this wait.</summary>
    public bool LastWentToChat => Sent == 1;

    public void Pause(long now) => pausedLeft ??= Math.Max(0, dueAt - now);

    public void Resume(long now)
    {
        if (pausedLeft is not { } left) return;
        dueAt = now + left;
        pausedLeft = null;
    }

    /// <summary>Whole seconds (rounded up) until the next reminder, frozen while paused; null when none is coming.</summary>
    public int? SecondsLeft(long now) => key is null || Sent >= MaxPerTurn ? null : (int)((pausedLeft ?? Math.Max(0, dueAt - now)) + 999) / 1000;

    public bool Due(string? stateKey, long now, int intervalMs)
    {
        if (stateKey != key)
        {
            // Back to the wait just left (a wrong roll waited for the host and was then deleted, or an edit put the turn
            // back): its reminders so far still count, so its first one does not go to chat twice.
            var back = stateKey is not null && stateKey == lastKey;
            if (key is not null) (lastKey, lastSent) = (key, Sent);
            key = stateKey;
            dueAt = now + intervalMs;
            if (pausedLeft is not null) pausedLeft = intervalMs;
            Sent = back ? lastSent : 0;
            return false;
        }

        if (key is null || pausedLeft is not null || Sent >= MaxPerTurn || now < dueAt) return false;
        Sent++;
        dueAt = now + intervalMs;
        return true;
    }
}

/// <summary>Character names: validation (a trust boundary, names end up inside chat lines) and matching chat text.</summary>
public static partial class Names
{
    // "Forename Surname": letters with optional ' or -, 2-15 chars each.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-]{1,14} [A-Za-z][A-Za-z'\-]{1,14}$")]
    private static partial Regex NameRegex();

    public static bool IsValid(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>
    /// True when <paramref name="raw"/> contains the name as a whole: a party-slot glyph may precede it and a
    /// cross-world glyph plus world name may follow it, but "Ann Lee" never matches "Ann Leez".
    /// </summary>
    public static bool Matches(string name, string raw)
    {
        if (name.Length == 0) return false;
        for (var i = raw.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0; i = raw.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + name.Length;
            var before = i == 0 || !char.IsLetter(raw[i - 1]) && raw[i - 1] is not ('\'' or '-');
            var after = end == raw.Length || !char.IsLower(raw[end]) && raw[end] is not ('\'' or '-' or ' ');
            if (before && after) return true;
        }

        return false;
    }
}

public readonly record struct ParsedRoll(string? Name, int Value, int? Max);

/// <summary>Roll text parsing (English client). Formats agreed by ChatDeathRoll2 and FFXIVGamblePyon.</summary>
public static partial class Rolls
{
    // "Random! You roll a 7 (out of 10)." / "Random! Ann Lee rolls a 7." (bare /random has no "out of")
    [GeneratedRegex(@"^Random! (?:You roll|(.+?) rolls) a ([0-9]{1,9})(?: \(out of ([0-9]{1,9})\))?\.$")]
    private static partial Regex RandomRegex();

    // "/dice" in a chat mode: "Random! (1-10) 7", the roller is the message sender; a bare /dice has no range.
    [GeneratedRegex(@"^Random! (?:\(1-([0-9]{1,9})\) )?([0-9]{1,9})$")]
    private static partial Regex DiceRegex();

    /// <summary>A /random result; Name is null for your own roll.</summary>
    public static ParsedRoll? ParseRandom(string text)
    {
        var m = RandomRegex().Match(text.Trim());
        if (!m.Success) return null;
        return Checked(new ParsedRoll(m.Groups[1].Success ? m.Groups[1].Value : null, int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null));
    }

    /// <summary>A /dice result; the caller supplies the name from the sender.</summary>
    public static ParsedRoll? ParseDice(string text)
    {
        var m = DiceRegex().Match(text.Trim());
        if (!m.Success) return null;
        return Checked(new ParsedRoll(null, int.Parse(m.Groups[2].Value), m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null));
    }

    // Party chat is typed text, so a /dice-shaped line can be forged: drop numbers no real roll produces.
    // /random and /dice roll 0 to N, and N is at least 2.
    private static ParsedRoll? Checked(ParsedRoll r) => r.Value >= 0 && r.Max is null or >= 2 && r.Value <= (r.Max ?? int.MaxValue) ? r : null;
}

/// <summary>
/// The host's wording for every line the plugin posts. {placeholders} are filled in when the line is made;
/// a blank text falls back to the default, so a line can never go out empty.
/// </summary>
[Serializable]
public sealed class Messages
{
    public static readonly Messages Default = new(); // never changed: the fallback for blank texts

    public string Opening { get; set; } = "Strip Deathroll round {round}! Everyone roll {roll} - highest goes first.";
    public string Call { get; set; } = "{name}, roll {roll}";
    public string Won { get; set; } = "{name} rolled {number} and wins round {round}! {name}, who strips?";
    public string WonAndPicked { get; set; } = "{name} rolled {number} and wins round {round}!"; // the strip line follows in the same post
    public string Stripped { get; set; } = "{name} loses their {piece}! {left} left.";
    public string StrippedOut { get; set; } = "{name} loses their {piece}, the last piece, and is out!";
    public string GameOver { get; set; } = "{name} is the last one standing and wins Strip Deathroll!";
    public string ReminderTurn { get; set; } = "Reminder: {name}, it's your turn - roll {roll}";
    public string ReminderOpening { get; set; } = "Still waiting on {names}: roll {roll} to start the round.";
    public string WrongRoll { get; set; } = "{name}, that was out of {max} - please roll {roll}";
}

/// <summary>Every line the plugin posts, and the command prefixes it posts them with.</summary>
public static class Lines
{
    public static string KindLabel(CallKind k) => k switch
    {
        CallKind.RoundStart => "Round start",
        CallKind.Call => "Call (whose turn, what to roll)",
        CallKind.Win => "Win",
        CallKind.Strip => "Strip, and strip-out",
        CallKind.GameOver => "Game over",
        CallKind.WrongRoll => "Wrong roll",
        _ => k.ToString(),
    };

    /// <summary>
    /// Which lines of one event Auto-post sends now and which wait for Post, each in event order. Auto-post off: all
    /// wait. On: only the lines of a held kind wait; the others go, so holding one kind never holds back another.
    /// </summary>
    public static (List<string> Send, List<string> Hold) SplitAuto(bool autoPost, IReadOnlyCollection<CallKind> held, params (CallKind Kind, string Line)[] lines)
    {
        bool Waits((CallKind Kind, string Line) l) => !autoPost || held.Contains(l.Kind);
        return (lines.Where(l => !Waits(l)).Select(l => l.Line).ToList(), lines.Where(Waits).Select(l => l.Line).ToList());
    }

    /// <summary>What became of a roll, as the History tab and the CSV export word it.</summary>
    public static string NoteLabel(RollNote n) => n switch
    {
        RollNote.Opening => "opening",
        RollNote.OpeningWrongNumber => "opening, wrong number (not counted)",
        RollNote.OpeningRepeat => "second opening roll (ignored)",
        RollNote.OutOfTurn => "out of turn (ignored)",
        RollNote.WrongNumber => "wrong number (not counted unless accepted)",
        RollNote.Accepted => "wrong number, accepted",
        RollNote.Won => "winning roll",
        RollNote.Void => "no longer counts (round already won, or player left)",
        _ => "counted",
    };

    /// <summary>
    /// <paramref name="template"/> (or <paramref name="fallback"/> when blank) with each {key} replaced. Values are
    /// plugin-made (validated names, numbers, roll commands), so they can never hold a {key} of their own.
    /// </summary>
    public static string Fill(string? template, string fallback, params (string Key, object Value)[] values)
    {
        var s = string.IsNullOrWhiteSpace(template) ? fallback : template.Trim();
        foreach (var (k, v) in values) s = s.Replace("{" + k + "}", v.ToString());
        return s;
    }

    /// <summary>The roll command players are asked to use; n null = the bare opening roll.</summary>
    public static string RollCommand(Venue venue, bool alliance, int? n)
    {
        var cmd = venue == Venue.Private ? "/random" : alliance ? "/dice alliance" : "/dice party";
        return n is null ? cmd : $"{cmd} {n}";
    }

    public static string AnnouncePrefix(Venue venue, bool alliance) => venue == Venue.Private ? "/y" : alliance ? "/a" : "/p";

    public static string CallPrefix(CallChannel c, Venue venue, bool alliance) => c switch
    {
        CallChannel.Say => "/s",
        CallChannel.Yell => "/y",
        CallChannel.Party => "/p",
        CallChannel.Alliance => "/a",
        _ => AnnouncePrefix(venue, alliance),
    };

    public static string TextPrefix(TextChannel c) => c switch
    {
        TextChannel.Yell => "/y",
        TextChannel.Party => "/p",
        _ => "/s",
    };

    private static readonly Messages D = Messages.Default;

    public static string Opening(int round, string cmd, Messages? m = null) =>
        Fill(m?.Opening, D.Opening, ("round", round), ("roll", cmd));

    public static string Call(string name, string cmd, Messages? m = null) => Fill(m?.Call, D.Call, ("name", name), ("roll", cmd));

    /// <summary>ask = false when the strip result follows in the same post, so the question is already answered.</summary>
    public static string Won(string winner, int roll, int round, bool ask = true, Messages? m = null) => ask
        ? Fill(m?.Won, D.Won, ("name", winner), ("number", roll), ("round", round))
        : Fill(m?.WonAndPicked, D.WonAndPicked, ("name", winner), ("number", roll), ("round", round));

    public static string PieceName(Piece p) => p.ToString().ToLowerInvariant();

    public static string Stripped(string target, Piece piece, int livesLeft, Messages? m = null) => livesLeft > 0
        ? Fill(m?.Stripped, D.Stripped, ("name", target), ("piece", PieceName(piece)), ("left", $"{livesLeft} {(livesLeft == 1 ? "piece" : "pieces")}"))
        : Fill(m?.StrippedOut, D.StrippedOut, ("name", target), ("piece", PieceName(piece)));

    /// <summary>The timed nudge; a long opening list is cut so the line stays short.</summary>
    public static string Reminder(IReadOnlyList<string> names, string cmd, bool opening, Messages? m = null)
    {
        if (!opening) return Fill(m?.ReminderTurn, D.ReminderTurn, ("name", names[0]), ("roll", cmd));
        var shown = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" and {names.Count - 6} more" : "");
        return Fill(m?.ReminderOpening, D.ReminderOpening, ("names", shown), ("roll", cmd));
    }

    public static string GameOver(string name, Messages? m = null) => Fill(m?.GameOver, D.GameOver, ("name", name));

    /// <summary>Posted once per turn when the player up rolls the wrong number: what they rolled to, and the right roll.</summary>
    public static string WrongRoll(string name, int value, int max, string cmd, Messages? m = null) =>
        Fill(m?.WrongRoll, D.WrongRoll, ("name", name), ("number", value), ("max", max), ("roll", cmd));

    /// <summary>The reminder channel saved before version 2 (Say or Party) as a call channel.</summary>
    public static CallChannel ReminderChannelFromV1(TextChannel old) => old switch
    {
        TextChannel.Party => CallChannel.Party,
        TextChannel.Yell => CallChannel.Yell,
        _ => CallChannel.Say,
    };

    /// <summary>A line is safe to send only as one short line that is not a command of its own.</summary>
    public static bool IsSendable(string line) =>
        line.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(line) <= 400 && !line.StartsWith('/') && !line.Any(char.IsControl);

    /// <summary>Rules/ad text: one chat line per non-empty text line, capped so a paste cannot flood chat.</summary>
    public const int MaxTextLines = 10;

    public static List<string> TextLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(MaxTextLines).ToList();
}

/// <summary>
/// Every line the plugin sends (rolls, calls, reminders, rules/ad) waits here: one at a time, a random 2-5 s apart (within the host's limits),
/// a line already waiting is not added twice, and the queue is capped so nothing can pile up into spam.
/// </summary>
public sealed class Outbox
{
    public const int MinGapMs = 2000, MaxGapMs = 5000;
    public const int MaxWaiting = Lines.MaxTextLines + 2; // a full rules post plus a call and a roll

    private readonly Queue<(string? Prefix, string Text, bool FreeText)> waiting = new();
    private long nextAt;

    public int Count => waiting.Count;

    /// <summary>False when the queue is full or the same line is already waiting.</summary>
    public bool Add(string? prefix, string text, bool freeText)
    {
        var item = (prefix, text, freeText);
        if (waiting.Count >= MaxWaiting || waiting.Contains(item)) return false;
        waiting.Enqueue(item);
        return true;
    }

    /// <summary>A random gap from minSeconds to maxSeconds (to the millisecond), both kept inside 2-5 s.</summary>
    public static int Gap(Random rng, int minSeconds, int maxSeconds)
    {
        var lo = Math.Clamp(minSeconds * 1000, MinGapMs, MaxGapMs);
        var hi = Math.Clamp(maxSeconds * 1000, lo, MaxGapMs);
        return rng.Next(lo, hi + 1);
    }

    /// <summary>The next line once the gap since the last one has passed; the following line waits gapMs after it.</summary>
    public (string? Prefix, string Text, bool FreeText)? Take(long now, int gapMs)
    {
        if (waiting.Count == 0 || now < nextAt) return null;
        nextAt = now + Math.Clamp(gapMs, MinGapMs, MaxGapMs);
        return waiting.Dequeue();
    }

    public void Clear() => waiting.Clear();
}

/// <summary>What changed since the last look, for the world markers' motion: new rolls and a hand-over of the turn.</summary>
public sealed class TurnCues
{
    private int rolls = -1; // -1: not looked yet, so nothing before the first look replays
    private Player? current;

    public (List<RollRecord> Rolls, (Player From, Player To)? Handover) Take(DeathrollGame g)
    {
        var from = rolls < 0 || rolls > g.RollLog.Count ? g.RollLog.Count : rolls; // first look, or the log was cleared
        var fresh = g.RollLog.GetRange(from, g.RollLog.Count - from);
        rolls = g.RollLog.Count;
        (Player, Player)? handover = current is { } a && g.Current is { } b && a != b ? (a, b) : null;
        current = g.Current;
        return (fresh, handover);
    }
}

/// <summary>One player's totals over every game in the history.</summary>
public sealed record TallyRow(string Player, int Games, int GamesWon, int RoundsWon, int PiecesLost, int TimesOut);

/// <summary>The running tally over all games in the history: games played and won, rounds won, pieces lost, times out.</summary>
public static class Tally
{
    public static List<TallyRow> Build(IEnumerable<GameRecord> games)
    {
        var rows = new Dictionary<string, (int Games, int GamesWon, int Rounds, int Lost, int Out)>();
        foreach (var g in games)
        {
            var names = g.RollLog.Select(r => r.Player).Concat(g.History.SelectMany(h => new[] { h.Winner, h.Target })).Distinct();
            foreach (var n in names)
            {
                var r = rows.GetValueOrDefault(n);
                rows[n] = r with { Games = r.Games + 1, GamesWon = r.GamesWon + (g.Champion == n ? 1 : 0) };
            }

            foreach (var h in g.History)
            {
                var w = rows[h.Winner];
                rows[h.Winner] = w with { Rounds = w.Rounds + 1 };
                var t = rows[h.Target];
                rows[h.Target] = t with { Lost = t.Lost + 1, Out = t.Out + (h.TargetLivesLeft <= 0 ? 1 : 0) };
            }
        }

        return rows.Select(kv => new TallyRow(kv.Key, kv.Value.Games, kv.Value.GamesWon, kv.Value.Rounds, kv.Value.Lost, kv.Value.Out))
            .OrderByDescending(r => r.GamesWon).ThenByDescending(r => r.RoundsWon).ThenBy(r => r.PiecesLost).ThenBy(r => r.Player, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>The history as CSV: per game and round, every roll in order, then a result line for a finished round.</summary>
public static class HistoryCsv
{
    public const string Header = "Game,Round,Line,Player,Roll,Out of,Note,Stripped,Piece,Pieces left";

    public static string Build(IEnumerable<GameRecord> games)
    {
        var sb = new System.Text.StringBuilder(Header).Append("\r\n");
        foreach (var g in games)
            foreach (var round in g.RollLog.Select(r => r.Round).Concat(g.History.Select(h => h.Round)).Distinct().Order())
            {
                foreach (var r in g.RollLog.Where(r => r.Round == round))
                    Row(sb, g.Number, round, "roll", r.Player, r.Value, r.Max ?? DeathrollGame.OpeningMax, Lines.NoteLabel(r.Note), "", "", "");
                if (g.History.FirstOrDefault(h => h.Round == round) is { } h)
                    Row(sb, g.Number, round, "result", h.Winner, h.WinningRoll, null, "won the round", h.Target, Lines.PieceName(h.Piece),
                        h.TargetLivesLeft > 0 ? h.TargetLivesLeft.ToString() : "out");
            }

        return sb.ToString();
    }

    private static void Row(System.Text.StringBuilder sb, int game, int round, string line, string player, int roll, int? outOf, string note,
        string stripped, string piece, string left) =>
        sb.AppendJoin(',', game.ToString(), round.ToString(), line, Field(player), roll.ToString(), outOf?.ToString() ?? "", Field(note),
            Field(stripped), piece, left).Append("\r\n");

    /// <summary>Quoted when it holds a comma, quote or line break; a leading = + - @ is defused so a spreadsheet never runs it.</summary>
    public static string Field(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : '"' + s.Replace("\"", "\"\"") + '"';
    }
}
