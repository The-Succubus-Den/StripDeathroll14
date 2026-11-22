using System.Text.RegularExpressions;

namespace StripDeathroll14;

/// <summary>Where the game is played. Saved as a number: never renumber, only append.</summary>
public enum Venue
{
    Private = 0, // say
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
    Venue = 0, // say in a private room; party, or alliance when in one, in public
    Say = 1,
    Yell = 2,
    Party = 3,
}

/// <summary>What a player wears; the winner's pick takes one off.</summary>
public enum Piece
{
    Top = 0,
    Bottom = 1,
    Shoes = 2,
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

/// <summary>One game's results and rolls: a finished game kept by New game, or a snapshot of the game in play.</summary>
public sealed record GameRecord(int Number, IReadOnlyList<RoundRecord> History, IReadOnlyList<RollRecord> RollLog);

/// <summary>Accepted: the host took this wrong-number roll, so a replay after an edit takes it again.</summary>
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

    public List<Player> Players { get; } = [];
    public List<RoundRecord> History { get; } = [];
    public List<RollRecord> RollLog { get; } = [];

    /// <summary>The game in play is Game N; New game files it under PastGames and starts Game N+1.</summary>
    public int GameNumber { get; private set; } = 1;

    /// <summary>Earlier games, oldest first. Kept until the host clears the history (or the plugin reloads).</summary>
    public List<GameRecord> PastGames { get; } = [];

    /// <summary>Every game with something in it, oldest first: the past games, then the game in play.</summary>
    public IEnumerable<GameRecord> AllGames =>
        History.Count == 0 && RollLog.Count == 0 ? PastGames : PastGames.Append(new GameRecord(GameNumber, History.ToList(), RollLog.ToList()));

    /// <summary>No round in play, so the current game's lines can go too (a round in play needs its rolls to be edited).</summary>
    public bool CanClearCurrentHistory => Phase is Phase.Setup or Phase.GameOver;

    /// <summary>
    /// Drops the past games, and the current game's results and rolls when no round is in play. Players, pieces and the
    /// round number are untouched. With nothing left, numbering starts again at Game 1.
    /// </summary>
    public void ClearHistory()
    {
        PastGames.Clear();
        if (CanClearCurrentHistory)
        {
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
        Phase.Opening => $"{Round}/o/{string.Join(",", Waiting.Select(p => p.Name))}",
        Phase.Turns when Pending is null && Current is not null => $"{Round}/t/{turnSerial}",
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
        Players.Add(new Player { Name = name, IsDummy = dummy });
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
        Current = Winner = Starter = roundStarter = null;
        Pending = null;
        Limit = null;
        turnSerial++;
        if (Phase != Phase.Setup) Phase = Phase.Setup;
    }

    public void Remove(Player p)
    {
        if (roundStarter == p) roundStarter = Players.Count > 1 ? NextActive(p) : null; // a replay after an edit still has a starter
        if (Current == p) Current = Players.Count > 1 ? NextActive(p) : null;
        if (Starter == p) Starter = null;
        if (Winner == p) { Winner = null; Phase = Phase.Setup; }
        if (Pending?.Player == p) Pending = null;
        Players.Remove(p);
        turnSerial++;
        if (Phase == Phase.Opening && Active.Any() && Active.All(x => x.Opening is not null)) SetOrder();
        if (Phase is Phase.Opening or Phase.Turns or Phase.Pick && Active.Count() < 2) { Phase = Phase.Setup; Current = null; }
    }

    public void Move(Player p, int delta)
    {
        var i = Players.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Players.Count) return;
        (Players[i], Players[j]) = (Players[j], Players[i]);
    }

    /// <summary>Moves p to seat index (the others shift up or down one), for dragging a seat around the circle.</summary>
    public void MoveTo(Player p, int index)
    {
        if (!Players.Remove(p)) return;
        Players.Insert(Math.Clamp(index, 0, Players.Count), p);
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
            PastGames.Add(new GameRecord(GameNumber, History.ToList(), RollLog.ToList()));
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
        Round = 0;
        Phase = Phase.Setup;
        Current = Winner = Starter = roundStarter = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
        WinningRoll = 0;
        turnSerial++;
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
        RollLog.RemoveAt(index);
        Replay();
        return true;
    }

    /// <summary>
    /// Re-runs the round in play from its start over its logged rolls, in order: every roll is judged again (the host's
    /// accepts included), so the turn, the limit and a win follow an edit. Each roll keeps one log line; a roll that
    /// no longer counts (after the win, or its player left) stays, marked Void, so a second edit can bring it back.
    /// </summary>
    private void Replay()
    {
        var inputs = RollLog.Where(r => r.Round == Round).ToList();
        RollLog.RemoveAll(r => r.Round == Round);
        BeginRound();
        foreach (var r in inputs)
        {
            var p = Players.FirstOrDefault(x => x.Name == r.Player);
            var before = RollLog.Count;
            var outcome = OnRoll(p, r.Value, r.Max);
            if (RollLog.Count == before) RollLog.Add(r with { Note = RollNote.Void, Accepted = false });
            else if (outcome == RollOutcome.WrongMax && r.Accepted) AcceptPending();
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

    public RollOutcome AcceptPending()
    {
        if (Pending is not { } pending || Phase != Phase.Turns || pending.Player != Current) return RollOutcome.Ignored;
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
        if (Active.Count() <= 1) { Phase = Phase.GameOver; return true; }
        // Round 2 on: no opening roll; the next player in the list after the winner starts with a fresh /random.
        // The piece is already off, so a player who just went out is skipped (NextActive ignores Out).
        roundStarter = NextActive(Winner);
        Round++;
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
    private long dueAt;
    private long? pausedLeft; // ms on the clock while paused; a new wait while paused starts full and stays paused

    public int Sent { get; private set; }
    public bool Paused => pausedLeft is not null;

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
            key = stateKey;
            dueAt = now + intervalMs;
            if (pausedLeft is not null) pausedLeft = intervalMs;
            Sent = 0;
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
}

/// <summary>Every line the plugin posts, and the command prefixes it posts them with.</summary>
public static class Lines
{
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
