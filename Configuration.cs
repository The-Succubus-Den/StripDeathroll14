using Dalamud.Configuration;
using KosLib;

namespace StripDeathroll14;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int MinReminderSeconds = 15; // floor: never nag faster than this

    public int Version { get; set; } = 1;
    public Venue Venue { get; set; } = Venue.Private;

    /// <summary>Where calls and results go; Venue = say in a private room, party/alliance in public.</summary>
    public CallChannel CallChannel { get; set; } = CallChannel.Venue;

    /// <summary>Off by default: calls and results wait in the window for a Post press.</summary>
    public bool AutoPost { get; set; }

    /// <summary>Each gap between two sent lines is random within these limits, 2-5 s (Outbox clamps them too).</summary>
    public int SendMinSeconds { get; set; } = 2;
    public int SendMaxSeconds { get; set; } = 4;

    public TextChannel TextChannel { get; set; } = TextChannel.Say;

    /// <summary>Game tab shows the players seated round a circle (drag to reseat) instead of the table.</summary>
    public bool CircleView { get; set; } = true;

    /// <summary>Window accent preset; Dalamud style (the default) pushes no colours.</summary>
    public KosAccent Accent { get; set; } = KosAccent.Dalamud;

    /// <summary>Off by default: a timed nudge to whoever has not rolled yet.</summary>
    public bool ReminderEnabled { get; set; }
    public int ReminderSeconds { get; set; } = 30;
    public TextChannel ReminderChannel { get; set; } = TextChannel.Say; // Say or Party only

    public string RulesText { get; set; } =
        "Strip Deathroll! Everyone rolls /random or /dice to start; highest goes first, then clockwise.\n" +
        "On your turn, roll /random or /dice up to the number the player before you rolled.\n" +
        "Roll a 0, 1, 69 or 666 to win the round - then you pick who strips.\n" +
        "Everyone has 3 pieces. Lose all 3 and you are out!";

    public string AdText { get; set; } = "Strip Deathroll starting soon! Ask to join the game.";

    /// <summary>Pulls hand-edited values back inside what the UI offers.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Venue)) Venue = Venue.Private;
        if (!Enum.IsDefined(TextChannel)) TextChannel = TextChannel.Say;
        if (!Enum.IsDefined(CallChannel)) CallChannel = CallChannel.Venue;
        if (!Enum.IsDefined(Accent)) Accent = KosAccent.Dalamud;
        if (ReminderChannel is not (TextChannel.Say or TextChannel.Party)) ReminderChannel = TextChannel.Say;
        ReminderSeconds = Math.Clamp(ReminderSeconds, MinReminderSeconds, 120);
        SendMinSeconds = Math.Clamp(SendMinSeconds, Outbox.MinGapMs / 1000, Outbox.MaxGapMs / 1000);
        SendMaxSeconds = Math.Clamp(SendMaxSeconds, SendMinSeconds, Outbox.MaxGapMs / 1000);
        RulesText ??= "";
        AdText ??= "";
    }
}
