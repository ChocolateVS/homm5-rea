namespace H5.Adventure.Engine;

/// <summary>
/// The exe's stateless "hashed" random numbers (H5_Game.exe): a seed is the adler32 (initial value 0x12345678) of the
/// argument dwords plus their byte length (008e7860 two args, 008e7930 three, 008e7a40 four → 00b62340); a draw
/// (008e7840 → 008e77e0) runs the MS LCG twice on a copy of the seed and returns ((s2 &lt;&lt; 6) ^ (s1 &gt;&gt; 7)) % (hi - lo + 1) + lo.
/// </summary>
public static class ExeHashRandom
{
    public static int Hash(params int[] args)
    {
        uint a = 0x5678, b = 0x1234;
        foreach (int v in args)
            for (int k = 0; k < 4; k++)
            {
                a = (a + (uint)((v >> (8 * k)) & 0xff)) % 65521;
                b = (b + a) % 65521;
            }
        return unchecked((int)((b << 16 | a) + (uint)(4 * args.Length)));
    }

    /// <summary>008e77e0: a value in [lo, hi] (lo when hi &lt; lo + 1).</summary>
    public static int Range(int seed, int lo, int hi)
    {
        if (hi < lo + 1) return lo;
        uint n = unchecked((uint)(hi - lo + 1));
        if (n == 0) return lo;
        int s1 = unchecked(seed * 0x343fd + 0x269ec3);
        int s2 = unchecked(s1 * 0x343fd + 0x269ec3);
        uint r = unchecked((uint)((s2 << 6) ^ (s1 >> 7)));
        return (int)(r % n) + lo;
    }
}

/// <summary>
/// CMoonCalendar (world +0x100, class at 005093d0): three "moons" (cycles at +0x1c: position 0..79, step 21..119 and
/// the phase min(pos/10, 8 - pos/10) = 0..4) index a 5×5×5 SMoonCalendar of MoonWeekID values; each new week every
/// moon advances by its step (mod 80). Saved fields (serializer 005099a0): the cycles, the current week (+0x28), the
/// calendar resource (+0x2c), the modifications (+0x34, BlockMonstersWeeks at +0x38), the astrologer candidates
/// (+0x3c) and the astrologer queue (+0x48).
/// </summary>
public sealed class MoonCalendarState
{
    public int[] Pos = new int[3];
    public int[] Step = { 21, 21, 21 };
    public int[] Phase = new int[3];
    /// <summary>+0x28: the calendar's current week (MoonWeekID).</summary>
    public int Week;
    /// <summary>+0x2c: game path of the SMoonCalendar in use; null when it failed the 5×5×5 check (005088a0).</summary>
    public string? Calendar;
    /// <summary>+0x38: map MoonCalendarModifications/BlockMonstersWeeks — creature weeks read as WEEK_OF_NOTHING.</summary>
    public bool BlockMonstersWeeks;
    /// <summary>+0x3c: weeks the Tower of Astrologer offers (00509010).</summary>
    public List<int> Candidates = new();
    /// <summary>+0x48: weeks bought at a Tower of Astrologer, applied on the next day (005091a0).</summary>
    public List<int> Queue = new();

    public MoonCalendarState Clone() => new()
    {
        Pos = (int[])Pos.Clone(), Step = (int[])Step.Clone(), Phase = (int[])Phase.Clone(), Week = Week,
        Calendar = Calendar, BlockMonstersWeeks = BlockMonstersWeeks, Candidates = new(Candidates), Queue = new(Queue),
    };
}

public static class MoonCalendar
{
    /// <summary>The default calendar the exe loads for random moons and maps without their own (string at 00e0d148).</summary>
    public const string DefaultPath = "GameMechanics/MoonCalendar/Default.xdb";

    /// <summary>
    /// 00bcd640: MoonWeekID → CreatureType of the week's double growth (0 for weeks that are not creature weeks).
    /// </summary>
    public static int CreatureOf(int week) => week switch
    {
        2 => 0x68, 4 => 0x7f, 7 => 0x7b, 0xd => 0x62, 0x13 => 0x66, 0x15 => 0x79, 0x1f => 0x81, 0x21 => 0x5e,
        0x22 => 0x60, 0x23 => 0x7d, 0x24 => 0x77, 0x25 => 0x75, 0x28 => 0x5c, 0x2e => 100,
        0x54 => 1, 0x55 => 3, 0x56 => 5, 0x57 => 7, 0x58 => 9, 0x59 => 0xb, 0x5a => 0xd,
        0x5b => 0x39, 0x5c => 0x3b, 0x5d => 0x3d, 0x5e => 0x3f, 0x5f => 0x41, 0x60 => 0x43, 0x61 => 0x45,
        0x62 => 0x2b, 99 => 0x2e, 100 => 0x2f, 0x65 => 0x31, 0x66 => 0x33, 0x67 => 0x35, 0x68 => 0x37,
        0x69 => 0x10, 0x6a => 0x11, 0x6b => 0x13, 0x6c => 0x15, 0x6d => 0x17, 0x6e => 0x19, 0x6f => 0x1b,
        0x70 => 0x48, 0x71 => 0x49, 0x72 => 0x4b, 0x73 => 0x4d, 0x74 => 0x4f, 0x75 => 0x51, 0x76 => 0x53,
        0x77 => 0x1d, 0x78 => 0x1f, 0x79 => 0x21, 0x7a => 0x23, 0x7b => 0x25, 0x7c => 0x27, 0x7d => 0x29,
        _ => 0,
    };

