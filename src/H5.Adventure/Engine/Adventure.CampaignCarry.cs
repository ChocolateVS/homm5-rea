using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// A hero carried from a won campaign mission to a later one (HeroesPool), as H5_Game.exe keeps it.
/// 0045edc0 fills the record from the hero: first vf 0x98 / vf 0x94 take every worn and carried artifact off
/// the hero (vf 0x90) and hand it back to the world's artifact generator (world vf 0x38 = +0xf8, vf 0x14), so
/// no artifact is carried; then Experience (+0, hero vf 0x1a4 = +0x60) with its "set" flag (+4 = 1), an
/// OverrideMask (+8 = 0xec) and an SAdvMapHeroEditable (+0xc, layout of reader 00a08b50): Offence / Defence /
/// Spellpower / Knowledge from vf 0x10..0x1c, skills with mastery (vf 0x17c / vf 0x174), perks (vf 0x188),
/// spellIDs (vf 0xd8 = hero +0x2f4), TalismanLevel (vf 0x300 = +0x230). The army is not stored (vf 0x250 is
/// the hero's AdvMapHeroShared, the key of the record), nor the name, war machines or favoured enemies.
/// At the next mission the map hero with the same shared hero is made with it (0041c4b0 → 009aaed0 →
/// 009aacb0): its Editable is combined with the record's under mask 0xec by SFLB_CombineEditablesStep
/// 009a9c40 / 009a9cf0 (bit 0x20 attributes, 0x08 skills and perks, 0x40 spells and talisman level taken from
/// the record; 0x01 name, 0x02 biography, 0x10 war machines, 0x100 favoured enemies stay the map's), the
/// experience is the record's (flag +4), the level follows it (009aa5f0, 00b4c6b0 table) and the attributes are
/// clamped (Offence / Defence ≥ 0, Spellpower / Knowledge ≥ 1).
/// </summary>
public sealed class CampaignHeroRecord
{
    /// <summary>The hero's shared definition (the record's key, vf 0x250).</summary>
    public string ScriptName = "";
    public long Experience;
    public int Attack, Defence, SpellPower, Knowledge;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new(), Spells = new();
    public int TalismanLevel;

    public static CampaignHeroRecord From(HeroState h) => new()
    {
        ScriptName = h.DefId,
        Experience = h.Experience,
        Attack = h.Attack, Defence = h.Defence, SpellPower = h.SpellPower, Knowledge = h.Knowledge,
        Skills = h.Skills.ToList(),
        Perks = h.Perks.ToList(),
        Spells = h.Spells.ToList(),
        TalismanLevel = h.TalismanLevel,
    };

    /// <summary>
    /// The heroes 0045f460 stores besides the HeroesPool entries, into the won mission's own record (+0x18; 0045f080
    /// hands record[mission − 1] +0x18 to the next mission of the campaign): extra = Count − entries + entries with an
    /// empty HeroScriptName, at most the player's hero count; the player's heroes (player vf 0xc0) are merge-sorted
    /// by experience (00460e80, comparator 0045fbc0: vf 0x1a4, ascending) and taken from the end, the most
    /// experienced first. Named heroes are not excluded.
    /// </summary>
    public static List<HeroState> ExtraHeroes(IReadOnlyList<HeroState> playerHeroes, int count, int entries, int emptyEntries)
    {
        int extra = Math.Min(count - entries + emptyEntries, playerHeroes.Count);
        if (extra <= 0) return new();
        // 0045fbc0 takes the left element only when its experience is strictly below the right one's: equal
        // experience puts the later hero first, so the order of equals is reversed.
        var sorted = playerHeroes.Select((h, i) => (h, i)).OrderBy(x => x.h.Experience).ThenByDescending(x => x.i).Select(x => x.h).ToList();
        var result = new List<HeroState>();
        for (int k = 0; k < extra; k++) result.Add(sorted[sorted.Count - 1 - k]);
        return result;
    }

    /// <summary>The bucket counts of 00cef2a0 (table 00e0c020, the first eight entries read; more than 1543 extras cannot occur).</summary>
    static readonly uint[] Primes = { 13, 29, 53, 97, 193, 389, 769, 1543 };

