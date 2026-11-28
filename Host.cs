namespace StripDeathroll14;

/// <summary>What the host's call logic reads from the settings, the party and the venue at the moment a line is made.</summary>
public sealed record HostSettings(
    bool AutoPost,
    IReadOnlyCollection<CallKind> HeldFromAutoPost,
    Messages Messages,
    Venue Venue,
    CallChannel CallChannel,
    bool InAlliance);

/// <summary>
/// The host side of a game: which line each event produces, whether Auto-post sends it or it waits in Proposed for
/// Post, the once-per-turn wrong-roll call, and the window's alert. No Dalamud types, so the tests drive it as is;
/// the plugin hands it the sending and the Echo printing.
/// </summary>
public sealed class GameHost(DeathrollGame game, Func<HostSettings> settings, Action<string, string> send, Action<string> echo)
{
    // The reroll call, once per turn (per player in the opening), keyed turn + player:
    private readonly HashSet<string> wrongRollCalled = []; // made: sent, or put in Proposed
    private readonly HashSet<string> wrongRollSent = [];   // reached chat (Auto-post, Post or Ask for reroll)
    private readonly List<(string Key, Player Player, string Line)> waitingWrongRolls = []; // in Proposed, taken out once answered
    private bool alertIsPending;        // LastAlert is about the wrong roll waiting for Accept; it goes once that is settled

    public DeathrollGame Game { get; } = game;

    /// <summary>Lines waiting for the host's Post press: everything with Auto-post off, the held kinds with it on.</summary>
    public List<string> Proposed { get; } = [];

    public string? LastAlert { get; private set; }

    private HostSettings S => settings();
    public string CallPrefix => Lines.CallPrefix(S.CallChannel, S.Venue, S.InAlliance);
    public string RollCommand(int? n) => Lines.RollCommand(S.Venue, S.InAlliance, n);

    // ---- host actions (button presses) ----

    /// <summary>
    /// Starts the next round or restarts the one in play. A round that opens with rolls proposes the round-start line;
    /// one that starts with turns (round 2 on) proposes the starter's call, so an older call never survives a restart.
    /// </summary>
    public void StartRound()
    {
        if (!Game.CanStartRound) return;
        ClearAlert(); // an alert about the round before (or the one being restarted) is stale now
        Game.StartRound();
        if (Game.Phase == Phase.Opening) Propose((CallKind.RoundStart, Lines.Opening(Game.Round, RollCommand(null), S.Messages)));
        else CallCurrent();
    }

    /// <summary>A clean slate: the game reset, plus the leftovers from the last one (alert, unposted lines, wrong-roll guard).</summary>
    public void NewGame()
    {
        Game.NewGame();
        ClearAlert();
        wrongRollCalled.Clear();
        wrongRollSent.Clear();
        waitingWrongRolls.Clear();
        Proposed.Clear();
    }

    /// <summary>Empties the player list; the round in play stops.</summary>
    public void ClearPlayers()
    {
        Game.ClearPlayers();
        ClearAlert();
        waitingWrongRolls.Clear();
        Proposed.Clear();
    }

    /// <summary>Debug mode off: the dummies leave, through the same path as removing a player by hand.</summary>
    public void RemoveDummies() => AfterChange(() =>
    {
        var any = Game.Players.Any(p => p.IsDummy);
        Game.RemoveDummies();
        return any;
    });

    /// <summary>
    /// Takes a player off the list. If the game moved because of it (they were up, the opening is now complete, the
    /// winner left, or too few are left) the window follows: the new player up is called, or the stale lines go.
    /// </summary>
    public void RemovePlayer(Player p) => AfterChange(() => { Game.Remove(p); return true; });

    /// <summary>The host fixes a roll of the round in play; a call or win line is proposed if the turn moved because of it.</summary>
    public bool EditRoll(int index, int value, int? max) => AfterChange(() => Game.EditRoll(index, value, max));

    public bool DeleteRoll(int index) => AfterChange(() => Game.DeleteRoll(index));