    /// <summary>
    /// 00bce040: a week without any effect — not a creature week, not 0x44..0x47 (00bcd950: WATER/FIRE/EARTH/AIR),
    /// not 0x4e..0x51, and in 0x2b..0x53 only IDLENESS (0x3d) and CALM (0x42) (byte table 00bce098).
    /// </summary>
    public static bool IsNeutral(int week)
    {
        if (CreatureOf(week) != 0) return false;
        if (week is >= 0x4e and <= 0x51) return false;
        if (week is >= 0x44 and <= 0x47) return false;
        if (week is >= 0x2b and <= 0x53) return week is 0x3d or 0x42;
        return true;
    }

    /// <summary>005087c0 / 00508e20: the phase 0..4 of a moon position (0..79).</summary>
    public static int PhaseOf(int pos)
    {
        int p = pos / 10;
        return 8 - p <= p ? 8 - p : p;
    }

    static int Clamp4(int v) => v < 0 ? 0 : v > 4 ? 4 : v;

    /// <summary>
    /// 005089a0 (random moons, map RandomMoons): steps — from 21..119, three picks rand(seed, 0, i) among the list,
    /// each pick removing from the list every value sharing a factor with it (the scan skips the value that slides
    /// into a removed slot, as the exe does); positions — up to five attempts of rand(seed, attempt, i) in 0..79, then
    /// 79 advances over the default calendar looking for a neutral week (00bce040) followed by a non-creature week
    /// (00bcd640); the moons are left one step back, on the neutral week. After five failed attempts the moons stay
    /// where the fifth attempt's 79 advances left them.
    /// </summary>
    public static (int Pos, int Step)[] RandomMoons(int seed, int[] defaultCalendar)
    {
        var list = new List<int>();
        for (int v = 0x15; v <= 0x77; v++) list.Add(v);
        var pos = new int[3];
        var step = new int[3];
        for (int i = 0; i < 3; i++)
        {
            int pick = ExeHashRandom.Range(ExeHashRandom.Hash(seed, 0, i), 0, list.Count - 1);
            step[i] = list[pick];
            for (int k = 0; k < list.Count; k++)
            {
                int a = step[i], b = list[k];
                while (b != 0) { int t = a % b; a = b; b = t; }
                if (a != 1) list.RemoveAt(k);   // no k-- : the exe advances past the shifted element
            }
        }
        int attempt = 0;
        while (true)
        {
            attempt++;
            for (int i = 0; i < 3; i++) pos[i] = ExeHashRandom.Range(ExeHashRandom.Hash(seed, attempt, i), 0, 0x4f);
            bool prevNeutral = false;
            for (int k = 0; k < 0x4f; k++)
            {
                for (int i = 0; i < 3; i++) pos[i] = (pos[i] + step[i]) % 0x50;
                int w = defaultCalendar[(PhaseOf(pos[0]) * 5 + PhaseOf(pos[1])) * 5 + PhaseOf(pos[2])];
                if (prevNeutral && CreatureOf(w) == 0)
                {
                    for (int i = 0; i < 3; i++) pos[i] = (pos[i] + 0x280 - step[i]) % 0x50;
                    return Zip(pos, step);
                }
                prevNeutral = IsNeutral(w);
            }
            if (attempt >= 5) return Zip(pos, step);
        }
    }

    static (int, int)[] Zip(int[] p, int[] s) => new[] { (p[0], s[0]), (p[1], s[1]), (p[2], s[2]) };

    /// <summary>005086a0: a moon the map leaves unset — one seed, position rand(0..79), step rand(21..119).</summary>
    public static (int Pos, int Step) UnsetMoon(int seed) =>
        (ExeHashRandom.Range(seed, 0, 0x4f), ExeHashRandom.Range(seed, 0x15, 0x77));

    /// <summary>
    /// 005093d0: positions clamped to 0..79, steps to 21..119 (005087c0); then the current week (00508d40) and the
    /// astrologer candidates (00509010) with <paramref name="candidateSeed"/>.
    /// </summary>
    public static MoonCalendarState Create((int Pos, int Step)[] moons, string? calendar, int[]? data, bool blockMonsterWeeks, int candidateSeed)
    {
        var s = new MoonCalendarState { Calendar = data != null ? calendar : null, BlockMonstersWeeks = blockMonsterWeeks };
        for (int i = 0; i < 3 && i < moons.Length; i++)
        {
            s.Pos[i] = Math.Clamp(moons[i].Pos, 0, 0x4f);
            s.Step[i] = Math.Clamp(moons[i].Step, 0x15, 0x77);
            s.Phase[i] = PhaseOf(s.Pos[i]);
        }
        ComputeWeek(s, data);
        FillCandidates(s, data, candidateSeed);
        return s;
    }