    /// <summary>Hash of the key (00421a30): the shared hero's InternalName (+0xec), h = h · 5 + c over signed chars.</summary>
    public static uint KeyHash(string internalName)
    {
        uint h = 0;
        foreach (var b in System.Text.Encoding.Latin1.GetBytes(internalName)) h = unchecked(h * 5 + (uint)(sbyte)b);
        return h;
    }

    /// <summary>
    /// The order 0041cc60 reads the extras in: the record's hash map keyed by hero (0045f460 inserts them most
    /// experienced first through 00461950 → 00461520: resize 00460a50 to 00cef2a0(n + 1) buckets when needed, which
    /// re-links each bucket's nodes in order to the head of their new bucket; a new key goes to the head of bucket
    /// hash % buckets; 13 buckets at first, 006fa080); iteration (00c44170 / 00422770) walks the buckets in order and
    /// each chain from its head. A key already present is not inserted again.
    /// </summary>
    public static List<CampaignHeroRecord> InMapOrder(IReadOnlyList<CampaignHeroRecord> inserted)
    {
        var buckets = new List<List<CampaignHeroRecord>>();
        for (int i = 0; i < Primes[0]; i++) buckets.Add(new());
        int n = 0;
        foreach (var r in inserted)
        {
            if (buckets.Any(b => b.Any(x => x.ScriptName == r.ScriptName))) continue;
            if (buckets.Count < n + 1)
            {
                uint size = Primes.FirstOrDefault(p => p >= n + 1, Primes[^1]);
                var grown = new List<List<CampaignHeroRecord>>();
                for (int i = 0; i < size; i++) grown.Add(new());
                foreach (var b in buckets)
                    foreach (var x in b) grown[(int)(KeyHash(x.ScriptName) % size)].Insert(0, x);
                buckets = grown;
            }
            buckets[(int)(KeyHash(r.ScriptName) % (uint)buckets.Count)].Insert(0, r);
            n++;
        }
        return buckets.SelectMany(b => b).ToList();
    }
}

public sealed partial class Adventure
{
    List<CampaignHeroRecord> _campaignExtras = new();
    int _campaignExtraNext;

    /// <summary>
    /// 0041cc60: with the campaign flag (setup model vf 0x54) and the counter below the start info's extras count
    /// (+0x18 map, count +0x28), the counter-th entry in map order is taken and the counter advances — even when its
    /// hero (the key's AdvMapHeroShared) does not resolve, which then makes no hero; else 009aafa0 makes the hero from
    /// its shared hero with the record (as for named carry-over) at the placeholder's place for the placeholder's owner.
    /// </summary>
    HeroState? NextCampaignExtra(int owner, Tile pos)
    {
        if (_campaignExtraNext >= _campaignExtras.Count) return null;
        var r = _campaignExtras[_campaignExtraNext++];
        if (!Core.Heroes.TryGetValue(r.ScriptName, out var def)) return null;
        var h = HeroFromDef(def, owner, pos);
        ApplyCampaignRecord(h, r);
        return h;
    }

    /// <summary>
    /// Gives a placed hero its carried record (009aacb0 with the record of 0045f080): experience and level, the
    /// attributes, skills, perks, spells and talisman level replace the map's; artifacts, army, war machines, name
    /// and favoured enemies stay as the map placed them. The book, the talisman spells (vf 0x384) and the start
    /// mana / movement follow the new values.
    /// </summary>
    public void ApplyCampaignRecord(HeroState h, CampaignHeroRecord r)
    {
        h.Experience = r.Experience;
        h.Level = Core.Experience.LevelFor(r.Experience);
        h.Attack = Math.Max(0, r.Attack);
        h.Defence = Math.Max(0, r.Defence);
        h.SpellPower = Math.Max(1, r.SpellPower);
        h.Knowledge = Math.Max(1, r.Knowledge);
        h.Skills = r.Skills.ToList();
        h.Perks = r.Perks.ToList();
        h.Spells = r.Spells.ToList();
        foreach (var s in h.SpellSources.Keys.Where(s => !h.Spells.Contains(s)).ToList()) h.SpellSources.Remove(s);
        h.TalismanLevel = r.TalismanLevel;
        SetUpHeroBook(h);
        GrantTalismanSpells(h);
        h.Mana = MaxMana(h);
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints;
    }
}