    /// <summary>A roll the plugin missed, entered by the host: judged exactly as if it had just come from chat.</summary>
    public string AddRoll(Player p, int value, int? max)
    {
        if (!Game.CanEditRolls || !Game.Players.Contains(p)) return "Rolls can be added once a round has started.";
        if (!DeathrollGame.IsPossible(value, max)) return "Not a possible roll: 0 up to the number rolled to (at least 2).";
        var count = Game.RollLog.Count;
        Receive(p, value, max);
        return Game.RollLog.Count > count ? $"Added {p.Name}'s {value}." : $"Not added: {p.Name} cannot roll now (out, or the round is already won).";
    }

    private (Phase Phase, Player? Current, int? Limit, Player? Winner, int WinningRoll, (Player, int, int?)? Pending) Moment() =>
        (Game.Phase, Game.Current, Game.Limit, Game.Winner, Game.WinningRoll, Game.Pending is { } p ? (p.Player, p.Value, p.Max) : null);

    private bool AfterChange(Func<bool> change)
    {
        var before = Moment();
        if (!change()) return false;
        DropAnsweredWrongRoll(); // the wrong roll it answered may be gone (deleted, edited right, or its player left)
        var now = Moment();
        if (now == before) return true; // nothing moved: what waits and the alert still hold
        ClearAlert();
        if (now with { Pending = null } == before with { Pending = null })
        {
            // only the waiting wrong roll changed (deleted, edited, or now there): same player, same roll due, no new call
            if (Game.Pending is { } still) PendingAlert(still);
            return true;
        }

        if (Game.Phase == Phase.Pick && Game.Winner is { } w) Propose((CallKind.Win, Lines.Won(w.Name, Game.WinningRoll, Game.Round, m: S.Messages)));
        else if (Game.Phase == Phase.Turns && Game.Pending is null)
        {
            if (before.Phase == Phase.Opening) TieAlert();
            CallCurrent();
        }
        else
        {
            // back in the opening, stopped, or a wrong number waits for the host: an older call is stale, but a reroll
            // call still owed (their wrong roll is the one waiting, or their opening roll is still due) stays
            Proposed.RemoveAll(l => !waitingWrongRolls.Any(x => x.Line == l));
            DropAnsweredWrongRoll();
        }

        if (Game.Pending is { } pending) PendingAlert(pending); // a wrong number (still, or now) waits for the host: say so
        return true;
    }

    public void Pick(Player target, Piece piece)
    {
        var w = Game.Winner;
        var (roll, round) = (Game.WinningRoll, Game.Round);
        if (!Game.Pick(target, piece)) return;
        ClearAlert(); // the next round (or the game over) starts clean

        // One event, one batch: the win (if not posted yet), who lost what, then the game-over line or the next call.
        // A win held back from Auto-post is the host's to announce: once the pick is made it is not caught up here.
        var s = S;
        var m = s.Messages;
        var lines = new List<(CallKind, string)>();
        var catchUpWin = !(s.AutoPost && s.HeldFromAutoPost.Contains(CallKind.Win));
        if (catchUpWin && w is not null && Proposed.Contains(Lines.Won(w.Name, roll, round, m: m))) lines.Add((CallKind.Win, Lines.Won(w.Name, roll, round, ask: false, m: m)));
        lines.Add((CallKind.Strip, Lines.Stripped(target.Name, piece, target.Lives, m)));
        if (Game.Phase == Phase.GameOver) lines.Add((CallKind.GameOver, Lines.GameOver(Game.Champion!, m)));
        else if (Game.Phase == Phase.Turns && Game.Current is { } p) lines.Add((CallKind.Call, Lines.Call(p.Name, RollCommand(Game.Limit), m)));
        Propose([.. lines]);
    }

    public void AcceptPending()
    {
        Handle(Game.AcceptPending());
        ClearSettledAlert();
    }

