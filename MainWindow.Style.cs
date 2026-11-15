using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace StripDeathroll14;

// Small drawn pieces: a framed card and a phase badge. Colours come from the live style or the
// semantic ImGuiColors, so the user's Dalamud theme stays in charge; nothing is pushed window-wide.
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

    /// <summary>A pill with dark text on a solid colour (all the colours used are bright, so it reads on any theme).</summary>
    private static void Badge(string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = ImGui.CalcTextSize(text);
        var padX = 6 * scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size.X + 2 * padX, size.Y)); // one text line tall, so SameLine text stays aligned
        var draw = ImGui.GetWindowDrawList();
        var grow = new Vector2(0, scale);
        draw.AddRectFilled(pos - grow, pos + new Vector2(size.X + 2 * padX, size.Y) + grow, ImGui.GetColorU32(color), size.Y / 2 + scale);
        draw.AddText(pos + new Vector2(padX, 0), 0xFF000000, text);
    }

    /// <summary>
    /// Full-width framed panel round whatever body draws. The frame goes on a background channel after the body
    /// is laid out. The body must not draw a table or columns (they split channels themselves).
    /// </summary>
    private static void Card(string id, Vector4? edge, Action body)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pad = new Vector2(8, 6) * scale;
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        draw.ChannelsSplit(2);
        Vector2 end;
        try
        {
            draw.ChannelsSetCurrent(1);
            ImGui.SetCursorScreenPos(start + pad);
            using (ImRaii.PushId(id))
            using (ImRaii.Group()) body();
            end = ImGui.GetItemRectMax();
            draw.ChannelsSetCurrent(0);
            var max = new Vector2(start.X + width, end.Y + pad.Y);
            var rounding = MathF.Max(style.FrameRounding, 4 * scale);
            draw.AddRectFilled(start, max, ImGui.GetColorU32(ImGuiCol.FrameBg, 0.6f), rounding);
            draw.AddRect(start, max, edge is { } e ? ImGui.GetColorU32(e with { W = 0.7f }) : ImGui.GetColorU32(ImGuiCol.Border), rounding, ImDrawFlags.None, scale);
        }
        finally
        {
            draw.ChannelsMerge(); // always, or the window's draw list is left split
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + pad.Y));
        ImGui.Dummy(new Vector2(width, 0));
    }
}
