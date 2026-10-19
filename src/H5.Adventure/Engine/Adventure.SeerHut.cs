using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>Hand the quest item over at a Seer Hut the hero stands at (UI/SeerHut "Complete Quest").</summary>
public sealed record CompleteSeerQuestCmd(int HeroId, int Hut) : AdvCommand;

/// <summary>Take on a Seer Hut's quest (UI/SeerHut "Accept" on the first visit, exe 006ef170: only in mode 1).</summary>
public sealed record AcceptSeerQuestCmd(int HeroId, int Hut) : AdvCommand;

// Seer Huts (AdvMapSeerHut <Quest>): objectives with enum-index parameters (types.xml) and an award.
public sealed partial class Adventure
{
    /// <summary>
    /// What the quest asks for, resolved from its parameters: OBTAIN_ARTIFACT [artifact], ASSEMBLE_ARMY
    /// [creature, count], COLLECT_RESOURCES [resource, amount], ADVANCE_HEROES [count, level, hero name],
    /// DEFEAT_NEUTRALS [monster object name]. MANUAL quests are driven by the map script.
    /// </summary>
    public (string Kind, string? Id, int Amount, string? Name) QuestRequirement(SeerQuest q)
    {
        var p = q.Objective.Parameters;
        int P(int i) => i < p.Count && int.TryParse(p[i], out int v) ? v : 0;
        string? Str(int i) => i < p.Count && p[i].Length > 0 ? p[i] : null;
        return q.Objective.Kind switch
        {
            "OBJECTIVE_KIND_OBTAIN_ARTIFACT" => ("artifact", Db.EnumName("ArtifactEffect", P(0)), 1, null),
            "OBJECTIVE_KIND_ASSEMBLE_ARMY" => ("army", Db.EnumName("CreatureType", P(0)), Math.Max(1, P(1)), null),
            "OBJECTIVE_KIND_COLLECT_RESOURCES" => ("resources", null, P(1), P(0).ToString()),
            "OBJECTIVE_KIND_ADVANCE_HEROES" => ("level", null, P(1), Str(2)),
            "OBJECTIVE_KIND_DEFEAT_NEUTRALS" => ("neutrals", null, 1, Str(0)),
            "OBJECTIVE_KIND_MANUAL" => ("manual", null, 0, null),
            // KILL_CREATURES has its own format (KILLARMY_FORMAT) but no shipped map uses it; parameters untraced.
            "OBJECTIVE_KIND_KILL_CREATURES" => ("kill", null, 0, null),
            _ => ("unknown", null, 0, null),
        };
    }

    /// <summary>
    /// Whether the visiting hero meets the quest. TODO(exe): the objective checks are not traced; the
    /// parameters are read as the map editor writes them.
    /// </summary>
    public bool SeerQuestMet(HeroState h, int hut)
    {
        var o = Map.Objects.ElementAtOrDefault(hut);
        if (o?.Quest is not { } q || S.Obj(hut).QuestDone) return false;
        var p = PlayerByIndex(h.Owner)!;
        var (kind, id, amount, name) = QuestRequirement(q);
        return kind switch
        {
            "artifact" => id != null && (h.Artifacts.Contains(id) || h.Backpack.Contains(id)),
            "army" => id != null && h.Army.Live().Where(st => st.Creature == id).Sum(st => st.Count) >= amount,
            "resources" => int.TryParse(name, out int r) && r is >= 0 and < 7 && p.Purse[r] >= amount,
            "level" => (name == null ? h : S.Heroes.FirstOrDefault(x => x.Alive && (x.DefId == name || x.Name == name)) ?? h).Level >= amount,
            "neutrals" => name != null && Map.Objects.Where(m => m.Kind == ObjectKind.Monster && m.Name == name).All(m => S.Obj(m.Index).Removed),
            _ => false,
        };
    }

    /// <summary>
    /// Visit (exe 005a5d90 case 0x1d, CSeerHutDialogLogEntry → 006f0ee0): quest state 1 (not taken) opens the box in
    /// mode 1 ("seerhut:offer": Accept takes the quest); state 2 (taken) opens it in mode 2 + fulfilled
    /// ("seerhut": Complete Quest + Cancel when fulfilled, else Accept closes); any other state opens no box.
    /// </summary>
    /// <summary>Reward resource the Seer Hut box shows (exe 006eefe0): the first positive of wood, ore, mercury, crystal, gems, gold — sulfur is skipped.</summary>
    public static int ShownRewardResource(int[] res)
    {
        foreach (int r in new[] { 0, 1, 2, 3, 5, 6 }) if (res[r] > 0) return r;
        return -1;
    }

