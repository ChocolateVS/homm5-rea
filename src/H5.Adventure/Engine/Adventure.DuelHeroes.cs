using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Duel heroes (H5_Game.exe). The duel is played on the adventure map Maps/DuelMode/PresetMap (the map-name code
// 0081ebc0 special-cases its folder "presetmap"); that map's objects are the 24 preset AdvMapHero records
// (Maps/DuelMode/Heroes/*.xdb, the same records UI/MPDMLobby/presets.(DuelPresets).xdb points at), so the preset heroes
// are made by the map-hero creation 009aacb0 like any map hero (talisman, the hero set-up 00b56cd0 with the worn
// artifacts' book entries and the artifact objects 00ca7e40). The preset screen 00749570 (ID_DM_CHOOSE_PRESET_SCREEN)
// is given those hero objects: 0072d430 reads their face, class (00b4d640), level (00b4c840), army, artifacts and
// book from the hero itself. User preset packs (DuelPresets/*.h5p) are mounted like the other user content (00c8bd00).
public sealed partial class Adventure
{
    /// <summary>
    /// The combat heroes of a duel between two presets: each preset's AdvMapHero record made into a hero by the map-hero
    /// creation (<see cref="HeroFromInstance"/>, 009aacb0 → 00b56cd0), then handed to combat as an adventure hero is
    /// (<see cref="ToInstance"/>). The artifact objects' draws (Rune of Flame, 00ca7e40) use an RNG seeded from
    /// <paramref name="seed"/> (the exe uses the world RNG of the preset map's load; the stream is ours). The heroes
    /// start the duel with full spell points (combat set-up), as before, and with the record's army slots.
    /// </summary>
    public static (HeroInstance A, HeroInstance B) DuelHeroes(AdventureDb db, int presetA, int presetB, ulong seed)
    {
        var adv = new Adventure(db, new AdvMap(), new AdvState { Rng = new Rng(seed ^ 0xD0E1D0E1D0E1D0E1UL) });
        HeroInstance Make(int preset, int owner)
        {
            var rec = db.Core.DuelPresets[preset].Hero;
            var h = adv.HeroFromInstance(rec, owner, new Tile(0, 0));
            var hi = adv.ToInstance(h);
            // The record's armySlots as listed: two presets carry two stacks of one creature, which the adventure
            // army merge (Army.TryAdd) would join; how 009aa5f0 copies a map hero's slots is not traced.
            hi.Army = rec.Army.ToList();
            hi.Mana = -1;
            return hi;
        }
        return (Make(presetA, 0), Make(presetB, 1));
    }
}
