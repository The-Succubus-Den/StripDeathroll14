using System.Numerics;
using KosDraw;
using KosLib;

namespace StripDeathroll14;

/// <summary>The KosDraw shapes under the players, in the window's look: MaterialKos colours from the KosMos accent.</summary>
internal static class WorldMarkers
{
    private const float Inner = 0.85f, Outer = 1.0f; // metres round the player's feet
    private const float Tall = 9f, Short = 5f; // pillar heights: who rolls and who won stand out over next and starter
    private static readonly Vector3 OverHead = new(0, 2.4f, 0);

    // Always the dark scheme: these are drawn over the game world, not over the user's window theme.
    private static readonly MaterialScheme Scheme = MaterialKos.Scheme(KosAccent.KosMos, light: false)!;

    public static void Build(KosShapes s, DeathrollGame game, IReadOnlyList<(Player Player, Vector3 At)> seen)
    {
        var m = Scheme;
        foreach (var (p, at) in seen)
        {
            var head = at + OverHead;
            switch (game.MarkerFor(p))
            {
                case Marker.Turn:
                    Beacon(s, at, m.Primary, Tall);
                    s.Ring(at, Inner, Outer + 0.05f, m.Primary with { W = 0.9f });
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

    /// <summary>A pillar of light over the ring: a soft wide glow round a brighter core.</summary>
    private static void Beacon(KosShapes s, Vector3 at, Vector4 c, float height)
    {
        s.Pillar(at, Inner, height, c with { W = 0.16f });
        s.Pillar(at, 0.22f, height, c with { W = 0.4f });
    }
}
