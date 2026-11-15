using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using KosLib;

namespace StripDeathroll14;

// The players seated round a circle in turn order (clockwise from the top), so who rolls now and who is next is obvious.
public sealed partial class MainWindow
{
    private const string SeatPayload = "SDR_SEAT";
    private int dragFrom = -1;

    private void DrawCircle(DeathrollGame game)
    {
        var players = game.Players;
        if (players.Count == 0) { Kos.EmptyState("No players yet."); return; }

        var scale = ImGuiHelpers.GlobalScale;
        var line = ImGui.GetTextLineHeight();
        var avail = ImGui.GetContentRegionAvail();
        var height = MathF.Max(240 * scale, MathF.Min(avail.X, avail.Y - 4 * scale));
        var origin = ImGui.GetCursorScreenPos();
        var center = origin + new Vector2(avail.X / 2, height / 2);

        var n = players.Count;
        var labelW = players.Max(p => ImGui.CalcTextSize(p.Name).X);
        var node = Math.Clamp(160 * scale / n, 11 * scale, 20 * scale);
        var ring = MathF.Max(40 * scale, MathF.Min(avail.X / 2 - labelW - node - 10 * scale, height / 2 - node - 2 * line - 8 * scale));

        var draw = ImGui.GetWindowDrawList();
        var faint = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var text = ImGui.GetColorU32(ImGuiCol.Text);
        var green = ImGui.GetColorU32(ImGuiColors.HealerGreen);
        var yellow = ImGui.GetColorU32(ImGuiColors.DalamudYellow);

        Vector2 Dir(int i) { var a = -MathF.PI / 2 + 2 * MathF.PI * i / n; return new Vector2(MathF.Cos(a), MathF.Sin(a)); }
        Vector2 Seat(int i) => center + Dir(i) * ring;

        // The table edge, with small arrowheads between seats showing the turn goes clockwise.
        draw.AddCircle(center, ring, faint, 64, 1.5f * scale);
        if (n >= 2)
            for (var i = 0; i < n; i++)
            {
                var a = -MathF.PI / 2 + 2 * MathF.PI * (i + 0.5f) / n;
                var at = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * ring;
                Arrowhead(draw, at, new Vector2(-MathF.Sin(a), MathF.Cos(a)), 5 * scale, faint);
            }

        // Who rolls now -> who is up after them.
        var current = game.Current;
        var next = current is null ? null : game.NextActive(current);
        if (current is not null && next is not null)
        {
            Vector2 from = Seat(players.IndexOf(current)), to = Seat(players.IndexOf(next));
            var d = Vector2.Normalize(to - from);
            var tip = to - d * (node + 3 * scale);
            draw.AddLine(from + d * (node + 3 * scale), tip, green, 2.5f * scale);
            Arrowhead(draw, tip, d, 8 * scale, green);
        }

        Action? edit = null; // applied after drawing so the list is not changed mid-loop
        for (var i = 0; i < n; i++)
        {
            var p = players[i];
            var pos = Seat(i);
            var isCurrent = p == current;
            var isStarter = p == game.Starter;

            if (isCurrent) draw.AddCircle(pos, node + (4 + 2 * MathF.Sin((float)ImGui.GetTime() * 4)) * scale, green, 32, 2 * scale);
            var fill = p.Out ? ImGui.GetColorU32(ImGuiCol.FrameBg)
                : isCurrent ? green
                : isStarter ? yellow
                : ImGui.GetColorU32(ImGuiCol.Button);
            draw.AddCircleFilled(pos, node, fill, 32);
            if (p == next) draw.AddCircle(pos, node, green, 32, 2 * scale);
            var num = (i + 1).ToString();
            var numSize = ImGui.CalcTextSize(num);
            draw.AddText(pos - numSize / 2, isCurrent || isStarter ? 0xFF000000 : p.Out ? faint : text, num);

            // Name and what they still wear, outside the ring, pushed away from the centre.
            var dir = Dir(i);
            var name = p.Name + (p.IsDummy ? "*" : "");
            // Pieces still worn as dots (drawn, not font glyphs, which the game font may lack), then the last roll.
            var dot = 3 * scale;
            var dotsW = p.Out ? 0 : DeathrollGame.StartLives * 3 * dot;
            var sub = p.Out ? "out" : game.LastRoll(p) is { } lr ? lr.Value.ToString() : "";
            var w = MathF.Max(ImGui.CalcTextSize(name).X, dotsW + ImGui.CalcTextSize(sub).X);
            var anchor = center + dir * (ring + node + 6 * scale);
            var x = dir.X > 0.3f ? anchor.X : dir.X < -0.3f ? anchor.X - w : anchor.X - w / 2;
            var y = dir.Y < -0.3f ? anchor.Y - 2 * line : dir.Y > 0.3f ? anchor.Y : anchor.Y - line;
            var nameCol = p.Out ? faint : isCurrent ? green : isStarter ? yellow : text;
            draw.AddText(new Vector2(x, y), nameCol, name);
            if (!p.Out) Kos.Pips(draw, new Vector2(x, y + line * 1.5f), p.Lives, DeathrollGame.StartLives, nameCol, faint, dot);
            draw.AddText(new Vector2(x + dotsW, y + line), faint, sub);

            // The seat itself: hover for detail, drag onto another seat to move there, right-click for actions.
            ImGui.SetCursorScreenPos(pos - new Vector2(node));
            ImGui.InvisibleButton($"##seat{i}", new Vector2(2 * node));
            if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                ImGui.SetTooltip($"{p.Name}{(p.IsDummy ? " (dummy)" : "")}\nSeat {i + 1}{(isCurrent ? ", next to roll" : p == next ? ", up after that" : "")}\nWearing: {Worn(p)}\nWins: {p.Wins}" +
                    (game.LastRoll(p) is { } l ? $"\nLast roll: {l.Value} (round {l.Round}, {NoteLabel(l.Note)})" : ""));
            if (ImGui.BeginDragDropSource())
            {
                dragFrom = i;
                ImGui.SetDragDropPayload(SeatPayload, ReadOnlySpan<byte>.Empty);
                ImGui.TextUnformatted($"Move {p.Name}");
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                if (!ImGui.AcceptDragDropPayload(SeatPayload).IsNull && dragFrom >= 0 && dragFrom < n && dragFrom != i)
                {
                    var moved = players[dragFrom];
                    var to = i;
                    edit = () => game.MoveTo(moved, to);
                    dragFrom = -1;
                }
                ImGui.EndDragDropTarget();
            }
            if (ImGui.BeginPopupContextItem($"##seatmenu{i}"))
            {
                ImGui.TextDisabled(p.Name);
                if (ImGui.Selectable("Move one seat back (anticlockwise)")) edit = () => game.Move(p, -1);
                if (ImGui.Selectable("Move one seat on (clockwise)")) edit = () => game.Move(p, 1);
                if (plugin.DebugMode && p.IsDummy && !p.Out)
                {
                    if (ImGui.Selectable("Roll for this dummy")) edit = () => plugin.SimulateRoll(p, false);
                    if (ImGui.Selectable("Make this dummy roll a 1")) edit = () => plugin.SimulateRoll(p, true);
                }
                if (ImGui.Selectable("Remove (hold Ctrl)", false, ImGui.GetIO().KeyCtrl ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled)) edit = () => game.Remove(p);
                ImGui.EndPopup();
            }
        }

        // In the middle: the round and whose roll it is.
        var mid = new List<(string, uint)> { (game.Round > 0 ? $"Round {game.Round}" : "Not started", text) };
        if (current is not null && game.Phase == Phase.Turns) mid.Add(($"{current.Name.Split(' ')[0]} to roll", green));
        if (game.Phase == Phase.Turns) mid.Add((game.Limit is { } lim ? $"up to {lim}" : "fresh roll", faint));
        else if (game.Phase == Phase.Opening) mid.Add(($"opening {game.Active.Count(p => p.Opening is not null)}/{game.Active.Count()}", faint));
        else if (game.Phase == Phase.Pick && game.Winner is { } win) mid.Add(($"{win.Name.Split(' ')[0]} won", yellow));
        var top = center.Y - mid.Count * line / 2;
        foreach (var (s, col) in mid)
        {
            draw.AddText(new Vector2(center.X - ImGui.CalcTextSize(s).X / 2, top), col, s);
            top += line;
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left)) dragFrom = -1;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(avail.X, height)); // claims the circle's space so the window scrolls past it
        edit?.Invoke();
    }

    private static void Arrowhead(ImDrawListPtr draw, Vector2 tip, Vector2 dir, float size, uint col)
    {
        var side = new Vector2(-dir.Y, dir.X) * size * 0.6f;
        draw.AddTriangleFilled(tip, tip - dir * size + side, tip - dir * size - side, col);
    }
}
