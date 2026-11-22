using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace NobodyValley.Jobs;

/// <summary>
/// Fighting with the equipped weapon, the vanilla way: face the monster and swing. A melee
/// swing hits the tiles in front of the player at the game's own range, and the game decides
/// hits, damage, knockback and deaths - this job just keeps swinging while a monster near the
/// target tile is still up.
/// </summary>
internal static class CombatJobs
{
    private static Farmer Me => Game1.player;

    public static IEnumerator Attack(Job job, Point tile)
    {
        var weapon = Me.Items.OfType<MeleeWeapon>().FirstOrDefault(w => !w.isScythe());
        if (weapon == null)
            throw new JobFailed("you don't have a weapon - put a sword in your backpack first");
        var loc = Me.currentLocation;
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");
        if (NearestMonster(loc, tile, 4) == null)
            throw new JobFailed($"nothing to fight at {tile.X},{tile.Y} - swing at something with fangs");

        int swings = 0;
        for (int i = 0; i < 20; i++)
        {
            var monster = NearestMonster(loc, tile, 5);
            if (monster == null)
            {
                job.Summary = swings == 0
                    ? $"nothing to fight at {tile.X},{tile.Y}"
                    : $"The {weapon.DisplayName} cleared {tile.X},{tile.Y} after {swings} swing(s).";
                yield break;
            }
            // Face it and swing - the swing hits in front of the player at melee range.
            Me.faceGeneralDirection(monster.Position, 0, false, false);
            yield return Steps.Swing(weapon);
            swings++;
            yield return Wait.Ticks(6);
        }
        job.Summary = $"Swung the {weapon.DisplayName} {swings} times at {tile.X},{tile.Y} - "
            + (NearestMonster(loc, tile, 5) == null ? "the monster is down." : "it's still up; swing again.");
    }

    /// <summary>The closest living monster to a tile, within <paramref name="radius"/> tiles.</summary>
    private static Monster? NearestMonster(GameLocation loc, Point tile, int radius)
    {
        Monster? best = null;
        float bestDist = radius * radius * 4f; // generous; monsters move between swings
        foreach (var m in loc.characters.OfType<Monster>())
        {
            if (m.IsInvisible || m.Health <= 0 || Math.Abs(m.Tile.X - tile.X) > radius || Math.Abs(m.Tile.Y - tile.Y) > radius)
                continue;
            float d = (m.Tile.X - tile.X) * (m.Tile.X - tile.X) + (m.Tile.Y - tile.Y) * (m.Tile.Y - tile.Y);
            if (d < bestDist)
            {
                best = m;
                bestDist = d;
            }
        }
        return best;
    }
}