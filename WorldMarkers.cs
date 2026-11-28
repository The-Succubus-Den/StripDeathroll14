using System.Numerics;
using KosDraw;
using KosLib;

namespace StripDeathroll14;

/// <summary>The KosDraw shapes under the players, in the window's look: MaterialKos colours from the KosMos accent.</summary>
internal sealed class WorldMarkers
{
    private const float RippleSeconds = 1.2f, PulseSeconds = 0.9f, GleamSeconds = 2.5f;
    private readonly TurnCues cues = new();
    private readonly List<(string Name, double Start, Vector4 Color)> ripples = [];
    private (Player From, Player To, double Start)? handover;
    private (HashSet<string> Names, double Start)? nudge; // a reminder just went out for these players
    private const float Inner = 0.85f, Outer = 1.0f; // metres round the player's feet
    private const float Tall = 9f, Short = 5f; // pillar heights: who rolls and who won stand out over next and starter
    private static readonly Vector3 OverHead = new(0, 2.4f, 0), Lift = new(0, 0.3f, 0); // the pulse leaves from the rings, a little off the ground

    // Always the dark scheme: these are drawn over the game world, not over the user's window theme.
    private static readonly MaterialScheme Scheme = MaterialKos.Scheme(KosAccent.KosMos, light: false)!;
    private float beamScale = 1f; // set by each Build from the Beam opacity setting

    /// <param name="now">Seconds on any steady clock.</param>
    /// <param name="motion">Off: no gleam, ripples or hand-over pulse (the cues are still taken, so nothing replays when it comes back on).</param>
    /// <param name="beam">The pillars' opacity, 0 to 1 of the original look; 0 draws no pillars.</param>
    public void Build(KosShapes s, DeathrollGame game, IReadOnlyList<(Player Player, Vector3 At)> seen, double now, bool motion, float beam)
    {
        var m = Scheme;
        beamScale = Math.Clamp(beam, 0f, 1f);
        Motion(s, game, seen, now, motion);
        DrawNudge(s, game, seen, now, motion);
        foreach (var (p, at) in seen)
        {
            var head = at + OverHead;
            switch (game.MarkerFor(p))
            {
                case Marker.Turn:
                    Beacon(s, at, m.Primary, Tall);
                    s.Ring(at, Inner, Outer + 0.05f, m.Primary with { W = 0.9f });
                    if (motion) s.Gleam(at, Outer + 0.12f, (float)(now % GleamSeconds / GleamSeconds), m.OnSurface with { W = 0.9f });
                    s.Text(head, game.Limit is { } n ? $"Roll {n}" : "Roll /random", m.Primary);
                    break;
                case Marker.Next:
                    Beacon(s, at, m.OnSurface, Short);
                    s.Ring(at, Inner, Outer - 0.05f, m.OnSurface with { W = 0.8f });
                    s.Text(head, p == game.Starter ? "Next (started)" : "Next", m.OnSurface);
                    break;
                case Marker.Starter:
                    Beacon(s, at, m.Tertiary, Short);
                    s.Ring(at, Inner, Outer - 0.08f, m.Tertiary with { W = 0.7f });
                    s.Text(head, "Started", m.Tertiary);
                    break;
                case Marker.Winner:
                    Beacon(s, at, m.Tertiary, Tall);
                    s.Ring(at, Inner, Outer + 0.05f, m.Tertiary with { W = 0.95f });
                    s.Text(head, "Winner: picking", m.Tertiary);
                    break;
                case Marker.Opening:
                    s.Ring(at, Inner, Outer - 0.05f, m.Tertiary with { W = 0.85f });
                    s.Text(head, "Opening roll", m.Tertiary);
                    break;
                case Marker.Seated:
                    s.Ring(at, Inner, Inner + 0.05f, m.Outline with { W = 0.6f });
                    break;
            }
        }
    }

    /// <summary>A reminder went out for these players: a short, soft ripple round each one's ring while they are still awaited.</summary>
    public void Nudge(IEnumerable<string> names, double now) => nudge = (names.ToHashSet(), now);

    private void DrawNudge(KosShapes s, DeathrollGame game, IReadOnlyList<(Player Player, Vector3 At)> seen, double now, bool motion)
    {
        if (nudge is not { } nd) return;
        var t = (float)((now - nd.Start) / Plugin.NudgeSeconds);
        if (t is < 0 or >= 1) { nudge = null; return; }
        var waiting = game.Waiting;
        var c = Scheme.OnSurface;
        foreach (var (p, at) in seen)
        {
            if (!nd.Names.Contains(p.Name) || !waiting.Contains(p)) continue;
            if (motion) s.Ripple(at, 1.8f, t, c with { W = 0.8f }, waves: 2);
            else s.Ring(at, Outer + 0.1f, Outer + 0.18f, c with { W = 0.7f * (1 - t) }); // Reduce motion: a still ring that fades
        }
    }

    /// <summary>For frames that draw nothing: take the cues anyway, so rolls from while the marks were off never replay later.</summary>
    public void Skip(DeathrollGame game)
    {
        cues.Take(game);
        ripples.Clear();
        handover = null;
    }

    /// <summary>A ripple from each new roll (gold for a winning roll, grey for one that did not count) and a pulse along the turn's hand-over.</summary>
    private void Motion(KosShapes s, DeathrollGame game, IReadOnlyList<(Player Player, Vector3 At)> seen, double now, bool motion)
    {
        if (!motion)
        {
            Skip(game);
            return;
        }
        var (rolls, passed) = cues.Take(game);
        var m = Scheme;
        foreach (var r in rolls)
        {
            ripples.RemoveAll(x => x.Name == r.Player); // one per player: a burst of rolls never stacks up
            var c = r.Note == RollNote.Won ? m.Tertiary : DeathrollGame.NotCounted(r.Note) ? m.Outline : m.Primary;
            ripples.Add((r.Player, now, c with { W = 0.9f }));
        }
        if (passed is { } h) handover = (h.From, h.To, now);
        ripples.RemoveAll(x => now - x.Start >= RippleSeconds);
        foreach (var (name, start, c) in ripples)
            foreach (var (p, at) in seen)
                if (p.Name == name) s.Ripple(at, 2.5f, (float)((now - start) / RippleSeconds), c);
        if (handover is not { } ho) return;
        var t = (float)((now - ho.Start) / PulseSeconds);
        if (t > 1) { handover = null; return; }
        Vector3? a = null, b = null;
        foreach (var (p, at) in seen)
        {
            if (p == ho.From) a = at;
            if (p == ho.To) b = at;
        }
        if (a is { } from && b is { } to) s.Pulse(from + Lift, to + Lift, t, m.Primary with { W = 0.95f }, lift: 1.5f);
    }

    /// <summary>A pillar of light over the ring: a soft wide glow round a brighter core, both scaled by the Beam opacity setting.</summary>
    private void Beacon(KosShapes s, Vector3 at, Vector4 c, float height)
    {
        if (beamScale <= 0) return;
        s.Pillar(at, Inner, height, c with { W = 0.16f * beamScale });
        s.Pillar(at, 0.22f, height, c with { W = 0.4f * beamScale });
    }
}
