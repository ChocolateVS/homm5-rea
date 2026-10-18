using System.Text.Json.Serialization;

namespace H5.Combat;

/// <summary>A battlefield square. X runs from the attacker's edge (0) to the defender's edge.</summary>
public readonly record struct Cell(int X, int Y)
{
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);
    public override string ToString() => $"({X},{Y})";
    /// <summary>Chebyshev distance (king moves).</summary>
    public int Chebyshev(Cell o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Y - o.Y));

    public static readonly Cell[] Neighbours8 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };
}

/// <summary>
/// xoshiro256** PRNG. All battle randomness goes through this so that the server
/// and every client replaying the same commands reach identical states.
/// The state is plain data and is serialised with the battle.
/// </summary>
public sealed class Rng
{
    public ulong S0, S1, S2, S3;

    public Rng() { }

    public Rng(ulong seed)
    {
        // SplitMix64 seeding, as recommended by the xoshiro authors.
        ulong x = seed;
        S0 = Mix(ref x); S1 = Mix(ref x); S2 = Mix(ref x); S3 = Mix(ref x);
    }

    static ulong Mix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextU64()
    {
        ulong result = Rotl(S1 * 5, 7) * 9;
        ulong t = S1 << 17;
        S2 ^= S0; S3 ^= S1; S1 ^= S2; S0 ^= S3;
        S2 ^= t;
        S3 = Rotl(S3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, max).</summary>
    public int Next(int max) => max <= 1 ? 0 : (int)(NextU64() % (ulong)max);

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public int Range(int min, int max) => min >= max ? min : min + Next(max - min + 1);

    /// <summary>Uniform real in [min, max].</summary>
    public double RangeD(double min, double max) => min + (max - min) * NextDouble();

    public bool Chance(double p) => p > 0 && (p >= 1 || NextDouble() < p);
}

public enum BattlePhase { Tactics, Combat, Finished }

public enum AttackKind { Melee, Ranged, Retaliation, Hero, Spell, Ability, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitKind { Creature, WarMachine, Obstacle }

/// <summary>Who is acting: a unit (Id >= 0) or a hero (encoded as -1 - side).</summary>
public static class Actor
{
    public static int Hero(int side) => -1 - side;
    public static bool IsHero(int actor) => actor < 0;
    public static int HeroSide(int actor) => -1 - actor;
}

public static class Elements
{
    public const string Fire = "ELEMENT_FIRE", Water = "ELEMENT_WATER", Air = "ELEMENT_AIR", Earth = "ELEMENT_EARTH", None = "ELEMENT_NONE";
}

public static class Towns
{
    public const string Haven = "TOWN_HEAVEN", Sylvan = "TOWN_PRESERVE", Academy = "TOWN_ACADEMY", Fortress = "TOWN_FORTRESS",
        Dungeon = "TOWN_DUNGEON", Inferno = "TOWN_INFERNO", Necropolis = "TOWN_NECROMANCY", Stronghold = "TOWN_STRONGHOLD",
        Neutral = "TOWN_NO_TYPE";

    public static bool IsGood(string t) => t is Haven or Sylvan or Academy or Fortress;
    public static bool IsEvil(string t) => t is Dungeon or Inferno or Necropolis or Stronghold;
    public static bool IsNeutral(string t) => !IsGood(t) && !IsEvil(t);

    /// <summary>Faction relation (fan manual p.314): neutrals are allies with everyone.</summary>
    public static bool Allied(string a, string b) => a == b || IsNeutral(a) || IsNeutral(b) || IsGood(a) == IsGood(b);
}
