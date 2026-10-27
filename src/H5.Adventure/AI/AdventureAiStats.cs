using H5.Core.Data;

namespace H5.Adventure.AI;

/// <summary>
/// AI/Adventure/Difficulty* row (exe: RPGStats+0xc08 + 0x80·difficulty, copied with the rest of
/// AI/Adventure into the global block at 0x010c5480 by FUN_00c029a0; per-player row via FUN_00c02a90).
/// </summary>
public sealed record AdventureDifficulty(
    double AttackHumanAggressivityCoef, double AttackComputerAggressivityCoef,
    double CowardiceVersusHuman, double CowardiceVersusComputer,
    int RolesSearchNearMaxCost, int RolesSearchFarMaxCost,
    double ArmyPowerCoefBonusPerHeroLevel, int ValueOfTown,
    double AvengerVersusHumanCoef, double AvengerVersusComputerCoef,
    int TownBuildPeriodInDays, bool CheckTownsInDanger, bool CheckHeroesDanger,
    bool ConsiderGarrisoningHeroToImportantTown,
    int HireHeroesBase, int HireHeroesPerTown, int HireHeroesGlobalLimit,
    int ThreatsMultiplierEnemys, int ThreatsMultiplierNeutral, int ThreatsMultiplierAllys, int ThreatsMultiplierOurs,
    int ThreatsDistanceInDays, double ThreatsTownInDangerThreshold, int ThreatsTownProtectDistanceInDays,
    int StrategicCaptureDistance, double TownCreatureGrowthCoef)
{
    /// <summary>ThreatsAccComingLimit (+0x64, int), …CalmingDivisor (+0x68, float), …Coef (+0x6c, float), read by FUN_0086cd10.</summary>
    public int ThreatsAccComingLimit { get; init; } = 5000;
    public double ThreatsAccComingCalmingDivisor { get; init; } = 1.5;
    public double ThreatsAccComingCoef { get; init; } = 0.2;
    /// <summary>GrailSearchOpenPartsMinimum (+0x48) / GrailSearchPercentMatchNeed (+0x4c), read by FUN_00bc6530.</summary>
    public int GrailSearchOpenPartsMinimum { get; init; } = 15;
    public int GrailSearchPercentMatchNeed { get; init; } = 80;
}

/// <summary>Typed view of DefaultStats.xdb AI/Adventure (exe RPGStats+0xc04; field offsets in docs/rules/AI.md).</summary>
public sealed class AdventureAiStats
{
    public required AdventureDifficulty[] Difficulty { get; init; }
    public double SearchOldTargetMultiplier { get; init; }
    public int SearchBestPositionRandomFactor { get; init; }
    public int SearchNearbyDistance { get; init; }
    public int SearchAverageTerrainCost { get; init; }
    /// <summary>SearchStrategicDistance (+0x214), read by the strategic propagation FUN_00a79700.</summary>
    public int SearchStrategicDistance { get; init; }
    public int RolesCollectorFreeScanMaxMoves { get; init; }
    public int RolesSupplierFreeScanMaxMoves { get; init; }
    /// <summary>Creature* coefficients of the stack value 004e9b90 (+0x250..+0x278, float).</summary>
    public double CreatureOverHealthCoef { get; init; }
    public double CreatureOverDamageCoef { get; init; }
    public double CreatureOverAttributesCoef { get; init; }
    public double CreatureMaeDefenceCrush { get; init; }
    public double CreatureMaeSpellReduce { get; init; }
    public double CreatureMaeMovement { get; init; }
    public double CreatureOverInitiativeCoef { get; init; }
    public double CreatureGoodLuckCoef { get; init; }
    public double CreatureBadLuckCoef { get; init; }
    public double CreatureGoodMoraleCoef { get; init; }
    public double CreatureBadMoraleCoef { get; init; }
    public double MovePointsMaxAllTypesSumCoef { get; init; }
    public int ValueOfBorderGuardKey { get; init; }
    public int ValueOfSpellSummonCreatures { get; init; }
    public int ValueOfTalismanBase { get; init; }
    public int ValueOfTalismanPerLevel { get; init; }
    public double SearchDangerEnemyMobilityMultiplier { get; init; }
    public double SearchDangerEnemyMobilityBonus { get; init; }
    /// <summary>Search{Positive,Negative}AttractorValue (+0x218/+0x21c, int), read by FUN_00bc5740 for script attractors.</summary>
    public int SearchPositiveAttractorValue { get; init; }
    public int SearchNegativeAttractorValue { get; init; }
    /// <summary>RolesSpread{Commander,Collector,Supplier,Freelancer}Multiplier (+0x22c..+0x238), read by FUN_00866670.</summary>
    public double[] RoleSpread { get; init; } = new double[4];
    public int CollectPathBonusesMaxCost { get; init; }
    public int CollectPathBonusesClipValue { get; init; }
    public int IncomeMultiplier { get; init; }
    public int ValueOfWarMachine { get; init; }
    public double TownCreaturesReserveCoef { get; init; }
    public int ValueOfSpellPerLevel { get; init; }
    public int ValueOfSkillPerMastery { get; init; }
    public int ValueOfPerkFeat { get; init; }
    public int ValueOfAttributeAttack { get; init; }
    public int ValueOfAttributeDefence { get; init; }
    public int ValueOfAttributeSpellPower { get; init; }
    public int ValueOfAttributeKnowledge { get; init; }
    public int ValueOfManaPoint { get; init; }
    public int ValueOfHundredMovePoints { get; init; }
    public int ValueOfLuckBuff { get; init; }
    public int ValueOfMoraleBuff { get; init; }
    public int ValueOfMinorArtifact { get; init; }
    public int ValueOfMajorArtifact { get; init; }
    public int ValueOfRelicArtifact { get; init; }
    public int ValueOfGrailMin { get; init; }
    public int ValueOfHeroLevelPow2 { get; init; }
    public int ValueOfHero { get; init; }
    /// <summary>SiegeShootersCoef (+0x2e0) / SiegeFlyerCoef (+0x2f4), read by the army sum 004ea5c0.</summary>
    public double SiegeShootersCoef { get; init; }
    public double SiegeFlyerCoef { get; init; }