    static int WeekAt(MoonCalendarState s, int[]? data, int p0, int p1, int p2)
    {
        if (data == null) return 0;
        int w = data[(Clamp4(p0) * 5 + Clamp4(p1)) * 5 + Clamp4(p2)];
        return s.BlockMonstersWeeks && CreatureOf(w) != 0 ? 0 : w;
    }

    /// <summary>00508d40: +0x28 from the stored phases (0 without a valid calendar).</summary>
    public static void ComputeWeek(MoonCalendarState s, int[]? data) => s.Week = WeekAt(s, data, s.Phase[0], s.Phase[1], s.Phase[2]);

    static void AdvanceOnce(int[] pos, int[] step, int[] phase)
    {
        for (int i = 0; i < 3; i++)
        {
            pos[i] = (step[i] + pos[i]) % 0x50;
            phase[i] = PhaseOf(pos[i]);
        }
    }

    /// <summary>00508e20 (CWorld vfunc 0xb8): the next <paramref name="count"/> weeks, advancing copies of the moons.</summary>
    public static List<int> Forecast(MoonCalendarState s, int[]? data, int count)
    {
        var pos = (int[])s.Pos.Clone(); var step = (int[])s.Step.Clone(); var phase = (int[])s.Phase.Clone();
        var r = new List<int>(Math.Max(0, count));
        for (int n = 0; n < count; n++)
        {
            AdvanceOnce(pos, step, phase);
            r.Add(WeekAt(s, data, phase[0], phase[1], phase[2]));
        }
        return r;
    }

    /// <summary>
    /// 00509010: the forecast of the next 777 (0x309) weeks without the current week is the pool; three draws
    /// rand(H(seed, added, previous pick, draw number), 0, pool - 1) move a week to the candidates; a week already a
    /// candidate is drawn again, at most 7 times after each addition (the duplicate is then kept).
    /// </summary>
    public static void FillCandidates(MoonCalendarState s, int[]? data, int seed)
    {
        s.Candidates.Clear();
        var pool = Forecast(s, data, 0x309);
        pool.RemoveAll(w => w == s.Week);
        int prev = 0, draw = 0, retries = 0;
        for (int added = 0; added < 3 && pool.Count > 0; added++)
        {
            draw++;
            prev = ExeHashRandom.Range(ExeHashRandom.Hash(seed, added, prev, draw), 0, pool.Count - 1);
            int w = pool[prev];
            if (s.Candidates.Contains(w) && retries > 0) { retries--; added--; continue; }
            s.Candidates.Add(w);
            pool.RemoveAt(prev);
            retries = 7;
        }
    }

    /// <summary>
    /// 005091a0: a new week (or, with a bought week queued, the day after the purchase). Without a queue (or without a
    /// calendar) every moon advances once. With a queue one of its weeks is drawn (rand(H(seed, 0), 0, n - 1)), the
    /// queue is cleared, and the moons advance until that week comes up (at most 125 steps). Then +0x28 is recomputed
    /// and, when <paramref name="newCandidates"/>, the astrologer candidates (00509010).
    /// </summary>
    public static void Advance(MoonCalendarState s, int[]? data, int seed, bool newCandidates)
    {
        if (s.Queue.Count == 0 || data == null) AdvanceOnce(s.Pos, s.Step, s.Phase);
        else
        {
            int want = s.Queue[ExeHashRandom.Range(ExeHashRandom.Hash(seed, 0), 0, s.Queue.Count - 1)];
            s.Queue.Clear();
            int tries = 0;
            int w;
            do
            {
                AdvanceOnce(s.Pos, s.Step, s.Phase);
                w = WeekAt(s, data, s.Phase[0], s.Phase[1], s.Phase[2]);
            } while (want != w && ++tries < 0x7d);
        }
        ComputeWeek(s, data);
        if (newCandidates) FillCandidates(s, data, seed);
    }

    /// <summary>00508930: a candidate week moves to the queue (nothing when it is not a candidate).</summary>
    public static void Queue(MoonCalendarState s, int week)
    {
        int i = s.Candidates.IndexOf(week);
        if (i < 0) return;
        s.Queue.Add(week);
        s.Candidates.RemoveAt(i);
    }

    /// <summary>005088a0: Elements = 5 items of 5 items of 5 weeks; the flat [a,b,c] array or null.</summary>
    public static int[]? Parse(System.Xml.Linq.XElement? root, Func<string, int> weekIndex)
    {
        var outer = root?.Element("Elements")?.Elements("Item").ToList();
        if (outer is not { Count: 5 }) return null;
        var r = new int[125];
        for (int a = 0; a < 5; a++)
        {
            var mid = outer[a].Element("Elements")?.Elements("Item").ToList();
            if (mid is not { Count: 5 }) return null;
            for (int b = 0; b < 5; b++)
            {
                var inner = mid[b].Element("Elements")?.Elements("Item").ToList();
                if (inner is not { Count: 5 }) return null;
                for (int c = 0; c < 5; c++) r[(a * 5 + b) * 5 + c] = weekIndex(inner[c].Value.Trim());
            }
        }
        return r;
    }
}
