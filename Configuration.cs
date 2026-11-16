using Dalamud.Configuration;

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

    /// <summary>On by default: KosDraw marks the players in the world (who is up, who owes an opening roll, who won).</summary>
    public bool WorldMarkers { get; set; } = true;

    /// <summary>Off by default: draw the world marks with KosDraw's Direct3D path (faded behind scenery) instead of ImGui.</summary>
    public bool WorldMarkersGpu { get; set; }

    /// <summary>On by default: a gleam round whoever is up, a ripple from each roll and a light passing the turn on. Off: still marks.</summary>
    public bool WorldMarkersMotion { get; set; } = true;

    /// <summary>Off by default: frosted glass behind the window (Dalamud's blur, tinted night blue; the window turns slightly see-through).</summary>
    public bool FrostedWindow { get; set; }

    /// <summary>Off by default: a timed nudge to whoever has not rolled yet.</summary>
    public bool ReminderEnabled { get; set; }
    public int ReminderSeconds { get; set; } = 30;
    public TextChannel ReminderChannel { get; set; } = TextChannel.Say; // Say or Party only

    /// <summary>Off by default. On: the reminder prints to your own Echo and nothing is sent.</summary>
    public bool ReminderOnlyMe { get; set; }

    /// <summary>The wording of every line the plugin posts (rules and ad are their own texts below).</summary>
    public Messages Messages { get; set; } = new();

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
        if (ReminderChannel is not (TextChannel.Say or TextChannel.Party)) ReminderChannel = TextChannel.Say;
        ReminderSeconds = Math.Clamp(ReminderSeconds, MinReminderSeconds, 120);
        SendMinSeconds = Math.Clamp(SendMinSeconds, Outbox.MinGapMs / 1000, Outbox.MaxGapMs / 1000);
        SendMaxSeconds = Math.Clamp(SendMaxSeconds, SendMinSeconds, Outbox.MaxGapMs / 1000);
        Messages ??= new();
        RulesText ??= "";
        AdText ??= "";
    }
}
