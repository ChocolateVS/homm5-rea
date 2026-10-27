using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Tower of Astrologer (BUILDING_ASTROLOGER_TOWER, building type 0x78): buy <paramref name="Week"/> (a MoonWeekID
/// value from the tower's candidates) as the week from the next day. CChangeMoonWeekCmd (vtable 0xe0edb0, execute
/// 00429bb0); <paramref name="HeroId"/> is the visiting hero, whose owner pays.
/// </summary>
public sealed record ChangeMoonWeekCmd(int HeroId, int Week) : AdvCommand;

public sealed partial class Adventure
{
    public const string AstrologerTower = "BUILDING_ASTROLOGER_TOWER";

    /// <summary>The calendar data in use (null without a valid calendar).</summary>
    int[]? MoonData => Db.MoonCalendarData(S.Moon?.Calendar);

    /// <summary>The seed of a calendar draw on the current day: H(world seed, day counter) (008e7860 at 00991926 / 00994a1b / 00994a9c).</summary>
    int MoonSeed => ExeHashRandom.Hash(S.WorldSeed, S.Day - 1);

    /// <summary>
    /// The calendar at map start (0041bdd0 → CWorld vfunc 0x30 = 009918f0 → 005093d0). Random moons (map RandomMoons):
    /// 005089a0 with H(world seed, 0) over the default calendar. Otherwise the map's moons (up to three; the rest drawn
    /// by 005086a0 with H(world seed, i)). The calendar is the map's MoonCalendar or the default one; the map's
    /// BlockMonstersWeeks turns creature weeks into WEEK_OF_NOTHING. The first week in effect is the calendar's week
    /// (00997f60 sets world +0x58), announced on the first day like every new week.
    /// </summary>
    void SetupMoonCalendar()
    {
        var defaults = Db.MoonCalendarData(MoonCalendar.DefaultPath);
        (int, int)[] moons;
        if (Map.RandomMoons)
            moons = defaults != null ? MoonCalendar.RandomMoons(ExeHashRandom.Hash(S.WorldSeed, 0), defaults) : new (int, int)[3];
        else
        {
            moons = new (int, int)[3];
            int n = Math.Min(3, Map.Moons.Count);
            for (int i = 0; i < n; i++) moons[i] = Map.Moons[i];
            for (int i = n; i < 3; i++) moons[i] = MoonCalendar.UnsetMoon(ExeHashRandom.Hash(S.WorldSeed, i));
        }
        string path = Map.MoonCalendar ?? MoonCalendar.DefaultPath;
        S.Moon = MoonCalendar.Create(moons, path, Db.MoonCalendarData(path), Map.BlockMonstersWeeks, MoonSeed);
        S.MoonWeek = Db.MoonWeekId(S.Moon.Week);
    }

    /// <summary>
    /// The calendar part of a new day (009949d0, after the day counter moved): a bought week first (world +0x1a4:
    /// 005091a0 without new candidates; world +0x58 follows; on days 2..7 the notice day +0x1dc becomes today so the
    /// start-of-week notice shows again), then on the first day of a week the regular advance (005091a0 with new
    /// candidates), the buyers' list is cleared and world +0x58 follows (00993cd0 → 00993bf0).
    /// Returns true when the week in effect is announced today.
    /// </summary>
    bool MoonNewDay(bool newWeek, out bool boughtMidWeek)
    {
        S.WeekNoticeDay = 1;
        bool announce = false;
        boughtMidWeek = false;
        if (S.AstrologerChanged && S.Moon != null)
        {
            S.AstrologerChanged = false;
            MoonCalendar.Advance(S.Moon, MoonData, MoonSeed, newCandidates: false);
            S.MoonWeek = Db.MoonWeekId(S.Moon.Week);
            S.WeekNoticeDay = S.DayOfWeek;
            announce = boughtMidWeek = S.DayOfWeek != 1;
        }
        if (newWeek)
        {
            if (S.Moon != null)
            {
                MoonCalendar.Advance(S.Moon, MoonData, MoonSeed, newCandidates: true);
                S.AstrologerPlayers.Clear();
                S.MoonWeek = Db.MoonWeekId(S.Moon.Week);
            }
            announce = true;
        }
        return announce;
    }

    /// <summary>The weeks the Tower of Astrologer offers (CWorld vfunc 0xbc = 0098fa90; empty without a calendar).</summary>
    public IReadOnlyList<int> AstrologerCandidates => S.Moon?.Candidates ?? (IReadOnlyList<int>)Array.Empty<int>();

    /// <summary>The next <paramref name="count"/> weeks as the calendar runs (CWorld vfunc 0xb8 = 0098cef0 → 00508e20).</summary>
    public List<int> MoonForecast(int count) => S.Moon != null ? MoonCalendar.Forecast(S.Moon, MoonData, count) : new List<int>();

    /// <summary>The current week (CWorld vfunc 0xb0 = world +0x58) as a MoonWeekID value.</summary>
    public int CurrentMoonWeekIndex => Db.MoonWeekIndex(S.MoonWeek);

    /// <summary>
    /// CWorld vfunc 0xc0 = 0098cf10: 2 when the player already bought a week this week (ASTROLOGER_CANT_CHANGE_TWICE_ON_WEEK),
    /// 1 on the seventh day of the week (day counter % 7 == 6, _ON_SUNDAY), 3 without a calendar, else 0 when the week is
    /// a candidate and 3 when not.
    /// </summary>
    public int AstrologerStatus(int week, int player)
    {
        if (S.AstrologerPlayers.Contains(player)) return 2;
        if ((S.Day - 1) % 7 == 6) return 1;
        if (S.Moon == null) return 3;
        return S.Moon.Candidates.Contains(week) ? 0 : 3;
    }

    /// <summary>CWorld vfunc 0xc4 = 0098c5a0: RPGStats adventure/AstrologerCallWeekCost (stock 6000 gold, MMH 5.5 2500).</summary>
    public Resources AstrologerCost => Resources.Parse(Db.Stats.Node("adventure/AstrologerCallWeekCost"));

    /// <summary>
    /// 00429bb0: status 0, the player affords the cost (009d0720: every resource), pays it (player vfunc 0xe0), then
    /// CWorld vfunc 0xc8 = 0098fae0: (once per player and week) the week moves from the candidates to the queue
    /// (00508930), the player is listed (+0x198) and +0x1a4 is set.
    /// </summary>
    string? DoChangeMoonWeek(ChangeMoonWeekCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != Current.Index) return "not your hero";
        var p = Current;
        if (AstrologerStatus(c.Week, p.Index) != 0) return "the week cannot be changed";
        var cost = AstrologerCost;
        if (!p.Purse.CanAfford(cost)) return "not enough gold";
        p.Purse.Add(cost, -1);
        Emit(new ResourcesChanged(p.Index, new Purse(Purse.Scale(cost, -1)).R, (int[])p.Purse.R.Clone()));
        if (S.AstrologerPlayers.Contains(p.Index) || S.Moon == null) return null;
        MoonCalendar.Queue(S.Moon, c.Week);
        S.AstrologerPlayers.Add(p.Index);
        S.AstrologerChanged = true;
        return null;
    }
}
