using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace StripDeathroll14;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sdr";
    private const int MinSpacingMs = 2000; // safety floor between any two sends
    private const int JitterMs = 1500;

    // What the game's own sanitiser may keep; a free-text line it would change is dropped, never sent altered.
    private const AllowedEntities TextEntities = AllowedEntities.UppercaseLetters | AllowedEntities.LowercaseLetters |
        AllowedEntities.Numbers | AllowedEntities.SpecialCharacters | AllowedEntities.CharacterList |
        AllowedEntities.OtherCharacters | AllowedEntities.Payloads | AllowedEntities.Unknown9 | AllowedEntities.CJK;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly IPartyList partyList;
    private readonly ITargetManager targetManager;
    private readonly ReminderClock reminder = new();
    private readonly WindowSystem windowSystem = new("StripDeathroll14");
    private readonly MainWindow window;
    private readonly Queue<(string? Prefix, string Text, bool FreeText)> outbox = new();
    private long nextSendAt;
    private const int RollRepeatMs = 10_000;
    private (int, Phase, int?) lastRollKey;
    private long lastRollAt;

    public Configuration Config { get; }
    public DeathrollGame Game { get; } = new();

    /// <summary>Announcements waiting for the host's Post press (when auto-post is off).</summary>
    public List<string> Proposed { get; } = [];

    public string? LastAlert { get; private set; }
    public int Queued => outbox.Count;

    /// <summary>Session only: dummy players, and every post goes to Echo instead of chat.</summary>
    public bool DebugMode { get; private set; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IPlayerState playerState,
        IFramework framework,
        IPartyList partyList,
        ITargetManager targetManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.playerState = playerState;
        this.framework = framework;
        this.partyList = partyList;
        this.targetManager = targetManager;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Normalize();

        window = new MainWindow(this);
        windowSystem.AddWindow(window);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += window.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += window.Toggle;
        chatGui.ChatMessage += OnChatMessage;
        clientState.Logout += OnLogout;
        framework.Update += OnUpdate;

        commandManager.AddHandler(CommandName, new CommandInfo((_, args) =>
        {
            if (args.Trim().Equals("roll", StringComparison.OrdinalIgnoreCase)) RollForMe();
            else window.Toggle();
        })
        {
            HelpMessage = "Open the Strip Deathroll window. /sdr roll makes your correct roll (for a hotbar macro).",
        });
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        framework.Update -= OnUpdate;
        clientState.Logout -= OnLogout;
        chatGui.ChatMessage -= OnChatMessage;
        pluginInterface.UiBuilder.OpenConfigUi -= window.Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= window.Toggle;
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
    }

    public void SaveConfig() => pluginInterface.SavePluginConfig(Config);

    public string MyName => playerState.CharacterName;
    public bool InAlliance => partyList.IsAlliance;
    public string RollCommand(int? n) => Lines.RollCommand(Config.Venue, InAlliance, n);
    public string CallPrefix => Lines.CallPrefix(Config.CallChannel, Config.Venue, InAlliance);

    // ---- host actions (all from button presses) ----

    public void StartRound()
    {
        Game.StartRound();
        if (Game.Phase == Phase.Opening) Propose(Lines.Opening(Game.Round, RollCommand(null)));
    }

    public void Pick(Player target, Piece piece)
    {
        if (!Game.Pick(target, piece)) return;
        Propose(Lines.Stripped(target.Name, piece, target.Lives));
        if (Game.Phase == Phase.GameOver) Propose(Lines.GameOver(Game.Active.First().Name));
        else CallCurrent();
    }

    public void AcceptPending() => Handle(Game.AcceptPending());

    public void CallCurrent()
    {
        if (Game.Current is { } p) Propose(Lines.Call(p.Name, RollCommand(Game.Limit)));
    }

    /// <summary>Your roll right now: a fresh /random for the opening or when you start the turns, else the previous roll; null when it is not your roll.</summary>
    public string? MyRoll => Game.Find(MyName) is { Out: false } me &&
        (Game.Phase == Phase.Opening && me.Opening is null || Game.Phase == Phase.Turns && Game.Current == me)
            ? RollCommand(Game.Phase == Phase.Opening ? null : Game.Limit)
            : null;

    /// <summary>Sends your roll once per game state; a second press within 10 s of the same state is refused, never queued.</summary>
    public void RollForMe()
    {
        if (MyRoll is not { } cmd) { Alert("It is not your roll."); return; }
        var key = (Game.Round, Game.Phase, Game.Limit);
        var now = Environment.TickCount64;
        if (key == lastRollKey && now - lastRollAt < RollRepeatMs) return;
        lastRollKey = key;
        lastRollAt = now;
        outbox.Enqueue((null, cmd, false));
    }

    public void PostProposed()
    {
        foreach (var line in Proposed) Enqueue(CallPrefix, line, false);
        Proposed.Clear();
    }

    public void PostText(string text)
    {
        foreach (var line in Lines.TextLines(text)) Enqueue(Lines.TextPrefix(Config.TextChannel), line, true);
    }

    public int AddGroup()
    {
        var names = new List<string>();
        foreach (var m in partyList) names.Add(m.Name.TextValue);
        for (var i = 0; i < 20; i++)
        {
            var addr = partyList.GetAllianceMemberAddress(i);
            if (addr != 0 && partyList.CreateAllianceMemberReference(addr) is { } m) names.Add(m.Name.TextValue);
        }

        AddCrossWorldParty(names);
        if (names.Count == 0 && MyName.Length > 0) names.Add(MyName);
        return names.Count(n => Game.Add(n)); // Add validates each name and skips duplicates
    }

    /// <summary>Adds your current in-game target; returns a note for the window.</summary>
    public string AddTarget()
    {
        if (targetManager.Target is not IPlayerCharacter pc) return "Target a player first.";
        var name = pc.Name.TextValue;
        return Game.Add(name) ? $"Added {name}." : $"{name} is already listed.";
    }

    public void SetDebugMode(bool on)
    {
        DebugMode = on;
        if (!on) Game.RemoveDummies(); // dummies never reach a real game
    }

    /// <summary>Debug mode: rolls for a dummy as if it came from chat. one = force a 1 to end the round.</summary>
    public void SimulateRoll(Player dummy, bool one)
    {
        if (!DebugMode || !dummy.IsDummy) return;
        int? max = Game.Phase == Phase.Opening ? null : Game.Limit;
        var value = one ? 1 : Random.Shared.Next(1, (max ?? DeathrollGame.OpeningMax) + 1);
        chatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = $"[Deathroll debug] {dummy.Name} rolls {value} (out of {max ?? DeathrollGame.OpeningMax})" });
        Receive(dummy, value, max);
    }

    // A cross-world party is not in IPartyList; read the local group from the cross-realm proxy (read-only).
    private static unsafe void AddCrossWorldParty(List<string> names)
    {
        if (!InfoProxyCrossRealm.IsCrossRealmParty()) return;
        var count = InfoProxyCrossRealm.GetPartyMemberCount();
        for (uint i = 0; i < count; i++)
        {
            var m = InfoProxyCrossRealm.GetGroupMember(i);
            if (m != null) names.Add(m->NameString);
        }
    }

    // ---- chat and tick ----

    private void Propose(string line)
    {
        if (Config.AutoPost) Enqueue(CallPrefix, line, false);
        else { Proposed.Clear(); Proposed.Add(line); } // only the latest line waits for Post; older ones are stale
    }

    private void Enqueue(string prefix, string line, bool freeText)
    {
        if (DebugMode)
        {
            chatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = $"[Deathroll debug, not sent] {prefix} {line}" });
            return;
        }

        if (Lines.IsSendable(line)) outbox.Enqueue((prefix, line, freeText));
        else Alert($"Not sent (too long, starts with /, or has a line break): {line}");
    }

    private void Alert(string text)
    {
        LastAlert = text;
        chatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = $"[Deathroll] {text}" });
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        ParsedRoll? roll;
        string? name;
        if (message.LogKind == XivChatType.RandomNumber)
        {
            roll = Rolls.ParseRandom(message.Message.TextValue);
            name = roll is null ? null : roll.Value.Name is null ? MyName : PlayerName(message.Message) ?? roll.Value.Name;
        }
        else if (message.LogKind == XivChatType.Party || message.LogKind == XivChatType.CrossParty || message.LogKind == XivChatType.Alliance)
        {
            roll = Rolls.ParseDice(message.Message.TextValue);
            name = roll is null ? null : PlayerName(message.Sender) ?? message.Sender.TextValue;
        }
        else
        {
            return;
        }

        if (roll is not { } r || name is null) return;
        Receive(Game.Find(name), r.Value, r.Max);
    }

    private void Receive(Player? player, int value, int? max)
    {
        var outcome = Game.OnRoll(player, value, max);
        var shownMax = (max ?? DeathrollGame.OpeningMax).ToString();
        switch (outcome)
        {
            case RollOutcome.OpeningWrongMax:
                Alert($"{player!.Name} rolled out of {shownMax}: the opening roll is a plain {RollCommand(null)}. Not counted.");
                break;
            case RollOutcome.AlreadyRolled:
                Alert($"{player!.Name} rolled again during the opening. Ignored.");
                break;
            case RollOutcome.OutOfTurn:
                Alert($"{player!.Name} rolled out of turn ({Game.Current?.Name} is up). Ignored.");
                break;
            case RollOutcome.WrongMax:
                Alert($"{player!.Name} rolled {value} out of {shownMax}, expected {RollCommand(Game.Limit)}. Accept it or ask for a reroll.");
                break;
            default:
                Handle(outcome);
                break;
        }
    }

    private void Handle(RollOutcome outcome)
    {
        switch (outcome)
        {
            case RollOutcome.OrderSet:
                if (Game.OpeningTie) Alert($"Tie for the highest opening roll: {Game.Starter?.Name} starts as the first of them in the list. Restart the round for a roll-off.");
                CallCurrent();
                break;
            case RollOutcome.Next:
                CallCurrent();
                break;
            case RollOutcome.Won:
                Propose(Lines.Won(Game.Winner!.Name, Game.WinningRoll, Game.Round));
                break;
        }
    }

    private void Remind()
    {
        var names = Game.Waiting.Select(p => p.Name).ToList();
        if (names.Count == 0) return;
        var opening = Game.Phase == Phase.Opening;
        Enqueue(Lines.TextPrefix(Config.ReminderChannel), Lines.Reminder(names, RollCommand(opening ? null : Game.Limit), opening), false);
    }

    private static string? PlayerName(SeString s) => s.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName;

    private void OnLogout(int type, int code)
    {
        outbox.Clear();
        Proposed.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        if (!clientState.IsLoggedIn) return;
        var now = Environment.TickCount64;

        // No reminder while a call is still waiting for Post: the player has not been asked yet.
        var waitKey = Config.ReminderEnabled && Proposed.Count == 0 ? Game.WaitingKey : null;
        if (reminder.Due(waitKey, now, Config.ReminderSeconds * 1000)) Remind();

        if (outbox.Count == 0 || now < nextSendAt) return;

        var (prefix, text, freeText) = outbox.Dequeue();
        if (freeText && !IsClean(text))
        {
            Alert($"Not sent (the game would change some characters): {text}");
            return;
        }

        if (Send(prefix is null ? text : $"{prefix} {text}"))
            nextSendAt = now + MinSpacingMs + Random.Shared.Next(JitterMs);
    }

    // Only plugin-composed text reaches here: a roll command, or a line checked by Lines.IsSendable behind a fixed prefix.
    private static unsafe bool Send(string text)
    {
        var ui = UIModule.Instance();
        if (ui == null) return false;
        var utf8 = Utf8String.FromString(text);
        ui->ProcessChatBoxEntry(utf8);
        utf8->Dtor(true);
        return true;
    }

    private static unsafe bool IsClean(string text)
    {
        var utf8 = Utf8String.FromString(text);
        utf8->SanitizeString(TextEntities);
        var clean = utf8->ToString() == text;
        utf8->Dtor(true);
        return clean;
    }
}