    /// <summary>
    /// The Ask for reroll button: sends the wrong roller their reroll call (naming the right roll) in the call channel
    /// right away, whatever Auto-post says, because the host pressed it. Drops the same line from Proposed so it is not
    /// posted twice, and counts as this turn's wrong-roll call.
    /// </summary>
    public void AskForReroll()
    {
        if (Game.Pending is not { } pending || Game.Phase != Phase.Turns || pending.Player != Game.Current) return;
        var line = WrongRollLine(pending);
        var key = WrongRollKey(pending.Player);
        Proposed.Remove(line);
        waitingWrongRolls.RemoveAll(w => w.Key == key);
        wrongRollCalled.Add(key);
        wrongRollSent.Add(key);
        send(CallPrefix, line);
    }

    /// <summary>Calls whoever is up, with their roll; only while the turns are on (never to the winner of a won round).</summary>
    public void CallCurrent()
    {
        if (Game.Phase == Phase.Turns && Game.Current is { } p) Propose((CallKind.Call, Lines.Call(p.Name, RollCommand(Game.Limit), S.Messages)));
    }

    public void PostProposed()
    {
        foreach (var w in waitingWrongRolls.Where(w => Proposed.Contains(w.Line))) wrongRollSent.Add(w.Key);
        waitingWrongRolls.Clear();
        foreach (var line in Proposed) send(CallPrefix, line);
        Proposed.Clear();
    }

    // ---- rolls ----

    public void Receive(Player? player, int value, int? max)
    {
        var outcome = Game.OnRoll(player, value, max);
        var shownMax = (max ?? DeathrollGame.OpeningMax).ToString();
        switch (outcome)
        {
            case RollOutcome.OpeningWrongMax:
                CallWrongRollOnce(player!, value, max, null);
                Alert($"{player!.Name} rolled out of {shownMax}: the opening roll is a plain {RollCommand(null)}. Not counted. {CallNote(player!)}");
                break;
            case RollOutcome.AlreadyRolled:
                Alert($"{player!.Name} rolled again during the opening. Ignored.");
                break;
            case RollOutcome.OutOfTurn:
                Alert($"{player!.Name} rolled out of turn ({Game.Current?.Name} is up). Ignored.");
                break;
            case RollOutcome.WrongMax:
                CallWrongRollOnce(player!, value, max, Game.Limit);
                PendingAlert(Game.Pending!.Value);
                break;
            default:
                Handle(outcome);
                ClearSettledAlert();
                break;
        }

        DropAnsweredWrongRoll();
    }

    /// <summary>
    /// Takes a waiting reroll call out of Proposed once its player no longer owes that reroll: they made their opening
    /// roll, the wrong roll was settled, or they left. In the turns a settled roll moves the turn, whose call replaces it
    /// anyway; in the opening nothing else would. A call already gone from Proposed (posted, discarded) is forgotten.
    /// </summary>
    private void DropAnsweredWrongRoll()
    {
        for (var n = waitingWrongRolls.Count - 1; n >= 0; n--)
        {
            var w = waitingWrongRolls[n];
            var owed = Game.Players.Contains(w.Player) && !w.Player.Out && w.Key == WrongRollKey(w.Player) && // still that turn (an edit can move it)
                (Game.Phase == Phase.Opening && w.Player.Opening is null || Game.Phase == Phase.Turns && Game.Pending?.Player == w.Player);
            var at = Proposed.IndexOf(w.Line);
            if (owed && at >= 0)
            {
                // the same call, but an edit may have changed the roll due or the wrong roll itself: keep its words true
                if (Game.Phase == Phase.Turns && WrongRollLine(Game.Pending!.Value) is var fresh && fresh != w.Line)
                {
                    Proposed[at] = fresh;
                    waitingWrongRolls[n] = w with { Line = fresh };
                }

                continue;
            }

            if (!owed && at >= 0) Proposed.RemoveAt(at);
            waitingWrongRolls.RemoveAt(n);
        }
    }

    private string WrongRollKey(Player p) => $"{Game.TurnId}/{p.Name}";

