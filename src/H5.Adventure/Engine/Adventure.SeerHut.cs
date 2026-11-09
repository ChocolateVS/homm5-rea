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
            "OBJECTIVE_KIND_COLLECT_RESOURCES" => ("resources", null, 1 < p.Count && int.TryParse(p[1], out int amt) ? amt : 1, P(0).ToString()),   // 00b44ab0: amount default 1
            "OBJECTIVE_KIND_ADVANCE_HEROES" => ("level", null, P(1), Str(2)),
            "OBJECTIVE_KIND_DEFEAT_NEUTRALS" => ("neutrals", null, 1, Str(0)),
            "OBJECTIVE_KIND_MANUAL" => ("manual", null, 0, null),
            // KILL_CREATURES (ctor 00b455a0): parameter 0 the creature (+0x30, 0 when FUN_0089fbf0 finds no such
            // creature), 1 the count (+0x34, default 0).
            "OBJECTIVE_KIND_KILL_CREATURES" => ("kill", P(0) != 0 ? Db.EnumName("CreatureType", P(0)) : null, P(1), null),
            _ => ("unknown", null, 0, null),
        };
    }

    /// <summary>
    /// ASSEMBLE_ARMY's requirement as its ctor 00b44950 builds it: the parameters in (creature, count) pairs (stride
    /// 0x18 = two strings), each read with sscanf "%i"; a pair counts when the creature is 0 ≤ id &lt; FUN_00acb380() (180)
    /// and the count ≥ 0, map[creature] = count (a later pair of the same creature replaces the earlier one).
    /// </summary>
    public Dictionary<string, int> ArmyRequirement(SeerQuest q)
    {
        var p = q.Objective.Parameters;
        var need = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < p.Count; i += 2)
            if (int.TryParse(p[i], out int c) && int.TryParse(p[i + 1], out int n) && c is >= 0 and < 180 && n >= 0 &&
                Db.EnumName("CreatureType", c) is { } id)
                need[id] = n;
        return need;
    }

    /// <summary>
    /// Whether the quest is met, per the objective conditions of H5_Game.exe (factory 00b45b70 by ObjectiveKind; each
    /// condition's check is vtable +0x10):
    /// <list type="bullet">
    /// <item>COLLECT_RESOURCES (ctor 00b44ab0, check 00b41850): parameter 0 the resource (+0x38, default 0), 1 the
    /// amount (+0x3c, default 1); the player's resources (vf 0xd8) of that index ≥ the amount.</item>
    /// <item>ADVANCE_HEROES (ctor 00b45420, check 00b42690 → 00b425e0): parameter 0 the number of heroes (+0x30, at
    /// least 1; 0 without the parameter), 1 the level (+0x34, 0..40, default 1), 2 a hero name (+0x38); met when that
    /// many of the player's heroes (vf 0xc0; with a name only that hero) have reached the level.</item>
    /// <item>DEFEAT_NEUTRALS (ctor 00b44e40 → 00496160, check 00b41fd0 → 00b41f50): every parameter names a monster;
    /// met when all of them are defeated (an object that is gone counts).</item>
    /// <item>OBTAIN_ARTIFACT (ctor 00b45210, check 00b422c0) and ASSEMBLE_ARMY (ctor 00b44950, check 00b43d30 →
    /// 00b43c60) look at the player's heroes (vf 0xc0), limited to one hero when the caller passes it; which hero the
    /// Seer Hut passes is not traced (TODO(exe)), here the visiting hero. The artifact counts worn (00ca6680) or in the
    /// backpack (00ca6b30); with a location (parameter 1, +0x3c) that names a map region, only while the holder's position —
    /// the town's when he is in one (world vf 0x100 → vf 0xa4), else his own (vf 0x54 → vf 0xc4) — is inside it (region
    /// vf 0xc); a location that names no region is ignored (local_c null: met). The army condition takes
    /// every (creature, count) pair (<see cref="ArmyRequirement"/>) and needs a creature total above their sum.</item>
    /// </list>
    /// What the box reads (004f25f0) is the player's quest objective in state 3 (completed) or 4 (failed): a quest failed
    /// by a ban (<see cref="SeerQuestFailed"/>) counts as fulfilled, and Complete then ends it without the award.
    /// </summary>
    public bool SeerQuestMet(HeroState h, int hut)
    {
        var o = Map.Objects.ElementAtOrDefault(hut);
        var os = S.Obj(hut);
        if (o?.Quest is not { } q || os.QuestDoneBy.Contains(h.Owner)) return false;
        if (os.QuestTakenBy.Contains(h.Owner) && SeerQuestFailed(h.Owner, hut)) return true;
        var p = PlayerByIndex(h.Owner)!;
        var (kind, id, amount, name) = QuestRequirement(q);
        var prm = q.Objective.Parameters;
        int P(int i, int dflt) => i < prm.Count && int.TryParse(prm[i], out int v) ? v : dflt;
        switch (kind)
        {
            case "artifact":
            {
                if (id == null || !(h.Artifacts.Contains(id) || h.Backpack.Contains(id))) return false;
                if (prm.Count < 2 || prm[1].Length == 0 || !Map.Regions.TryGetValue(prm[1], out var region)) return true;
                var at = h.InTown >= 0 && Map.Objects.ElementAtOrDefault(h.InTown) is { } town ? town.Pos : h.Pos;
                return region.Contains(at);
            }
            case "army":
            {
                // 00b43c60: every required creature present in at least its count (00b43940 / 00b42cb0) and the hero's
                // creature total (0045a360) greater than the summed requirement (00b42dd0): a hero never hands over his
                // whole army.
                var need = ArmyRequirement(q);
                return need.All(kv => h.Army.Live().Where(st => st.Creature == kv.Key).Sum(st => st.Count) >= kv.Value) &&
                       h.Army.Live().Sum(st => st.Count) > need.Values.Sum();
            }
            case "resources":
            {
                int r = P(0, 0);
                return r is >= 0 and < 7 && p.Purse[r] >= P(1, 1);
            }
            case "level":
            {
                int need = prm.Count > 0 ? Math.Max(1, P(0, 0)) : 0;
                int level = prm.Count > 1 ? Math.Clamp(P(1, 0), 0, 40) : 1;
                int have = S.Heroes.Count(x => x.Alive && x.Owner == h.Owner && (name == null || x.DefId == name || x.Name == name) && x.Level >= level);
                return have >= need;
            }
            case "neutrals":
                return prm.Where(n => n.Length > 0).All(n =>
                    Map.Objects.Where(m => m.Kind == ObjectKind.Monster && m.Name == n).All(m => S.Obj(m.Index).Removed));
            case "kill":
                // 00b42840: creature set and KilledCount − baseline (+0x38) ≥ the count.
                return id != null && KilledCount(h.Owner, id) - S.Obj(hut).QuestKillBaseline.GetValueOrDefault(h.Owner) >= amount;
            default: return false;
        }
    }

    /// <summary>
    /// FUN_00b412f0(player, creature): over the player's heroes (vf 0xc0) the hero kill counter (vf 0x60 = hero +0x1e0,
    /// get 00a84480) of exactly that creature, summed.
    /// </summary>
    int KilledCount(int player, string creature) =>
        S.Heroes.Where(x => x.Alive && x.Owner == player).Sum(x => x.Kills.GetValueOrDefault(creature));

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
        Emit(new ObjectVisited(h.Id, o.Index, os.QuestDoneBy.Contains(h.Owner) ? "seerhut:done" : os.QuestTakenBy.Contains(h.Owner) ? "seerhut" : "seerhut:offer"));
    }

    /// <summary>
    /// The player's taken quest failed: the bannable condition (00afb060) answers "decided, not met" when the common
    /// objective's recorded player (+0x16c, 00afb170) is another player — at the take (004f2560 checks with flags 3) or when
    /// the listener 00afb410 re-checks the players still active (flags 1). State 4 is final.
    /// </summary>
    public bool SeerQuestFailed(int player, int hut) => S.Obj(hut).QuestFailedBy.Contains(player);

    /// <summary>
    /// The common objective's state listener (00afb410) for a player's quest objective entering state 2 (taken) with
    /// AllowMultipleActivations off, or state 3 (completed) with AllowMultipleCompletions off: the player is recorded (+0x16c)
    /// and every other player whose objective is still active (state 2) is checked again (vf 0x60(…, 1, 0) → 00c603f0),
    /// which the ban fails (state 4, no award).
    /// UNVERIFIED: here "still active" is taken but neither completed at the hut nor failed; the exe's objective may already
    /// be completed (state 3) when its condition was met at an earlier check, and then it is spared.
    /// </summary>
    void SeerQuestStateChanged(int hut, int player, bool completed)
    {
        var o = Map.Objects[hut];
        var os = S.Obj(hut);
        if (completed ? o.Quest!.AllowMultipleCompletions : o.Quest!.AllowMultipleActivations) return;
        os.QuestBanPlayer = player;
        foreach (int other in os.QuestTakenBy)
            if (other != player && !os.QuestDoneBy.Contains(other)) os.QuestFailedBy.Add(other);
    }

    string? DoAcceptSeerQuest(AcceptSeerQuestCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Hut);
        if (o?.Quest == null) return "not a seer hut";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the seer hut";
        var os = S.Obj(c.Hut);
        if (os.QuestDoneBy.Contains(p.Index) || !os.QuestTakenBy.Add(p.Index)) return "quest already taken";
        // 004f2560: the offer and the quest objectives enter state 2 (the listener 00afb410 runs), then the quest is checked
        // (vf 0x60 flags 3): a ban by another recorded player fails it at once.
        SeerQuestStateChanged(c.Hut, p.Index, completed: false);
        if (os.QuestBanPlayer >= 0 && os.QuestBanPlayer != p.Index) os.QuestFailedBy.Add(p.Index);
        // The KILL_CREATURES baseline (condition vf 8 = 00b427f0); UNVERIFIED: that it runs when the quest is taken.
        if (QuestRequirement(o.Quest) is ("kill", { } cr, _, _)) os.QuestKillBaseline[p.Index] = KilledCount(p.Index, cr);
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
        var os = S.Obj(c.Hut);
        if (os.QuestDoneBy.Contains(p.Index)) return "quest already completed";
        if (!SeerQuestMet(h, c.Hut)) return "quest not fulfilled";
        // 004f26b0: the offer objective takes the quest objective's state; a failed quest (4) ends with no award.
        if (os.QuestFailedBy.Contains(p.Index))
        {
            os.QuestDoneBy.Add(p.Index);
            Emit(new ObjectVisited(h.Id, c.Hut, "seerhut:failed"));
            return null;
        }
        // The quest item is handed over when the quest takes the contribution.
        if (q.TakeContribution)
        {
            var (kind, id, amount, name) = QuestRequirement(q);
            switch (kind)
            {
                case "artifact" when id != null:
                    if (h.Artifacts.Remove(id)) TakeInstance(h, id, true);
                    else if (h.Backpack.Remove(id)) TakeInstance(h, id, false);
                    break;
                case "army":
                    foreach (var (cr, n) in ArmyRequirement(q))
                    {
                        int left = n;
                        for (int i = 0; i < h.Army.Length && left > 0; i++)
                        {
                            if (h.Army[i] is not { } st || st.Creature != cr) continue;
                            int take = Math.Min(left, st.Count);
                            st.Count -= take;
                            left -= take;
                            if (st.Count == 0) h.Army[i] = null;
                        }
                    }
                    Emit(new ArmyChanged(h.Id, -1));
                    break;
                case "resources" when int.TryParse(name, out int r):
                    GiveOne(p, r, -amount);
                    break;
            }
        }
        os.QuestDoneBy.Add(p.Index);
        // TODO(exe): the exe's quest objective completes (state 3) at the check that first finds it met, which is when the
        // listener runs for AllowMultipleCompletions off; here at the hand-in.
        SeerQuestStateChanged(c.Hut, p.Index, completed: true);
        GiveSeerAward(h, p, q);
        Emit(new ObjectVisited(h.Id, c.Hut, "seerhut:complete"));
        return null;
    }

    /// <summary>
    /// The award (CObjectiveAwardEffect apply 00a7e2c0, switch on the award type +0x28): 1 experience through the hero's
    /// modifiers (vf 0x19c → vf 0x198); 2 resources to the player (+0x30, vf 0xd8 / 0xdc); 3 attribute (vf 0x1bc(+0x50,
    /// +0x54)); 4 artifact (+0x58, vf 0x80 / 0x84, TT_ARTIFACT_FOUND); 5 spell (+0x5c, vf 0xe8(spell, 0)); 6 creatures
    /// (+0x64 × +0x68 into the army, vf 0x378); 10 skill (+0x80) raised one level at a time from the hero's mastery
    /// (vf 0x174) up to +0x7c (vf 0x1cc / 0x1c8, NEW_SKILL / NEW_ABILITY). AWARD_SPELL_POINTS, AWARD_MORALE and
    /// AWARD_LUCK (7, 8, 9) have no case: nothing is given (the box shows them as UNKNOWN_FORMAT too, 006efb10).
    /// TODO(exe): vf 0x1cc (the per-level refusal) and the hero vf 0x80 artifact test are not decoded; MASTERY_EXTRA_EXPERT
    /// is capped at Expert here.
    /// </summary>
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
            case "AWARD_SKILL" when q.Skill != null:
            {
                // 00a7e2c0 case 10: steps = max(0, award mastery (+0x7c) − current (vf 0x174)); each step stops when
                // vf 0x1cc = 00b4f760 refuses: 1 a perk id (record +0x48), 2 a new skill with no free slot (vf 0x1b8 =
                // 00b4d380: 5 − the skills that are neither +0x48 nor +0x4c), 3 already Expert (> 2), so
                // MASTERY_EXTRA_EXPERT ends at Expert; else vf 0x1c8 raises one level.
                int target = q.Mastery switch { "MASTERY_EXTRA_EXPERT" => 4, "MASTERY_EXPERT" => 3, "MASTERY_ADVANCED" => 2, "MASTERY_BASIC" => 1, _ => 0 };
                bool perk = Db.Perks.TryGetValue(q.Skill, out var sd) && !sd.IsSkill;
                for (int step = Math.Max(0, target - (int)h.MasteryOf(q.Skill)); step > 0 && !perk; step--)
                {
                    var cur = h.MasteryOf(q.Skill);
                    if (cur > Mastery.Advanced || (cur == Mastery.None && KnownSkills(h).Count >= MaxSkills)) break;
                    var next = cur + 1;
                    int at = h.Skills.FindIndex(x => x.SkillId == q.Skill);
                    if (at >= 0) h.Skills[at] = new SkillEntry(q.Skill, next);
                    else h.Skills.Add(new SkillEntry(q.Skill, next));
                    Emit(new SkillLearned(h.Id, q.Skill, next));
                }
                break;
            }
        }
    }
}
