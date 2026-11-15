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

public sealed record RollRecord(int Round, string Player, int Value, int? Max, RollNote Note);

public readonly record struct PendingRoll(Player Player, int Value, int? Max, int LogIndex);

/// <summary>Pure Strip Deathroll state: no Dalamud types, so the test project compiles it as is.</summary>
public sealed class DeathrollGame
{
    public static readonly Piece[] AllPieces = [Piece.Top, Piece.Bottom, Piece.Shoes];
    public static readonly int StartLives = AllPieces.Length;
    public const int OpeningMax = 999; // what a bare /random rolls up to

    // Debug-mode stand-ins; valid "Forename Surname" names so every code path treats them like players.
    public static readonly string[] DummyNames =
        ["Dummy Alpha", "Dummy Bravo", "Dummy Charlie", "Dummy Delta", "Dummy Echo", "Dummy Foxtrot", "Dummy Golf", "Dummy Hotel"];

    private int turnSerial;

    public List<Player> Players { get; } = [];
    public List<RoundRecord> History { get; } = [];
    public List<RollRecord> RollLog { get; } = [];
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

    public static bool IsEnding(int roll) => roll is 0 or 1 or 69 or 666; // /random rolls 0 to N

    /// <summary>The player's most recent roll this game, any round, counted or not; null before their first.</summary>
    public RollRecord? LastRoll(Player p) => RollLog.LastOrDefault(r => r.Player == p.Name);

    /// <summary>Rolls that changed nothing: shown greyed.</summary>
    public static bool NotCounted(RollNote n) => n is RollNote.OpeningRepeat or RollNote.OpeningWrongNumber or RollNote.OutOfTurn or RollNote.WrongNumber;

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

    public void Remove(Player p)
    {
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

    public bool CanStartRound => Phase is Phase.Setup or Phase.Opening or Phase.Turns && Active.Count() >= 2;

    public void StartRound()
    {
        if (!CanStartRound) return;
        foreach (var p in Players) p.Opening = null;
        Round++;
        Phase = Phase.Opening;
        Current = Winner = Starter = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
    }

    public void NewGame()
    {
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
        Current = Winner = Starter = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
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
        var from = Winner;
        Phase = Phase.Setup;
        StartRound();
        // Round 2 on: no opening roll; the next player in the list after the winner starts with a fresh /random.
        // The piece is already off, so a player who just went out is skipped (NextActive ignores Out).
        Starter = Current = NextActive(from);
        turnSerial++;
        Phase = Phase.Turns;
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

    private Player? NextActive(Player from)
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

    public int Sent { get; private set; }

    /// <summary>Whole seconds (rounded up) until the next reminder; null when none is coming.</summary>
    public int? SecondsLeft(long now) => key is null || Sent >= MaxPerTurn ? null : (int)Math.Max(0, (dueAt - now + 999) / 1000);

    public bool Due(string? stateKey, long now, int intervalMs)
    {
        if (stateKey != key)
        {
            key = stateKey;
            dueAt = now + intervalMs;
            Sent = 0;
            return false;
        }

        if (key is null || Sent >= MaxPerTurn || now < dueAt) return false;
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

/// <summary>Every line the plugin posts, and the command prefixes it posts them with.</summary>
public static class Lines
{
    /// <summary>The roll command players are asked to use; n null = the bare opening roll.</summary>
    public static string RollCommand(Venue venue, bool alliance, int? n)
    {
        var cmd = venue == Venue.Private ? "/random" : alliance ? "/dice alliance" : "/dice party";
        return n is null ? cmd : $"{cmd} {n}";
    }

    public static string AnnouncePrefix(Venue venue, bool alliance) => venue == Venue.Private ? "/s" : alliance ? "/a" : "/p";

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

    public static string Opening(int round, string cmd) => $"Strip Deathroll round {round}! Everyone roll {cmd} - highest goes first.";

    public static string Call(string name, string cmd) => $"{name}, roll {cmd}";

    public static string Won(string winner, int roll, int round) => $"{winner} rolled {roll} and wins round {round}! {winner}, who strips?";

    public static string PieceName(Piece p) => p.ToString().ToLowerInvariant();

    public static string Stripped(string target, Piece piece, int livesLeft) => livesLeft > 0
        ? $"{target} loses their {PieceName(piece)}! {livesLeft} {(livesLeft == 1 ? "piece" : "pieces")} left."
        : $"{target} loses their {PieceName(piece)}, the last piece, and is out!";

    /// <summary>The timed nudge; a long opening list is cut so the line stays short.</summary>
    public static string Reminder(IReadOnlyList<string> names, string cmd, bool opening)
    {
        if (!opening) return $"Reminder: {names[0]}, it's your turn - roll {cmd}";
        var shown = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" and {names.Count - 6} more" : "");
        return $"Still waiting on {shown}: roll {cmd} to start the round.";
    }

    public static string GameOver(string name) => $"{name} is the last one standing and wins Strip Deathroll!";

    /// <summary>A line is safe to send only as one short line that is not a command of its own.</summary>
    public static bool IsSendable(string line) =>
        line.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(line) <= 400 && !line.StartsWith('/') && !line.Any(char.IsControl);

    /// <summary>Rules/ad text: one chat line per non-empty text line, capped so a paste cannot flood chat.</summary>
    public const int MaxTextLines = 10;

    public static List<string> TextLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(MaxTextLines).ToList();
}