    public static AdventureAiStats Load(GameDatabase db)
    {
        var s = db.Stats;
        const string P = "AI/Adventure/";
        int I(string n, int d) => (int)s.Get(P + n, d);
        double D(string n, double d) => s.Get(P + n, d);
        AdventureDifficulty Diff(string n)
        {
            string p = P + n + "/";
            int i(string f, int d) => (int)s.Get(p + f, d);
            double d(string f, double v) => s.Get(p + f, v);
            return new(
                d("AttackHumanAggressivityCoef", 1), d("AttackComputerAggressivityCoef", 1),
                d("CowardiceVersusHuman", 0.1), d("CowardiceVersusComputer", 0),
                i("RolesSearchNearMaxCost", 700), i("RolesSearchFarMaxCost", 32000),
                d("ArmyPowerCoefBonusPerHeroLevel", 0.1), i("ValueOfTown", 32000),
                d("AvengerVersusHumanCoef", 1), d("AvengerVersusComputerCoef", 1),
                Math.Max(1, i("TownBuildPeriodInDays", 1)), i("CheckTownsInDanger", 0) != 0, i("CheckHeroesDanger", 1) != 0,
                i("ConsiderGarrisoningHeroToImportantTown", 0) != 0,
                i("HireHeroesBase", 1), i("HireHeroesPerTown", 1), i("HireHeroesGlobalLimit", 8),
                i("ThreatsMultiplierEnemys", 0), i("ThreatsMultiplierNeutral", 1), i("ThreatsMultiplierAllys", 2), i("ThreatsMultiplierOurs", 3),
                i("ThreatsDistanceInDays", 2), d("ThreatsTownInDangerThreshold", 100000), i("ThreatsTownProtectDistanceInDays", 1),
                i("StrategicCaptureDistance", 15), d("TownCreatureGrowthCoef", 1))
            {
                ThreatsAccComingLimit = i("ThreatsAccComingLimit", 5000),
                ThreatsAccComingCalmingDivisor = d("ThreatsAccComingCalmingDivisor", 1.5),
                ThreatsAccComingCoef = d("ThreatsAccComingCoef", 0.2),
                GrailSearchOpenPartsMinimum = i("GrailSearchOpenPartsMinimum", 15),
                GrailSearchPercentMatchNeed = i("GrailSearchPercentMatchNeed", 80),
            };
        }
        return new AdventureAiStats
        {
            Difficulty = new[] { Diff("DifficultyEasy"), Diff("DifficultyNormal"), Diff("DifficultyHard"), Diff("DifficultyHeroic") },
            SearchOldTargetMultiplier = D("SearchOldTargetMultiplier", 1.5),
            SearchBestPositionRandomFactor = I("SearchBestPositionRandomFactor", 25),
            SearchNearbyDistance = I("SearchNearbyDistance", 21),
            SearchAverageTerrainCost = Math.Max(1, I("SearchAverageTerrainCost", 100)),
            SearchStrategicDistance = I("SearchStrategicDistance", 4),
            RolesCollectorFreeScanMaxMoves = I("RolesCollectorFreeScanMaxMoves", 5),
            RolesSupplierFreeScanMaxMoves = I("RolesSupplierFreeScanMaxMoves", 10),
            CreatureOverHealthCoef = D("CreatureOverHealthCoef", 0.1), CreatureOverDamageCoef = D("CreatureOverDamageCoef", 0.5),
            CreatureOverAttributesCoef = D("CreatureOverAttributesCoef", 1),
            CreatureMaeDefenceCrush = D("CreatureMAECoef_DefenceCrush", 1), CreatureMaeSpellReduce = D("CreatureMAECoef_SpellReduce", 1),
            CreatureMaeMovement = D("CreatureMAECoef_Movement", 1), CreatureOverInitiativeCoef = D("CreatureOverInitiativeCoef", 0.1),
            CreatureGoodLuckCoef = D("CreatureGoodLuckCoef", 0.0173), CreatureBadLuckCoef = D("CreatureBadLuckCoef", 0.0122),
            CreatureGoodMoraleCoef = D("CreatureGoodMoraleCoef", 0.0173), CreatureBadMoraleCoef = D("CreatureBadMoraleCoef", 0.0833),
            MovePointsMaxAllTypesSumCoef = D("MovePointsMaxAllTypesSumCoef", 1),
            ValueOfBorderGuardKey = I("ValueOfBorderGuardKey", 1500),
            ValueOfSpellSummonCreatures = I("ValueOfSpellSummonCreatures", 10000),
            ValueOfTalismanBase = I("ValueOfTalismanBase", 10000), ValueOfTalismanPerLevel = I("ValueOfTalismanPerLevel", 5000),
            SearchDangerEnemyMobilityMultiplier = D("SearchDangerEnemyMobilityMultiplier", 1),
            SearchDangerEnemyMobilityBonus = D("SearchDangerEnemyMobilityBonus", 300),
            SearchPositiveAttractorValue = I("SearchPositiveAttractorValue", 1000000),
            SearchNegativeAttractorValue = I("SearchNegativeAttractorValue", 1000000),
            RoleSpread = new[]
            {
                D("RolesSpreadCommanderMultiplier", 2.7), D("RolesSpreadCollectorMultiplier", 0.5),
                D("RolesSpreadSupplierMultiplier", 0.25), D("RolesSpreadFreelancerMultiplier", 1),
            },
            CollectPathBonusesMaxCost = I("CollectPathBonusesMaxCost", 400),
            CollectPathBonusesClipValue = I("CollectPathBonusesClipValue", 100),
            IncomeMultiplier = I("IncomeMultiplier", 50),
            ValueOfWarMachine = I("ValueOfWarMachine", 250),
            TownCreaturesReserveCoef = D("TownCreaturesReserveCoef", 0.1),
            ValueOfSpellPerLevel = I("ValueOfSpellPerLevel", 500),
            ValueOfSkillPerMastery = I("ValueOfSkillPerMastery", 1000),
            ValueOfPerkFeat = I("ValueOfPerkFeat", 1000),
            ValueOfAttributeAttack = I("ValueOfAttributeAttack", 300),
            ValueOfAttributeDefence = I("ValueOfAttributeDefence", 300),
            ValueOfAttributeSpellPower = I("ValueOfAttributeSpellPower", 300),
            ValueOfAttributeKnowledge = I("ValueOfAttributeKnowledge", 300),
            ValueOfManaPoint = I("ValueOfManaPoint", 50),
            ValueOfHundredMovePoints = I("ValueOfHundredMovePoints", 10),
            ValueOfLuckBuff = I("ValueOfLuckBuff", 100),
            ValueOfMoraleBuff = I("ValueOfMoraleBuff", 50),
            ValueOfMinorArtifact = I("ValueOfMinorArtifact", 3000),
            ValueOfMajorArtifact = I("ValueOfMajorArtifact", 9000),
            ValueOfRelicArtifact = I("ValueOfRelicArtifact", 12000),
            ValueOfGrailMin = I("ValueOfGrailMin", 100),
            ValueOfHeroLevelPow2 = I("ValueOfHeroLevelPow2", 1000),
            ValueOfHero = I("ValueOfHero", 250),
            SiegeShootersCoef = D("SiegeShootersCoef", 2), SiegeFlyerCoef = D("SiegeFlyerCoef", 2),
        };
    }

    /// <summary>Hero attribute value (exe FUN_004e9f60): Σ attribute · ValueOfAttribute*.</summary>
    public long AttributeValue(int attack, int defence, int spellPower, int knowledge) =>
        (long)attack * ValueOfAttributeAttack + (long)defence * ValueOfAttributeDefence
        + (long)spellPower * ValueOfAttributeSpellPower + (long)knowledge * ValueOfAttributeKnowledge;

    /// <summary>Artifact value by class (exe FUN_004eb840: 0 minor, 1 major, 2 relic, 3 grail).</summary>
    public int ArtifactValue(string artifactClass) => artifactClass switch
    {
        "ARTF_CLASS_MINOR" => ValueOfMinorArtifact,
        "ARTF_CLASS_MAJOR" => ValueOfMajorArtifact,
        "ARTF_CLASS_RELIC" => ValueOfRelicArtifact,
        "ARTF_CLASS_GRAIL" => ValueOfGrailMin,
        _ => 0,
    };
}

/// <summary>Hero roles used to spread heroes over targets (exe role id → FUN_00866670 multiplier).</summary>
public enum HeroRole { Commander = 1, Collector = 2, Supplier = 3, Freelancer = 4 }
