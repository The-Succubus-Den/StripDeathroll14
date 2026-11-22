using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using KosLib;

namespace StripDeathroll14;

public sealed partial class MainWindow : Window
{
    private readonly Plugin plugin;
    private string newName = "";
    private string addNote = "";
    private Player? pickTarget;
    private Piece? pickPiece;
    private KosTheme.Scope? windowTheme;
    private bool showHistory;       // the Game tab's Edit rolls button selects the History tab next frame
    private int editIndex = -1;     // the roll log line being edited, -1 none
    private int editValue, editMax;
    private string? addRollFor;     // the player picked in the Add a missed roll row
    private int addValue, addMax;
    private string rollNote = "";
    private string historyNote = "";
    private readonly FileDialogManager fileDialog = new();

    public MainWindow(Plugin plugin) : base("Strip Deathroll###StripDeathroll14")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 360), MaximumSize = new Vector2(1200, 1400) };
    }

    /// <summary>
    /// The one look under test: MaterialKos in KosMos colours, frosted if the setting is on. It must be pushed before
    /// ImGui begins the window. Frosted, KosTheme.Frost() in Draw() blurs behind the window, so Dalamud's own pass is off.
    /// </summary>
    public override void PreDraw()
    {
        windowTheme?.Dispose();
        var frosted = plugin.Config.FrostedWindow;
        AllowBackgroundBlur = !frosted;
        windowTheme = KosTheme.PushWindow(KosAccent.KosMos, material: true, frosted: frosted);
    }

    /// <summary>Dalamud calls this whenever it called PreDraw, so the whole-window push is always popped.</summary>
    public override void PostDraw()
    {
        windowTheme?.Dispose();
        windowTheme = null;
    }

    public override void Draw()
    {
        KosTheme.Frost(); // nothing unless PreDraw pushed the frosted look
        fileDialog.Draw();
        using var bar = ImRaii.TabBar("##tabs");
        if (!bar) return;
        using (var t = ImRaii.TabItem("Game")) if (t) DrawGame();
        using (var t = ImRaii.TabItem("History", showHistory ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) if (t) DrawHistory();
        showHistory = false;
        using (var t = ImRaii.TabItem("Messages")) if (t) DrawTexts();
        using (var t = ImRaii.TabItem("Settings")) if (t) DrawSettings();
    }

    private void DrawGame()
    {
        var config = plugin.Config;
        var game = plugin.Game;

        if (plugin.DebugMode)
        {
            Kos.Badge("Debug", ImGuiColors.DalamudOrange);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Posts go to Echo only, never to chat.");
        }

        ImGui.SetNextItemWidth(200);
        if (Combo("Venue", config.Venue, Enum.GetValues<Venue>(), VenueLabel, v => config.Venue = v)) plugin.SaveConfig();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (Combo("Calls in", config.CallChannel, Enum.GetValues<CallChannel>(), CallLabel, c => config.CallChannel = c)) plugin.SaveConfig();

        var auto = config.AutoPost;
        if (MaterialKos.Switch("Auto-post", ref auto)) { config.AutoPost = auto; plugin.SaveConfig(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Post calls and results as they happen ({config.SendMinSeconds}-{config.SendMaxSeconds} s apart). Off: they wait below for Post.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        int lo = config.SendMinSeconds, hi = config.SendMaxSeconds;
        if (ImGui.DragIntRange2("between lines", ref lo, ref hi, 0.05f, Outbox.MinGapMs / 1000, Outbox.MaxGapMs / 1000, "%d s"))
        {
            config.SendMinSeconds = lo;
            config.SendMaxSeconds = hi;
            config.Normalize(); // a typed value (Ctrl+click) can land outside 2-5 or below the minimum
            plugin.SaveConfig();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wait between any two lines the plugin sends (rolls, calls, reminders, rules and ad): a random time between these two limits, each 2-5 s. Drag either number.");
        ImGui.SameLine();
        ImGui.TextDisabled($"Calls go to {plugin.CallPrefix}; players roll {plugin.RollCommand(null)} [N].");

        ImGui.Spacing();
        var phaseColor = PhaseColor(game.Phase);
        Kos.Card("status", phaseColor, () =>
        {
            Kos.Badge(PhaseLabel(game), phaseColor);
            ImGui.SameLine();
            ImGui.TextUnformatted(Status(game));
            if (plugin.ReminderSecondsLeft is { } left)
            {
                var paused = plugin.ReminderPaused;
                ImGui.SameLine();
                ImGui.TextDisabled(paused ? $"Reminder paused at {left} s." : $"Reminder in {left} s ({ReminderClock.MaxPerTurn - plugin.RemindersSent} left).");
                ImGui.SameLine();
                if (MaterialKos.Button(paused ? "Resume##rem" : "Pause##rem", MaterialButton.Text, paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause))
                    plugin.ReminderPaused = !paused;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Holds the reminder timer, through turn changes too, until you resume it. This session only.");
            }
            if (game.Phase == Phase.Turns && game.Starter is { } top)
                ImGui.TextColored(ImGuiColors.DalamudYellow, top.Opening is { } o ? $"Highest opening: {top.Name} ({o})." : $"Started by {top.Name} (next after last round's winner).");
            if (plugin.LastAlert is { } alert) Kos.Callout(alert, ImGuiColors.DalamudRed);

            if (game.Pending is { } pending && pending.Player == game.Current)
            {
                if (MaterialKos.Button($"Accept {pending.Value}", MaterialButton.Filled, FontAwesomeIcon.Check)) plugin.AcceptPending();
                ImGui.SameLine();
                if (MaterialKos.Button("Ask for reroll", MaterialButton.Tonal, FontAwesomeIcon.RedoAlt)) plugin.CallCurrent();
            }

            if (game.Phase == Phase.Pick) DrawPick(game);
        });

        using (ImRaii.Disabled(!game.CanStartRound))
        {
            if (game.Phase == Phase.Setup ? MaterialKos.Button("Start round", MaterialButton.Filled, FontAwesomeIcon.Play) : MaterialKos.Button("Restart round", MaterialButton.Outlined, FontAwesomeIcon.Redo))
                plugin.StartRound();
        }
        ImGui.SameLine();
        var myRoll = plugin.MyRoll;
        using (ImRaii.Disabled(myRoll is null))
        {
            if (MaterialKos.Button(myRoll is null ? "Roll for me" : $"Roll for me: {myRoll}", MaterialButton.Filled, FontAwesomeIcon.Dice)) plugin.RollForMe();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Your roll: a fresh /random to open or to start the turns, else up to the previous roll. /sdr roll does the same from a macro.");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(game.Current is null)) { if (MaterialKos.Button("Call again", MaterialButton.Tonal, FontAwesomeIcon.Bullhorn)) plugin.CallCurrent(); }
        ImGui.SameLine();
        using (ImRaii.Disabled(!game.CanEditRolls))
        {
            if (MaterialKos.Button("Edit rolls", MaterialButton.Text, FontAwesomeIcon.Edit)) showHistory = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Fix a misread roll, delete one, or add a roll the plugin missed (History tab). Once a round has started.");
        }
        ImGui.SameLine();
        if (MaterialKos.Button("New game", MaterialButton.Text, FontAwesomeIcon.Broom) && ImGui.GetIO().KeyCtrl) NewGame();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click: a clean slate. Everyone back to 3 pieces, round 1 again, alerts and unposted lines cleared. This game moves to the History tab as Game N. Players stay.");

        DrawProposed();
        Kos.Section("Players");
        DrawAddPlayers(game);
        ImGui.TextDisabled("Clockwise from the top. Drag a seat to move it; right-click for more.");
        DrawCircle(game);
    }

    private void DrawPick(DeathrollGame game)
    {
        var choices = game.Active.ToList(); // the winner may pick themselves
        if (pickTarget is not null && !choices.Contains(pickTarget)) pickTarget = null;
        if (pickTarget is null || pickPiece is not { } pc || !pickTarget.Worn.Contains(pc)) pickPiece = pickTarget?.Worn.FirstOrDefault();

        ImGui.SetNextItemWidth(220);
        using (var combo = ImRaii.Combo("Who strips?", pickTarget?.Name ?? "Choose..."))
        {
            if (combo)
            {
                foreach (var p in choices)
                {
                    var label = $"{p.Name}{(p == game.Winner ? " (winner)" : "")} - {Worn(p)}";
                    if (ImGui.Selectable(label, p == pickTarget)) pickTarget = p;
                }
            }
        }

        if (pickTarget is null) return;
        foreach (var piece in pickTarget.Worn) // one chip per piece still worn; picking one picks only that one
        {
            ImGui.SameLine();
            var on = piece == pickPiece;
            if (MaterialKos.Chip($"{piece}##piece", ref on)) pickPiece = piece;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(pickPiece is null))
        {
            if (MaterialKos.Button("Confirm", MaterialButton.Filled, FontAwesomeIcon.Check) && pickPiece is { } chosen)
            {
                plugin.Pick(pickTarget, chosen);
                pickTarget = null;
                pickPiece = null;
            }
        }
    }

    private void DrawProposed()
    {
        if (plugin.Proposed.Count == 0)
        {
            if (plugin.Queued > 0) ImGui.TextDisabled($"Sending {plugin.Queued} line(s)...");
            return;
        }

        Kos.Card("proposed", ImGuiColors.TankBlue, () =>
        {
            ImGui.TextUnformatted($"To post in {plugin.CallPrefix}:");
            foreach (var line in plugin.Proposed) ImGui.BulletText(line);
            if (MaterialKos.Button("Post", MaterialButton.Filled, FontAwesomeIcon.PaperPlane)) plugin.PostProposed();
            ImGui.SameLine();
            if (MaterialKos.Button("Discard", MaterialButton.Text, FontAwesomeIcon.Times)) plugin.Proposed.Clear();
        });
    }

    private void DrawAddPlayers(DeathrollGame game)
    {
        ImGui.SetNextItemWidth(180);
        var enter = ImGui.InputTextWithHint("##name", "Forename Surname", ref newName, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (MaterialKos.Button("Add", MaterialButton.Tonal, FontAwesomeIcon.Plus) || enter)
        {
            addNote = game.Add(newName) ? "" : "Needs Forename Surname (letters, ' or -), not already listed.";
            if (addNote.Length == 0) newName = "";
        }

        ImGui.SameLine();
        if (MaterialKos.Button("Add target", MaterialButton.Tonal, FontAwesomeIcon.Crosshairs)) addNote = plugin.AddTarget();
        ImGui.SameLine();
        if (MaterialKos.Button("Add party/alliance", MaterialButton.Tonal, FontAwesomeIcon.Users)) addNote = $"Added {plugin.AddGroup()} player(s).";
        if (plugin.DebugMode)
        {
            ImGui.SameLine();
            if (MaterialKos.Button("Add dummy", MaterialButton.Outlined, FontAwesomeIcon.Robot)) addNote = game.AddDummy() ? "" : "All dummy names are in use.";
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(game.Players.Count == 0))
        {
            if (MaterialKos.Button("Clear players", MaterialButton.Text, FontAwesomeIcon.UserSlash) && ImGui.GetIO().KeyCtrl)
            {
                plugin.ClearPlayers();
                pickTarget = null;
                addRollFor = null;
                editIndex = -1;
                addNote = "";
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Ctrl+click: remove every name from the list. A round in play stops; history stays until New game.");
        }

        if (addNote.Length > 0) ImGui.TextDisabled(addNote);
    }

    private void NewGame()
    {
        plugin.NewGame();
        pickTarget = null;
        pickPiece = null;
        editIndex = -1;
        addRollFor = null;
        addNote = "";
        rollNote = "";
        historyNote = "";
    }

    private void DrawHistory()
    {
        var game = plugin.Game;
        if (editIndex >= 0 && !game.IsEditable(editIndex)) editIndex = -1; // the round moved on
        DrawHistoryBar(game);
        if (game.CanEditRolls) DrawAddRoll(game);
        var current = game.AllGames.LastOrDefault(g => g.Number == game.GameNumber);
        if (current is null && game.PastGames.Count == 0) { Kos.EmptyState("No rolls yet."); return; }

        if (current is not null && ImGui.CollapsingHeader($"Game {current.Number} (this game)###game{current.Number}", ImGuiTreeNodeFlags.DefaultOpen))
            using (ImRaii.PushIndent())
                DrawRounds(game, current, true);

        foreach (var past in Enumerable.Reverse(game.PastGames)) // newest first, closed
        {
            var wins = past.History.GroupBy(h => h.Winner).OrderByDescending(w => w.Count()).FirstOrDefault();
            var title = $"Game {past.Number}: {past.History.Count} round{(past.History.Count == 1 ? "" : "s")} won"
                        + (wins is null ? "" : $", most wins {wins.Key} ({wins.Count()})");
            if (!ImGui.CollapsingHeader($"{title}###game{past.Number}")) continue;
            using (ImRaii.PushIndent())
                DrawRounds(game, past, false);
        }
    }

    /// <summary>Export and clear, above the games.</summary>
    private void DrawHistoryBar(DeathrollGame game)
    {
        var any = game.AllGames.Any();
        using (ImRaii.Disabled(!any))
        {
            if (MaterialKos.Button("Export CSV", MaterialButton.Tonal, FontAwesomeIcon.FileExport))
                fileDialog.SaveFileDialog("Export history as CSV", ".csv", $"StripDeathroll history {DateTime.Now:yyyy-MM-dd HHmm}", ".csv", (ok, path) =>
                {
                    if (!ok) return;
                    try
                    {
                        File.WriteAllText(path, HistoryCsv.Build(plugin.Game.AllGames), new System.Text.UTF8Encoding(true)); // BOM: Excel reads it as UTF-8
                        historyNote = $"Saved {Path.GetFileName(path)}.";
                    }
                    catch (Exception e)
                    {
                        historyNote = $"Could not save: {e.Message}";
                    }
                });
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Every game in the history, this one included: one line per roll, plus each round's result.");
            ImGui.SameLine();
            if (MaterialKos.Button("Clear history", MaterialButton.Text, FontAwesomeIcon.Trash) && ImGui.GetIO().KeyCtrl)
            {
                var keptCurrent = !game.CanClearCurrentHistory && (game.History.Count > 0 || game.RollLog.Count > 0);
                game.ClearHistory();
                editIndex = -1;
                historyNote = keptCurrent ? "Past games cleared. This game's rounds stay while a round is in play." : "History cleared.";
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Ctrl+click: delete every past game, and this game's rounds too unless a round is in play. Players and pieces stay.");
        }

        if (historyNote.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(historyNote);
        }
        ImGui.Spacing();
    }

    /// <summary>One game's rounds, newest first. Only the game in play's current round can be edited.</summary>
    private void DrawRounds(DeathrollGame game, GameRecord g, bool current)
    {
        using var gameId = ImRaii.PushId(g.Number);
        var rounds = g.RollLog.Select(r => r.Round).Concat(g.History.Select(h => h.Round)).Distinct().OrderDescending().ToList();
        foreach (var round in rounds)
        {
            var result = g.History.FirstOrDefault(h => h.Round == round);
            var title = result is null
                ? $"Round {round}" + (current && round == game.Round && game.Phase is not Phase.Setup and not Phase.GameOver ? " (in play)" : " (no result)")
                : $"Round {round}: {result.Winner} won with {result.WinningRoll}; {result.Target} lost their {Lines.PieceName(result.Piece)} ({(result.TargetLivesLeft > 0 ? $"{result.TargetLivesLeft} left" : "out")})";
            if (!ImGui.CollapsingHeader($"{title}###round{round}", current && round == game.Round ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) continue;

            var editable = current && round == game.Round && game.CanEditRolls;
            using var table = ImRaii.Table($"##rolls{round}", editable ? 5 : 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
            if (!table) continue;
            var cell = 70 * ImGuiHelpers.GlobalScale;
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Roll", ImGuiTableColumnFlags.WidthFixed, editable ? cell : 40);
            ImGui.TableSetupColumn("Out of", ImGuiTableColumnFlags.WidthFixed, editable ? cell : 50);
            ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
            if (editable) ImGui.TableSetupColumn("##edit", ImGuiTableColumnFlags.WidthFixed, 5.5f * ImGui.GetFrameHeight()); // two icon-only MaterialKos buttons, ~2.5 frame heights each
            ImGui.TableHeadersRow();
            Action? change = null; // applied after the table so the log is not changed mid-loop
            for (var i = 0; i < g.RollLog.Count; i++) // the current game's snapshot keeps RollLog's indices
            {
                var r = g.RollLog[i];
                if (r.Round != round) continue;
                using var id = ImRaii.PushId(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Player);
                if (editable && i == editIndex)
                {
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##v", ref editValue, 0);
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##m", ref editMax, 0);
                    ImGui.TableNextColumn();
                    var ok = DeathrollGame.IsPossible(editValue, MaxOf(editMax));
                    if (ok) ImGui.TextDisabled("out of 999 = a plain roll");
                    else ImGui.TextColored(ImGuiColors.DalamudRed, "0 up to the out-of (at least 2)");
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!ok))
                        if (MaterialKos.Button("##save", MaterialButton.Filled, FontAwesomeIcon.Check))
                        {
                            var (at, v, m) = (i, editValue, MaxOf(editMax));
                            change = () => rollNote = plugin.EditRoll(at, v, m) ? "Roll changed; the round was worked out again from its start." : "That roll can no longer be changed.";
                        }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Save");
                    ImGui.SameLine();
                    if (MaterialKos.Button("##cancel", MaterialButton.Text, FontAwesomeIcon.Times)) editIndex = -1;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cancel");
                    continue;
                }

                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Value.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted((r.Max ?? DeathrollGame.OpeningMax).ToString());
                ImGui.TableNextColumn();
                var note = Lines.NoteLabel(r.Note);
                if (r.Note is RollNote.Counted or RollNote.Opening) ImGui.TextDisabled(note);
                else if (r.Note == RollNote.Won) ImGui.TextColored(ImGuiColors.HealerGreen, note);
                else ImGui.TextColored(ImGuiColors.DalamudOrange, note);
                if (!editable) continue;
                ImGui.TableNextColumn();
                if (MaterialKos.Button("##edit", MaterialButton.Text, FontAwesomeIcon.Pen))
                {
                    editIndex = i;
                    editValue = r.Value;
                    editMax = r.Max ?? DeathrollGame.OpeningMax;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this roll");
                ImGui.SameLine();
                if (MaterialKos.Button("##del", MaterialButton.Text, FontAwesomeIcon.Trash) && ImGui.GetIO().KeyCtrl)
                {
                    var at = i;
                    change = () => rollNote = plugin.DeleteRoll(at) ? "Roll deleted; the round was worked out again from its start." : "That roll can no longer be deleted.";
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click: delete this roll");
            }

            if (change is not null)
            {
                editIndex = -1;
                change();
            }
        }
    }

    /// <summary>The Out of box: 999 means a plain /random; anything else is checked by IsPossible.</summary>
    private static int? MaxOf(int outOf) => outOf == DeathrollGame.OpeningMax ? null : outOf;

    /// <summary>Above the rounds: enter a roll the plugin missed. It is judged as if it had just come from chat.</summary>
    private void DrawAddRoll(DeathrollGame game)
    {
        Kos.Section($"Round {game.Round}: add a missed roll");
        var players = game.Active.ToList();
        if (addRollFor is not null && players.All(p => p.Name != addRollFor)) addRollFor = null;
        addRollFor ??= (game.Phase == Phase.Opening ? game.Waiting.FirstOrDefault() : game.Current)?.Name ?? players.FirstOrDefault()?.Name;

        ImGui.SetNextItemWidth(180);
        using (var combo = ImRaii.Combo("##addfor", addRollFor ?? "Player..."))
        {
            if (combo)
                foreach (var p in players)
                    if (ImGui.Selectable(p.Name, p.Name == addRollFor))
                    {
                        addRollFor = p.Name;
                        addMax = 0; // refilled with what this player should roll to
                    }
        }

        var expected = game.Phase == Phase.Opening ? DeathrollGame.OpeningMax : game.Limit ?? DeathrollGame.OpeningMax;
        if (addMax == 0) addMax = expected;
        ImGui.SameLine();
        ImGui.TextUnformatted("rolled");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("##addv", ref addValue, 0);
        ImGui.SameLine();
        ImGui.TextUnformatted("out of");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("##addm", ref addMax, 0);
        ImGui.SameLine();
        var who = players.FirstOrDefault(p => p.Name == addRollFor);
        var ok = who is not null && DeathrollGame.IsPossible(addValue, MaxOf(addMax));
        using (ImRaii.Disabled(!ok))
        {
            if (MaterialKos.Button("Add roll", MaterialButton.Tonal, FontAwesomeIcon.Plus) && who is not null)
            {
                rollNote = plugin.AddRoll(who, addValue, MaxOf(addMax));
                addMax = 0;
                addRollFor = null; // next time it follows whoever is up
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Counts as if it had just come from chat, at the end of this round's rolls. Out of 999 = a plain /random.");
        if (rollNote.Length > 0) ImGui.TextDisabled(rollNote);
        ImGui.Spacing();
    }

    private void DrawTexts()
    {
        var config = plugin.Config;
        ImGui.SetNextItemWidth(160);
        if (Combo("Post to", config.TextChannel, Enum.GetValues<TextChannel>(), c => c.ToString(), c => config.TextChannel = c)) plugin.SaveConfig();
        ImGui.TextDisabled($"One chat line per text line, up to {Lines.MaxTextLines}, {config.SendMinSeconds}-{config.SendMaxSeconds} s apart.");
        var defaults = new Configuration();
        DrawText("Rules", config.RulesText, v => config.RulesText = v, defaults.RulesText);
        DrawText("Ad", config.AdText, v => config.AdText = v, defaults.AdText);

        Kos.Section("Game lines");
        ImGui.TextDisabled("What the plugin posts during a game. Words in {braces} are filled in; a blank line uses the default.");
        var m = config.Messages;
        const string ann = "Ann Lee", bo = "Bo Ray";
        DrawMessage("Round start", "{round} {roll}", m.Opening, v => m.Opening = v, () => Lines.Opening(3, "/random", m));
        DrawMessage("Call", "{name} {roll}", m.Call, v => m.Call = v, () => Lines.Call(ann, "/random 412", m));
        DrawMessage("Win", "{name} {number} {round}", m.Won, v => m.Won = v, () => Lines.Won(ann, 69, 3, m: m));
        DrawMessage("Win, posted with the pick", "{name} {number} {round}", m.WonAndPicked, v => m.WonAndPicked = v, () => Lines.Won(ann, 69, 3, ask: false, m: m));
        DrawMessage("Strip", "{name} {piece} {left}", m.Stripped, v => m.Stripped = v, () => Lines.Stripped(bo, Piece.Top, 2, m));
        DrawMessage("Strip, out", "{name} {piece}", m.StrippedOut, v => m.StrippedOut = v, () => Lines.Stripped(bo, Piece.Shoes, 0, m));
        DrawMessage("Game over", "{name}", m.GameOver, v => m.GameOver = v, () => Lines.GameOver(ann, m));
        DrawMessage("Reminder, turn", "{name} {roll}", m.ReminderTurn, v => m.ReminderTurn = v, () => Lines.Reminder([ann], "/random 412", false, m));
        DrawMessage("Reminder, opening", "{names} {roll}", m.ReminderOpening, v => m.ReminderOpening = v, () => Lines.Reminder([ann, bo], "/random", true, m));
    }

    /// <summary>One editable game line with its placeholders, a filled-in example (red when it could not be sent) and Default.</summary>
    private void DrawMessage(string label, string keys, string value, Action<string> set, Func<string> example)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.TextDisabled(keys);
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Default").X - 4 * ImGui.GetFrameHeight());
        if (ImGui.InputText($"##{label}", ref value, 400)) set(value);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        ImGui.SameLine();
        using (ImRaii.Disabled(value.Length == 0)) // blank already is the default
            if (MaterialKos.Button($"Default##{label}", MaterialButton.Text, FontAwesomeIcon.Undo)) { set(""); plugin.SaveConfig(); }
        var line = example();
        if (Lines.IsSendable(line)) ImGui.TextDisabled($"e.g. {line}");
        else ImGui.TextColored(ImGuiColors.DalamudRed, $"Would not be sent (starts with /, or too long): {line}");
    }

    private void DrawText(string label, string value, Action<string> set, string defaultValue)
    {
        Kos.Section(label);
        if (ImGui.InputTextMultiline($"##{label}", ref value, 4000, new Vector2(-1, 110))) set(value);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        if (MaterialKos.Button($"Post {label.ToLowerInvariant()}", MaterialButton.Tonal, FontAwesomeIcon.Bullhorn)) plugin.PostText(value);
        ImGui.SameLine();
        if (MaterialKos.Button($"Default##{label}", MaterialButton.Text, FontAwesomeIcon.Undo)) { set(defaultValue); plugin.SaveConfig(); } // a saved text never picks up a new default by itself
    }

    private void DrawSettings()
    {
        var config = plugin.Config;
        Kos.Section("Reminder", "01");
        var on = config.ReminderEnabled;
        if (MaterialKos.Switch("Remind whoever has not rolled", ref on)) { config.ReminderEnabled = on; plugin.SaveConfig(); }
        var secs = config.ReminderSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Seconds of quiet", ref secs, Configuration.MinReminderSeconds, 120))
            config.ReminderSeconds = Math.Clamp(secs, Configuration.MinReminderSeconds, 120);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        var onlyMe = config.ReminderOnlyMe;
        if (MaterialKos.Switch("Only remind me (Echo, sends nothing)", ref onlyMe)) { config.ReminderOnlyMe = onlyMe; plugin.SaveConfig(); }
        using (ImRaii.Disabled(config.ReminderOnlyMe))
        {
            ImGui.SetNextItemWidth(120);
            if (Combo("Remind in", config.ReminderChannel, [TextChannel.Say, TextChannel.Party], c => c.ToString(), c => config.ReminderChannel = c)) plugin.SaveConfig();
        }
        ImGui.TextDisabled($"Posts the reminder line (Messages tab) after that long with no roll, at most {ReminderClock.MaxPerTurn} times per turn,\n" +
            "whether or not the call was posted. Not while a wrong roll waits for you. Pause it from the Game tab.");

        Kos.Section("World markers", "02");
        var world = config.WorldMarkers;
        if (MaterialKos.Switch("Mark the players in the world", ref world)) { config.WorldMarkers = world; plugin.SaveConfig(); }
        using (ImRaii.Disabled(!config.WorldMarkers))
        {
            var mode = config.WorldMarkersGpu ? 1 : 0;
            if (Kos.Segmented("##drawwith", ref mode, "ImGui", "GPU")) { config.WorldMarkersGpu = mode == 1; plugin.SaveConfig(); }
            ImGui.SameLine();
            Kos.HelpMarker("ImGui: drawn over everything, so the rings show through walls and other players.\n" +
                "GPU: KosDraw's Direct3D path. Rings fade behind scenery and stay off the game's own UI.\n" +
                "If a GPU part fails it switches itself off and ImGui draws that part instead.");
            var motion = config.WorldMarkersMotion;
            if (MaterialKos.Switch("Animate the markers", ref motion)) { config.WorldMarkersMotion = motion; plugin.SaveConfig(); }
            ImGui.SameLine();
            Kos.HelpMarker("A light circles the ring of whoever is up, each roll sends a ripple out from the roller\n" +
                "(gold: a winning roll, grey: one that did not count) and a light runs along an arch to the next player.\n" +
                "Off: the same marks, standing still.");
            if (plugin.ReduceMotion) ImGui.TextDisabled("Dalamud's Reduce motion is on, so the markers stand still either way.");
            var move = plugin.MovingRings;
            if (MaterialKos.Switch("Move the rings", ref move)) plugin.MovingRings = move;
            ImGui.SameLine();
            using (ImRaii.Disabled(!plugin.AnyRingPlaced)) { if (MaterialKos.Button("Rings follow the players", MaterialButton.Text, FontAwesomeIcon.Undo)) plugin.ResetRings(); }
            ImGui.TextDisabled("While on (and this window is open), drag the handle in a ring to put it where you want; right-click a\n" +
                "handle to let that ring follow its player again. Placed rings stay put until you change zone.");
            ImGui.TextDisabled("Pillars of light: crimson, tall, with the number to roll: whoever is up. White: next. Gold, tall: won the round.\n" +
                "Gold, short: started the turns. Gold ring: owes an opening roll. Thin ring: everyone else. Out: nothing.");
            ImGui.TextDisabled(plugin.WorldStatus);
        }

        Kos.Section("Window", "03");
        var frost = config.FrostedWindow;
        if (MaterialKos.Switch("Frosted glass", ref frost)) { config.FrostedWindow = frost; plugin.SaveConfig(); }
        ImGui.SameLine();
        Kos.HelpMarker("Blurs whatever is behind this window, the game and other windows, tinted night blue.\n" +
            "The window turns slightly see-through for it (at most 85% opaque). The blur is Dalamud's own.\n" +
            "Not while the window is dragged out of the game window.");

        Kos.Section("Debug", "04");
        var debug = plugin.DebugMode;
        if (MaterialKos.Switch("Debug mode (this session only)", ref debug)) plugin.SetDebugMode(debug);
        ImGui.TextDisabled("Adds an Add dummy button; right-click a dummy's seat to make it roll, so you can play a whole game alone.\n" +
            "While it is on, calls, results, reminders and the rules/ad print to Echo instead of chat; Roll for me still rolls.\n" +
            "Turning it off removes the dummies.");
        if (plugin.DebugMode && ImGui.CollapsingHeader("KosLib gallery")) KosGallery.Draw();
    }

    private static bool Combo<T>(string label, T current, IEnumerable<T> values, Func<T, string> name, Action<T> set) where T : struct, Enum
    {
        using var combo = ImRaii.Combo(label, name(current));
        if (!combo) return false;
        var changed = false;
        foreach (var v in values)
        {
            if (!ImGui.Selectable(name(v), v.Equals(current))) continue;
            set(v);
            changed = true;
        }

        return changed;
    }

    private static string Worn(Player p) => p.Out ? "nothing" : string.Join(", ", p.Worn);

    private static string VenueLabel(Venue v) => v == Venue.Private ? "Private room (yell)" : "Public (party/alliance)";

    private static string CallLabel(CallChannel c) => c == CallChannel.Venue ? "Venue default" : c.ToString();

    private string Status(DeathrollGame g) => g.Phase switch
    {
        Phase.Setup when g.Round == 0 => "Add at least 2 players, then Start round.",
        Phase.Setup => $"Round {g.Round} stopped. Start a round when ready.",
        Phase.Opening => $"Round {g.Round}: opening rolls {g.Active.Count(p => p.Opening is not null)}/{g.Active.Count()}.",
        Phase.Turns => $"Round {g.Round}: next to roll is {g.Current?.Name} - {plugin.RollCommand(g.Limit)}.",
        Phase.Pick => $"Round {g.Round}: {g.Winner?.Name} rolled {g.WinningRoll} and wins. Who strips, and what?",
        Phase.GameOver => $"Game over: {g.Active.FirstOrDefault()?.Name ?? "nobody"} is the last one standing.",
        _ => "",
    };
}