    /// <summary>What became of this turn's reroll call for p, truthfully: it went, it waits, it was discarded, or none was made.</summary>
    private string CallNote(Player p)
    {
        var key = WrongRollKey(p);
        if (wrongRollSent.Contains(key)) return "Their reroll call went to chat.";
        if (waitingWrongRolls.Any(w => w.Key == key && Proposed.Contains(w.Line))) return "Their reroll call waits for Post.";
        if (wrongRollCalled.Contains(key)) return "Their reroll call was discarded, not posted (once per turn); Ask for reroll sends one.";
        return "No reroll call made; Ask for reroll sends one.";
    }

    /// <summary>The alert for the wrong-number roll waiting for the host: what was rolled, what was due, and the call's fate.</summary>
    private void PendingAlert(PendingRoll p) =>
        Alert($"{p.Player.Name} rolled {p.Value} out of {(p.Max ?? DeathrollGame.OpeningMax)}, expected {RollCommand(Game.Limit)}. {CallNote(p.Player)} Accept counts it anyway.", aboutPending: true);

    /// <summary>
    /// Calls the player to reroll, naming the right roll, the first time they roll the wrong number in a turn (in the
    /// opening: in the round). A second wrong roll in the same turn only alerts, so a muddle never spams chat. In the
    /// opening the call joins what waits for Post (another player's call, the round-start line) instead of replacing it.
    /// </summary>
    private void CallWrongRollOnce(Player p, int value, int? max, int? limit)
    {
        var key = WrongRollKey(p);
        if (!wrongRollCalled.Add(key)) return;
        var line = Lines.WrongRoll(p.Name, value, max ?? DeathrollGame.OpeningMax, RollCommand(limit), S.Messages);
        if (Game.Phase == Phase.Opening) Propose(keep: true, (CallKind.WrongRoll, line));
        else Propose((CallKind.WrongRoll, line));
        if (Proposed.Contains(line)) waitingWrongRolls.Add((key, p, line));
        else wrongRollSent.Add(key);
    }

    private string WrongRollLine(PendingRoll p) =>
        Lines.WrongRoll(p.Player.Name, p.Value, p.Max ?? DeathrollGame.OpeningMax, RollCommand(Game.Limit), S.Messages);

    private void Handle(RollOutcome outcome)
    {
        switch (outcome)
        {
            case RollOutcome.OrderSet:
                TieAlert();
                CallCurrent();
                break;
            case RollOutcome.Next:
                CallCurrent();
                break;
            case RollOutcome.Won:
                Propose((CallKind.Win, Lines.Won(Game.Winner!.Name, Game.WinningRoll, Game.Round, m: S.Messages)));
                break;
        }
    }

    private void TieAlert()
    {
        if (Game.OpeningTie) Alert($"Tie for the highest opening roll: {Game.Starter?.Name} starts as the first of them in the list. Restart the round for a roll-off.");
    }

    // Each event replaces what waits for Post (an older line is stale), but the lines of one event stay together.
    // Auto-post sends the lines of the kinds it is set to send and leaves the held kinds waiting, each kind on its own.
    private void Propose(params (CallKind Kind, string Line)[] lines) => Propose(keep: false, lines);

    private void Propose(bool keep, params (CallKind Kind, string Line)[] lines)
    {
        var s = S;
        var (sending, holding) = Lines.SplitAuto(s.AutoPost, s.HeldFromAutoPost, lines);
        if (!keep) Proposed.Clear();
        Proposed.AddRange(holding.Where(l => !Proposed.Contains(l)));
        foreach (var line in sending) send(CallPrefix, line);
    }

    // ---- alert ----

    public void ClearAlert()
    {
        LastAlert = null;
        alertIsPending = false;
    }

    /// <summary>The wrong-roll alert names Accept, which is gone once the roll is accepted or rerolled right.</summary>
    private void ClearSettledAlert()
    {
        if (alertIsPending && Game.Pending is null) ClearAlert();
    }

    public void Alert(string text, bool aboutPending = false)
    {
        LastAlert = text;
        alertIsPending = aboutPending;
        echo(text);
    }
}
