using System.Numerics;
using Dalamud.Interface.Colors;

namespace StripDeathroll14;

// Phase label and colour for the status card. Card and Badge themselves live in KosLib (Kos.Card / Kos.Badge).
public sealed partial class MainWindow
{
    private static string PhaseLabel(DeathrollGame g) => g.Phase switch
    {
        Phase.Setup when g.Round == 0 => "Setup",
        Phase.Setup => "Stopped",
        Phase.Opening => "Opening",
        Phase.Turns => "Turns",
        Phase.Pick => "Pick",
        Phase.GameOver => "Game over",
        _ => "",
    };

    private static Vector4 PhaseColor(Phase p) => p switch
    {
        Phase.Opening => ImGuiColors.TankBlue,
        Phase.Turns => ImGuiColors.HealerGreen,
        Phase.Pick => ImGuiColors.DalamudYellow,
        Phase.GameOver => ImGuiColors.DalamudRed,
        _ => ImGuiColors.DalamudGrey,
    };
}
