using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
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

    public MainWindow(Plugin plugin) : base("Strip Deathroll###StripDeathroll14")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 360), MaximumSize = new Vector2(1200, 1400) };
    }

    /// <summary>The one look under test: MaterialKos in KosMos colours. It must be pushed before ImGui begins the window.</summary>
    public override void PreDraw()
    {
        windowTheme?.Dispose();
        windowTheme = KosTheme.PushWindow(KosAccent.KosMos, material: true);
    }

    /// <summary>Dalamud calls this whenever it called PreDraw, so the whole-window push is always popped.</summary>
    public override void PostDraw()
    {
        windowTheme?.Dispose();
        windowTheme = null;
    }

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("##tabs");
        if (!bar) return;
        using (var t = ImRaii.TabItem("Game")) if (t) DrawGame();
        using (var t = ImRaii.TabItem("History")) if (t) DrawHistory();
        using (var t = ImRaii.TabItem("Rules & Ad")) if (t) DrawTexts();
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
        if (ImGui.Checkbox("Auto-post", ref auto)) { config.AutoPost = auto; plugin.SaveConfig(); }
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
                ImGui.SameLine();
                ImGui.TextDisabled($"Reminder in {left} s ({ReminderClock.MaxPerTurn - plugin.RemindersSent} left).");
            }
            if (game.Phase == Phase.Turns && game.Starter is { } top)
                ImGui.TextColored(ImGuiColors.DalamudYellow, top.Opening is { } o ? $"Highest opening: {top.Name} ({o})." : $"Started by {top.Name} (next after last round's winner).");
            if (plugin.LastAlert is { } alert) Kos.Callout(alert, ImGuiColors.DalamudRed);

            if (game.Pending is { } pending && pending.Player == game.Current)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, $"Accept {pending.Value}")) plugin.AcceptPending();
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.RedoAlt, "Ask for reroll")) plugin.CallCurrent();
            }

            if (game.Phase == Phase.Pick) DrawPick(game);
        });

        using (ImRaii.Disabled(!game.CanStartRound))
        {
            if (game.Phase == Phase.Setup ? Kos.PrimaryButton(FontAwesomeIcon.Play, "Start round") : ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Restart round"))
                plugin.StartRound();
        }
        ImGui.SameLine();
        var myRoll = plugin.MyRoll;
        using (ImRaii.Disabled(myRoll is null))
        {
            if (Kos.IconButton(FontAwesomeIcon.Dice, myRoll is null ? "Roll for me" : $"Roll for me: {myRoll}",
                    "Your roll: a fresh /random to open or to start the turns, else up to the previous roll. /sdr roll does the same from a macro."))
                plugin.RollForMe();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(game.Current is null)) { if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Bullhorn, "Call again")) plugin.CallCurrent(); }
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Broom, "New game") && ImGui.GetIO().KeyCtrl) game.NewGame();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click: everyone back to 3 pieces, history cleared.");

        DrawProposed();
        Kos.Section("Players");
        DrawAddPlayers(game);
        var view = config.CircleView ? 0 : 1;
        if (Kos.Segmented("view", ref view, "Circle", "List")) { config.CircleView = view == 0; plugin.SaveConfig(); }
        ImGui.SameLine();
        ImGui.TextDisabled(config.CircleView ? "Clockwise from the top. Drag a seat to move it; right-click for more." : "Turn order runs down the list.");
        if (config.CircleView) DrawCircle(game);
        else DrawPlayers(game);
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
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        using (var combo = ImRaii.Combo("##piece", pickPiece is { } shown ? shown.ToString() : "Piece"))
        {
            if (combo)
            {
                foreach (var piece in pickTarget.Worn)
                    if (ImGui.Selectable(piece.ToString(), piece == pickPiece)) pickPiece = piece;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(pickPiece is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Confirm") && pickPiece is { } chosen)
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
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PaperPlane, "Post")) plugin.PostProposed();
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Discard")) plugin.Proposed.Clear();
        });
    }

    private void DrawAddPlayers(DeathrollGame game)
    {
        ImGui.SetNextItemWidth(180);
        var enter = ImGui.InputTextWithHint("##name", "Forename Surname", ref newName, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add") || enter)
        {
            addNote = game.Add(newName) ? "" : "Needs Forename Surname (letters, ' or -), not already listed.";
            if (addNote.Length == 0) newName = "";
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Crosshairs, "Add target")) addNote = plugin.AddTarget();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Users, "Add party/alliance")) addNote = $"Added {plugin.AddGroup()} player(s).";
        if (plugin.DebugMode)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Robot, "Add dummy")) addNote = game.AddDummy() ? "" : "All dummy names are in use.";
        }

        if (addNote.Length > 0) ImGui.TextDisabled(addNote);
    }

    private void DrawPlayers(DeathrollGame game)
    {
        using var table = ImRaii.Table("##players", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Last roll", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Wearing", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Wins", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("##edit", ImGuiTableColumnFlags.WidthFixed, plugin.DebugMode ? 150 : 88);
        ImGui.TableHeadersRow();

        Action? edit = null; // applied after the loop so the list is not changed while it is drawn
        for (var i = 0; i < game.Players.Count; i++)
        {
            var p = game.Players[i];
            var top = p == game.Starter;
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p == game.Current ? ">" : (i + 1).ToString());
            ImGui.TableNextColumn();
            var label = p.Name + (p == game.Current ? "  (next to roll)" : top ? (p.Opening is null ? "  (started)" : "  (highest)") : "") + (p.IsDummy ? "  [dummy]" : "");
            if (p.Out) ImGui.TextDisabled($"{p.Name} (out)");
            else if (p == game.Current) ImGui.TextColored(ImGuiColors.HealerGreen, label);
            else if (top) ImGui.TextColored(ImGuiColors.DalamudYellow, label);
            else ImGui.TextUnformatted(label);
            ImGui.TableNextColumn();
            var last = game.LastRoll(p);
            var lastText = last?.Value.ToString() ?? "-";
            if (last is { } lr && DeathrollGame.NotCounted(lr.Note)) ImGui.TextDisabled(lastText);
            else if (top) ImGui.TextColored(ImGuiColors.DalamudYellow, lastText);
            else ImGui.TextUnformatted(lastText);
            if (last is { } l && ImGui.IsItemHovered()) ImGui.SetTooltip($"Round {l.Round}: {l.Value} out of {l.Max ?? DeathrollGame.OpeningMax} ({NoteLabel(l.Note)})");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Worn(p));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Wins.ToString());
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Up")) edit = () => game.Move(p, -1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Dn")) edit = () => game.Move(p, 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("X") && ImGui.GetIO().KeyCtrl) edit = () => game.Remove(p);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click to remove.");
            if (plugin.DebugMode && p.IsDummy && !p.Out)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Roll")) edit = () => plugin.SimulateRoll(p, false);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Roll for this dummy (a fresh /random or up to the current number).");
                ImGui.SameLine();
                if (ImGui.SmallButton("1")) edit = () => plugin.SimulateRoll(p, true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Make this dummy roll a 1.");
            }
        }

        edit?.Invoke();
    }

    private void DrawHistory()
    {
        var game = plugin.Game;
        var rounds = game.RollLog.Select(r => r.Round).Concat(game.History.Select(h => h.Round)).Distinct().OrderDescending().ToList();
        if (rounds.Count == 0) { Kos.EmptyState("No rolls yet."); return; }

        foreach (var round in rounds) // newest first, the current round open
        {
            var result = game.History.FirstOrDefault(h => h.Round == round);
            var title = result is null
                ? $"Round {round}" + (round == game.Round && game.Phase is not Phase.Setup ? " (in play)" : " (no result)")
                : $"Round {round}: {result.Winner} won with {result.WinningRoll}; {result.Target} lost their {Lines.PieceName(result.Piece)} ({(result.TargetLivesLeft > 0 ? $"{result.TargetLivesLeft} left" : "out")})";
            if (!ImGui.CollapsingHeader($"{title}###round{round}", round == game.Round ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) continue;

            using var table = ImRaii.Table($"##rolls{round}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
            if (!table) continue;
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Roll", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Out of", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var r in game.RollLog.Where(r => r.Round == round))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Player);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Value.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted((r.Max ?? DeathrollGame.OpeningMax).ToString());
                ImGui.TableNextColumn();
                var note = NoteLabel(r.Note);
                if (r.Note is RollNote.Counted or RollNote.Opening) ImGui.TextDisabled(note);
                else if (r.Note == RollNote.Won) ImGui.TextColored(ImGuiColors.HealerGreen, note);
                else ImGui.TextColored(ImGuiColors.DalamudOrange, note);
            }
        }
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
    }

    private void DrawText(string label, string value, Action<string> set, string defaultValue)
    {
        Kos.Section(label);
        if (ImGui.InputTextMultiline($"##{label}", ref value, 4000, new Vector2(-1, 110))) set(value);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Bullhorn, $"Post {label.ToLowerInvariant()}")) plugin.PostText(value);
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, $"Default##{label}")) { set(defaultValue); plugin.SaveConfig(); } // a saved text never picks up a new default by itself
    }

    private void DrawSettings()
    {
        var config = plugin.Config;
        Kos.Section("Reminder", "01");
        var on = config.ReminderEnabled;
        if (ImGui.Checkbox("Remind whoever has not rolled", ref on)) { config.ReminderEnabled = on; plugin.SaveConfig(); }
        var secs = config.ReminderSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Seconds of quiet", ref secs, Configuration.MinReminderSeconds, 120))
            config.ReminderSeconds = Math.Clamp(secs, Configuration.MinReminderSeconds, 120);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        ImGui.SetNextItemWidth(120);
        if (Combo("Remind in", config.ReminderChannel, [TextChannel.Say, TextChannel.Party], c => c.ToString(), c => config.ReminderChannel = c)) plugin.SaveConfig();
        ImGui.TextDisabled($"Posts \"Reminder: Name, it's your turn - roll ...\" (or lists everyone missing an opening roll) after that long\n" +
            $"with no roll, at most {ReminderClock.MaxPerTurn} times per turn, whether or not the call was posted. Not while a wrong roll waits for you.");

        Kos.Section("World markers", "02");
        var world = config.WorldMarkers;
        if (ImGui.Checkbox("Mark the players in the world", ref world)) { config.WorldMarkers = world; plugin.SaveConfig(); }
        using (ImRaii.Disabled(!config.WorldMarkers))
        {
            var mode = config.WorldMarkersGpu ? 1 : 0;
            if (Kos.Segmented("##drawwith", ref mode, "ImGui", "GPU")) { config.WorldMarkersGpu = mode == 1; plugin.SaveConfig(); }
            ImGui.SameLine();
            Kos.HelpMarker("ImGui: drawn over everything, so the rings show through walls and other players.\n" +
                "GPU: KosDraw's Direct3D path. Rings fade behind scenery and stay off the game's own UI.\n" +
                "If a GPU part fails it switches itself off and ImGui draws that part instead.");
            ImGui.TextDisabled("Crimson ring and the number to roll: whoever is up. Gold: owes an opening roll, started the turns\n" +
                "(dot overhead) or won the round. Thin ring: everyone else. Players who are out get none.");
            ImGui.TextDisabled(plugin.WorldStatus);
        }

        Kos.Section("Debug", "03");
        var debug = plugin.DebugMode;
        if (ImGui.Checkbox("Debug mode (this session only)", ref debug)) plugin.SetDebugMode(debug);
        ImGui.TextDisabled("Adds an Add dummy button and Roll / 1 buttons on dummy rows, so you can play a whole game alone.\n" +
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

    private static string NoteLabel(RollNote n) => n switch
    {
        RollNote.Opening => "opening",
        RollNote.OpeningWrongNumber => "opening, wrong number (not counted)",
        RollNote.OpeningRepeat => "second opening roll (ignored)",
        RollNote.OutOfTurn => "out of turn (ignored)",
        RollNote.WrongNumber => "wrong number (not counted unless accepted)",
        RollNote.Accepted => "wrong number, accepted",
        RollNote.Won => "winning roll",
        _ => "",
    };

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