    void VisitSeerHut(HeroState h, MapObject o)
    {
        var os = S.Obj(o.Index);
        if (o.Quest == null) { Emit(new ObjectVisited(h.Id, o.Index, o.Shared.Type)); return; }
        PlayerByIndex(h.Owner)?.VisitedObjects.Add(o.Index);
        Emit(new ObjectVisited(h.Id, o.Index, os.QuestDone ? "seerhut:done" : os.QuestTakenBy.Contains(h.Owner) ? "seerhut" : "seerhut:offer"));
    }

    string? DoAcceptSeerQuest(AcceptSeerQuestCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Hut);
        if (o?.Quest == null) return "not a seer hut";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the seer hut";
        var os = S.Obj(c.Hut);
        if (os.QuestDone || !os.QuestTakenBy.Add(p.Index)) return "quest already taken";
        return null;
    }

    string? DoCompleteSeerQuest(CompleteSeerQuestCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Hut);
        if (o?.Quest is not { } q) return "not a seer hut";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the seer hut";
        if (!S.Obj(c.Hut).QuestTakenBy.Contains(p.Index)) return "quest not taken";
        if (!SeerQuestMet(h, c.Hut)) return "quest not fulfilled";
        // The quest item is handed over when the quest takes the contribution.
        if (q.TakeContribution)
        {
            var (kind, id, amount, name) = QuestRequirement(q);
            switch (kind)
            {
                case "artifact" when id != null:
                    if (!h.Artifacts.Remove(id)) h.Backpack.Remove(id);
                    break;
                case "army" when id != null:
                    int left = amount;
                    for (int i = 0; i < h.Army.Length && left > 0; i++)
                    {
                        if (h.Army[i] is not { } st || st.Creature != id) continue;
                        int take = Math.Min(left, st.Count);
                        st.Count -= take;
                        left -= take;
                        if (st.Count == 0) h.Army[i] = null;
                    }
                    Emit(new ArmyChanged(h.Id, -1));
                    break;
                case "resources" when int.TryParse(name, out int r):
                    GiveOne(p, r, -amount);
                    break;
            }
        }
        S.Obj(c.Hut).QuestDone = true;
        GiveSeerAward(h, p, q);
        Emit(new ObjectVisited(h.Id, c.Hut, "seerhut:complete"));
        return null;
    }

    void GiveSeerAward(HeroState h, PlayerState p, SeerQuest q)
    {
        switch (q.AwardType)
        {
            case "AWARD_EXPERIENCE": GainExperience(h, q.Experience); break;
            case "AWARD_RESOURCE":
                for (int i = 0; i < 7; i++) if (q.Resources[i] != 0) GiveOne(p, i, q.Resources[i]);
                break;
            case "AWARD_ATTRIBUTE":
                AddStat(h, q.Attribute switch
                {
                    "HERO_ATTRIB_ATTACK" => "Attack", "HERO_ATTRIB_DEFENCE" => "Defence",
                    "HERO_ATTRIB_SPELLPOWER" => "SpellPower", _ => "Knowledge",
                }, q.AttributeAmount);
                break;
            case "AWARD_ARTIFACT" when q.Artifact != null: GiveArtifact(h, q.Artifact); break;
            case "AWARD_SPELL" when q.Spell != null: LearnSpell(h, q.Spell, force: true); break;
            case "AWARD_CREATURE" when q.Creature != null:
                if (h.Army.TryAdd(q.Creature, q.CreatureCount)) Emit(new ArmyChanged(h.Id, -1));
                break;
            case "AWARD_SPELL_POINTS": h.Mana += q.SpellPoints; break;
            case "AWARD_MORALE": h.NextBattleMorale += q.Morale; break;
            case "AWARD_LUCK": h.NextBattleLuck += q.Luck; break;
            case "AWARD_SKILL" when q.Skill != null:
                var m = q.Mastery switch { "MASTERY_EXPERT" => Mastery.Expert, "MASTERY_ADVANCED" => Mastery.Advanced, _ => Mastery.Basic };
                if (h.MasteryOf(q.Skill) < m)
                {
                    h.Skills.RemoveAll(x => x.SkillId == q.Skill);
                    h.Skills.Add(new SkillEntry(q.Skill, m));
                    Emit(new SkillLearned(h.Id, q.Skill, m));
                }
                break;
        }
    }
}
