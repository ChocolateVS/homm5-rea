using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Answer the riddle a Sphinx posed (UI/Sphinx: the answer position 0..2 on the Riddle page and the reward
/// picked on the ChooseReward page: 0 gold, 1 artifact, 2 experience — exe 006ebe20).
/// </summary>
public sealed record AnswerSphinxCmd(int HeroId, int Sphinx, int Answer, int Reward) : AdvCommand;

/// <summary>A Sphinx poses a riddle: the question and the three answers in the order shown.</summary>
public sealed record SphinxRiddlePosed(int HeroId, int Object, string Question, string[] Answers) : AdvEvent;

// Sphinx (CAdvMapSphinx, visit 009cb070; screen 006ebfd0 / state handler 006eb640).
public sealed partial class Adventure
{
    /// <summary>
    /// The riddle a Sphinx uses: its own (RandomRiddle false) or the next one drawn from the riddle pool
    /// (CRiddlesPool 00ced780: index clamp(round(n·f), 0, n−1) with f = rand[0,100]·0.01, the pick is swapped
    /// with the last entry and dropped; an empty pool is refilled from GameMechanics/SphinxRiddles/RandomGroup).
    /// </summary>
    SphinxRiddle? DrawRiddle(MapObject o)
    {
        if (!o.SphinxRandomRiddle) return o.SphinxRiddle;
        var group = Db.RandomRiddles;
        if (group.Count == 0) return null;
        if (S.RiddlePool.Count == 0) S.RiddlePool.AddRange(Enumerable.Range(0, group.Count));
        int n = S.RiddlePool.Count;
        float f = S.Rng.Range(0, 100) * 0.01f;
        int i = Math.Clamp((int)MathF.Round(n * f, MidpointRounding.ToEven), 0, n - 1);
        int pick = S.RiddlePool[i];
        S.RiddlePool[i] = S.RiddlePool[n - 1];
        S.RiddlePool.RemoveAt(n - 1);
        return group[pick];
    }

    /// <summary>
    /// Visit (009cb070): a used Sphinx (or one without a riddle of at least three answers) shows its "refuse"
    /// message (messagesFileRef[3]); otherwise a riddle is drawn and its answers arranged — the right one
    /// (answers[0]) at position rand[0,2], the others drawn in turn from the remaining answers.
    /// </summary>
    void VisitSphinx(HeroState h, MapObject o)
    {
        var os = S.Obj(o.Index);
        PlayerByIndex(h.Owner)?.VisitedObjects.Add(o.Index);
        var riddle = os.SphinxUsed ? null : DrawRiddle(o);
        if (riddle == null || riddle.Answers.Count < 3)
        {
            Emit(new ObjectVisited(h.Id, o.Index, "sphinx:refuse"));
            return;
        }
        int correct = S.Rng.Range(0, 2);
        var rest = Enumerable.Range(1, riddle.Answers.Count - 1).ToList();
        var order = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i == correct) { order[i] = 0; continue; }
            int k = S.Rng.Range(0, rest.Count - 1);
            order[i] = rest[k];
            rest.RemoveAt(k);
        }
        os.Riddle = new PendingRiddle
        {
            Hero = h.Id, Question = riddle.Question, Answers = order.Select(a => riddle.Answers[a]).ToArray(), Correct = correct,
            Experience = riddle.Experience, Gold = riddle.Gold, Artifact = riddle.Artifact,
        };
        Emit(new SphinxRiddlePosed(h.Id, o.Index, riddle.Question, os.Riddle.Answers));
    }

    string? DoAnswerSphinx(AnswerSphinxCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Sphinx);
        if (o == null || o.Kind != ObjectKind.Sphinx) return "not a sphinx";
        var os = S.Obj(c.Sphinx);
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the sphinx";
        if (os.Riddle is not { } r || r.Hero != h.Id || os.SphinxUsed) return "no riddle posed";
        if (c.Answer is < 0 or > 2 || c.Reward is < 0 or > 2) return "bad choice";
        os.Riddle = null;
        // TODO(exe): the used flag (009cb000) is set from the screen's callback; set here for both outcomes.
        os.SphinxUsed = true;
        if (c.Answer != r.Correct)
        {
            // Wrong answer: the exe starts a battle (CFightSphinxCmd, CCombatHeroVsSphinxArenaAndOpponentsLoader);
            // its opponents are not traced, so no battle is fought (GAP in docs/rules/AUDIT_ADVENTURE.md).
            Emit(new ObjectVisited(h.Id, c.Sphinx, "sphinx:wrong"));
            return null;
        }
        switch (c.Reward)
        {
            case 0: GiveOne(p, Purse.Gold, r.Gold); break;                    // CAddPlayerMoneyCmd
            case 1: if (r.Artifact != null) GiveArtifact(h, r.Artifact); break; // CGiveArtefactCmd
            case 2: GainExperience(h, r.Experience); break;                      // CGiveHeroExperienceCmd
        }
        Emit(new ObjectVisited(h.Id, c.Sphinx, "sphinx:rewarded"));
        return null;
    }
}
