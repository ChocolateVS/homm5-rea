namespace H5.Combat;

/// <summary>Ability ids used by rules code (subset of GameMechanics/RefTables/CombatAbilities.xdb).</summary>
public static class Ab
{
    public const string
        NoMeleePenalty = "ABILITY_NO_MELEE_PENALTY", NoRangePenalty = "ABILITY_NO_RANGE_PENALTY", RangePenalty = "ABILITY_RANGE_PENALTY",
        NoEnemyRetaliation = "ABILITY_NO_ENEMY_RETALIATION", UnlimitedRetaliation = "ABILITY_UNLIMITED_RETALIATION",
        LargeShield = "ABILITY_LARGE_SHIELD", ShieldOther = "ABILITY_SHIELD_OTHER", Undead = "ABILITY_UNDEAD", Elemental = "ABILITY_ELEMENTAL",
        Mechanical = "ABILITY_MECHANICAL", Demonic = "ABILITY_DEMONIC", Bravery = "ABILITY_BRAVERY", Armored = "ABILITY_ARMORED",
        Enraged = "ABILITY_ENRAGED", Incorporeal = "ABILITY_INCORPOREAL", DoubleAttack = "ABILITY_DOUBLE_ATTACK",
        DoubleShot = "ABILITY_ELVES_DOUBLE_SHOT", PreciseShot = "ABILITY_PRECISE_SHOT", Jousting = "ABILITY_JOUSTING",
        LifeDrain = "ABILITY_LIFE_DRAIN", Bash = "ABILITY_BASH", Fear = "ABILITY_FEAR", FrightAura = "ABILITY_FRIGHT_AURA",
        BlindingAttack = "ABILITY_BLINDING_ATTACK", Poison = "ABILITY_POISONOUS_ATTACK", Venom = "ABILITY_VENOM",
        StrikeAndReturn = "ABILITY_STRIKE_AND_RETURN", Regeneration = "ABILITY_REGENERATION", ThreeHeaded = "ABILITY_THREE_HEADED_ATTACK",
        SixHeaded = "ABILITY_SIX_HEADED_ATTACK", Spray = "ABILITY_SPRAY_ATTACK", FireBreath = "ABILITY_FIRE_BREATH", Cleave = "ABILITY_CLEAVE",
        Vorpal = "ABILITY_VORPAL_SWORD", WeakeningStrike = "ABILITY_WEAKENING_STRIKE", CursingAttack = "ABILITY_CURSING_ATTACK",
        HexingAttack = "ABILITY_HEXING_ATTACK", WhipStrike = "ABILITY_WHIP_STRIKE", Wound = "ABILITY_WOUND", MarkOfFire = "ABILITY_MARK_OF_FIRE",
        FierceRetaliation = "ABILITY_FIERCE_RETALIATION", BattleFrenzy = "ABILITY_BATTLE_FRENZY", BattleRage = "ABILITY_BATTLE_RAGE",
        AntiGiant = "ABILITY_ANTI_GIANT", RiderCharge = "ABILITY_RIDER_CHARGE", ShieldWall = "ABILITY_SHIELD_WALL", Agility = "ABILITY_AGILITY",
        PreparedPosition = "ABILITY_PREPARED_POSITION", HoldGround = "ABILITY_HOLD_GROUND", MagmaShield = "ABILITY_MAGMA_SHIELD",
        FireShield = "ABILITY_FIRE_SHIELD", AuraMagicResistance = "ABILITY_AURA_OF_MAGIC_RESISTANCE", AuraBravery = "ABILITY_AURA_OF_BRAVERY",
        PresenceOfCommander = "ABILITY_PRESENCE_OF_COMMANDER", WeakeningAura = "ABILITY_WEAKENING_AURA", SearingAura = "ABILITY_SEARING_AURA",
        ManaStealer = "ABILITY_MANA_STEALER", ManaDestroyer = "ABILITY_MANA_DESTROYER", EnergyChannel = "ABILITY_ENERGY_CHANNEL",
        EntanglingRoots = "ABILITY_ENTANGLING_ROOTS", TakeRoots = "ABILITY_TAKE_ROOTS", DeathCloud = "ABILITY_DEATH_CLOUD",
        MagicAttack = "ABILITY_MAGIC_ATTACK", ChainShot = "ABILITY_CHAIN_SHOT", ScatterShot = "ABILITY_SCATTER_SHOT",
        RangedRetaliate = "ABILITY_RANGED_RETALIATE", AxeOfSlaughter = "ABILITY_AXE_OF_SLAUGHTER", ChampionCharge = "ABILITY_CHAMPION_CHARGE",
        AcidBreath = "ABILITY_ACID_BREATH", RainbowBreath = "ABILITY_RAINBOW_BREATH", LightningBreath = "ABILITY_LIGHTNING_BREATH",
        CrushingBlow = "ABILITY_CRUSHING_BLOW", WarDance = "ABILITY_WAR_DANCE", Whirlwind = "ABILITY_WHIRLWIND", CrossAttack = "ABILITY_CROSS_ATTACK",
        LuckGambler = "ABILITY_LUCK_GAMBLER", SorrowStrike = "ABILITY_SORROW_STRIKE", EvilEye = "ABILITY_EVIL_EYE", SleepingStrike = "ABILITY_SLEEPING_STRIKE",
        StormStrike = "ABILITY_STORMSTRIKE", AcidBlood = "ABILITY_ACID_BLOOD", Incinerate = "ABILITY_INCINERATE", CallLightning = "ABILITY_CALL_LIGHTNING",
        LayHands = "ABILITY_LAY_HANDS", ResurrectAllies = "ABILITY_RESURRECT_ALLIES", Dash = "ABILITY_DASH", Explosion = "ABILITY_EXPLOSION",
        HarmTouch = "ABILITY_HARM_TOUCH", DeathWail = "ABILITY_DEATH_WAIL", RageOfForest = "ABILITY_RAGE_OF_THE_FOREST",
        BerserkerRage = "ABILITY_BERSERKER_RAGE", OrderOfChief = "ABILITY_ORDER_OF_THE_CHIEF", Repair = "ABILITY_REPAIR",
        ManaFeed = "ABILITY_MANA_FEED", Invisibility = "ABILITY_INVISIBILITY", Leap = "ABILITY_LEAP", Purger = "ABILITY_PURGER",
        ImmuneFire = "ABILITY_IMMUNITY_TO_FIRE", ImmuneAir = "ABILITY_IMMUNITY_TO_AIR", ImmuneEarth = "ABILITY_IMMUNITY_TO_EARTH",
        ImmuneWater = "ABILITY_IMMUNITY_TO_WATER", ImmuneMagic = "ABILITY_IMMUNITY_TO_MAGIC", ImmuneMind = "ABILITY_IMMUNITY_TO_MIND_CONTROL",
        ImmuneBlind = "ABILITY_IMMUNITY_TO_BLIND", ImmuneSlow = "ABILITY_IMMUNITY_TO_SLOW", ImmuneBerserk = "ABILITY_IMMUNITY_TO_BERSERK",
        ImmuneCurse = "ABILITY_IMMUNITY_TO_CURSE", ImmuneHypnotize = "ABILITY_IMMUNITY_TO_HYPNOTIZE",
        MagicProof25 = "ABILITY_MAGIC_PROOF_25", MagicProof50 = "ABILITY_MAGIC_PROOF_50", MagicProof75 = "ABILITY_MAGIC_PROOF_75",
        FireProof50 = "ABILITY_FIRE_PROOF_50", Taxpayer = "ABILITY_TAXPAYER", Cowardice = "ABILITY_COWARDICE", DemonRaged = "ABILITY_DEMON_RAGED",
        Taunt = "ABILITY_TAUNT", FastAttack = "ABILITY_FAST_ATTACK", Rebirth = "ABILITY_REBIRTH", DeadlyStrike = "ABILITY_DEADLY_STRIKE",
        PawStrike = "ABILITY_PAW_STRIKE", WardingArrows = "ABILITY_WARDING_ARROWS", PiercingArrow = "ABILITY_PIERCING_ARROW",
        BearRoar = "ABILITY_BEAR_ROAR", LiquidFlameBreath = "ABILITY_LIQUID_FLAME_BREATH", HarpoonStrike = "ABILITY_HARPOON_STRIKE",
        Flamestrike = "ABILITY_FLAMESTRIKE", Flamewave = "ABILITY_FLAMEWAVE", StormBolt = "ABILITY_STORMBOLT", CallStorm = "ABILITY_CALL_STORM",
        AuraFireVuln = "ABILITY_AURA_OF_FIRE_VULNERABILITY", AuraIceVuln = "ABILITY_AURA_OF_ICE_VULNERABILITY",
        AuraLightningVuln = "ABILITY_AURA_OF_LIGHTNING_VULNERABILITY", EnchantedObsidian = "ABILITY_ENCHANTED_OBSIDIAN",
        Magnetism = "ABILITY_DESTRUCTION_MAGIC_MAGNETISM", EldritchAura = "ABILITY_ELDRITCH_AURA", BondOfLight = "ABILITY_BOND_OF_LIGHT",
        SwallowGoblin = "ABILITY_SWALLOW_GOBLIN", SacrificeGoblin = "ABILITY_SACRIFICE_GOBLIN", BalorSummoning = "ABILITY_BALOR_SUMMONIG",
        SummonOther = "ABILITY_SUMMON_OTHER", Sabotage = "ABILITY_SABOTAGE", AmmoSteal = "ABILITY_AMMO_STEAL", ManaDrain = "ABILITY_MANA_DRAIN",
        SyphonMana = "ABILITY_SYPHON_MANA", Scavenger = "ABILITY_SCAVENGER", PackDive = "ABILITY_PACK_DIVE", BattleDive = "ABILITY_BATTLE_DIVE",
        GoblinThrower = "ABILITY_GOBLIN_THROWER", Maneure = "ABILITY_MANEURE", SetSnares = "ABILITY_SET_SNARES", ChainShotAbility = "ABILITY_CHAIN_SHOT",
        RideByAttack = "ABILITY_RIDE_BY_ATTACK", TreeantUnion = "ABILITY_TREEANT_UNION", PowerFeed = "ABILITY_POWER_FEED",
        DefileMagic = "ABILITY_DEFILE_MAGIC", Treachery = "ABILITY_TREACHERY", LizardBite = "ABILITY_LIZARD_BITE",
        HorrorOfDeath = "ABILITY_HORROR_OF_THE_DEATH", Brutality = "ABILITY_BRUTALITY", RandomCaster = "ABILITY_RANDOM_CASTER",
        RandomCasterBless = "ABILITY_RANDOM_CASTER_BLESS";

    /// <summary>Gating, available through the Demon Lord's Gating skill (not a data ability).</summary>
    public const string Gating = "GATING";
}

/// <summary>Effect ids for non-spell effects (spell effects use the spell id).</summary>
public static class Fx
{
    public const string
        Enraged = "FX_ENRAGED", BattleRage = "FX_BATTLE_RAGE", Poison = "FX_POISON", Wound = "FX_WOUND", MarkOfFire = "FX_MARK_OF_FIRE",
        Frozen = "FX_FROZEN", ArmorCrushed = "FX_ARMOR_CRUSHED", Dash = "FX_DASH", Invisible = "FX_INVISIBLE", Entangled = "FX_ENTANGLED",
        TakeRoots = "FX_TAKE_ROOTS", Sleep = "FX_SLEEP", WeakeningStrike = "FX_WEAKENING_STRIKE", EvilEye = "FX_EVIL_EYE",
        RageOfForest = "FX_RAGE_OF_FOREST", BerserkerRage = "FX_BERSERKER_RAGE", Benediction = "FX_BENEDICTION", BansheeHowl = "FX_BANSHEE_HOWL",
        RallyingCry = "FX_RALLYING_CRY", Battlecry = "FX_BATTLECRY", BearRoar = "FX_BEAR_ROAR", Ignite = "FX_IGNITE", Feared = "FX_FEARED",
        Sacrifice = "FX_SACRIFICE", RuneCharge = "FX_RUNE_CHARGE", RuneBerserking = "FX_RUNE_BERSERKING", RuneMagicControl = "FX_RUNE_MAGIC_CONTROL",
        RuneElementalImmunity = "FX_RUNE_ELEMENTAL_IMMUNITY", RuneEtherealness = "FX_RUNE_ETHEREALNESS", RuneThunderclap = "FX_RUNE_THUNDERCLAP",
        RuneBattleRage = "FX_RUNE_BATTLE_RAGE", RuneDragonform = "FX_RUNE_DRAGONFORM", DeepFreeze = "FX_DEEP_FREEZE", ResurrectionPenalty = "FX_RESURRECTION_PENALTY",
        Stunned = "FX_STUNNED", ArcaneArmorHp = "FX_ARCANE_ARMOR", Seduced = "FX_SEDUCED", RunicAttunement = "FX_RUNIC_ATTUNEMENT", Sabotaged = "FX_SABOTAGED";
}

public static class Sk
{
    public const string
        Offence = "HERO_SKILL_OFFENCE", Defence = "HERO_SKILL_DEFENCE", Leadership = "HERO_SKILL_LEADERSHIP", Luck = "HERO_SKILL_LUCK",
        Sorcery = "HERO_SKILL_SORCERY", Destructive = "HERO_SKILL_DESTRUCTIVE_MAGIC", Dark = "HERO_SKILL_DARK_MAGIC", Light = "HERO_SKILL_LIGHT_MAGIC",
        Summoning = "HERO_SKILL_SUMMONING_MAGIC", WarMachines = "HERO_SKILL_WAR_MACHINES", Training = "HERO_SKILL_TRAINING", Gating = "HERO_SKILL_GATING",
        Necromancy = "HERO_SKILL_NECROMANCY", Avenger = "HERO_SKILL_AVENGER", Artificer = "HERO_SKILL_ARTIFICIER", Invocation = "HERO_SKILL_INVOCATION",
        Runelore = "HERO_SKILL_RUNELORE", BloodRage = "HERO_SKILL_DEMONIC_RAGE", Shout = "HERO_SKILL_VOICE", Learning = "HERO_SKILL_LEARNING",
        Logistics = "HERO_SKILL_LOGISTICS",
        ShatterDestructive = "HERO_SKILL_SHATTER_DESTRUCTIVE_MAGIC", ShatterDark = "HERO_SKILL_SHATTER_DARK_MAGIC",
        ShatterLight = "HERO_SKILL_SHATTER_LIGHT_MAGIC", ShatterSummoning = "HERO_SKILL_SHATTER_SUMMONING_MAGIC";

    // Perks
    public const string
        Archery = "HERO_SKILL_ARCHERY", Frenzy = "HERO_SKILL_FRENZY", Tactics = "HERO_SKILL_TACTICS", Evasion = "HERO_SKILL_EVASION",
        Protection = "HERO_SKILL_PROTECTION", Toughness = "HERO_SKILL_TOUGHNESS", HoldGround = "HERO_SKILL_HOLD_GROUND", LastStand = "HERO_SKILL_LAST_STAND",
        Preparation = "HERO_SKILL_PREPARATION", MagicResistance = "HERO_SKILL_RESISTANCE", SoldiersLuck = "HERO_SKILL_LUCKY_STRIKE",
        DeadLuck = "HERO_SKILL_DEAD_LUCK", ArcaneTraining = "HERO_SKILL_ARCANE_TRAINING", Intelligence = "HERO_SKILL_INTELLIGENCE",
        BarbIntelligence = "HERO_SKILL_BARBARIAN_INTELLIGENCE", MasterIce = "HERO_SKILL_MASTER_OF_ICE", MasterFire = "HERO_SKILL_MASTER_OF_FIRE",
        MasterStorms = "HERO_SKILL_MASTER_OF_LIGHTNINGS", MasterCurses = "HERO_SKILL_MASTER_OF_CURSES", MasterMind = "HERO_SKILL_MASTER_OF_MIND",
        MasterPain = "HERO_SKILL_MASTER_OF_SICKNESS", MasterBlessing = "HERO_SKILL_MASTER_OF_BLESSING", MasterAbjuration = "HERO_SKILL_MASTER_OF_ABJURATION",
        MasterWrath = "HERO_SKILL_MASTER_OF_WRATH", MasterEarthblood = "HERO_SKILL_MASTER_OF_QUAKES", MasterConjuration = "HERO_SKILL_MASTER_OF_CREATURES",
        MasterLife = "HERO_SKILL_MASTER_OF_ANIMATION", RetaliationStrike = "HERO_SKILL_HOLY_CHARGE", Benediction = "HERO_SKILL_PRAYER",
        DivineGuidance = "HERO_SKILL_ENCOURAGE", Retribution = "HERO_SKILL_RETRIBUTION", GuardianAngel = "HERO_SKILL_GUARDIAN_ANGEL",
        UnstoppableCharge = "HERO_SKILL_ABSOLUTE_CHARGE", FallenKnight = "HERO_SKILL_PARIAH", Hellfire = "HERO_SKILL_DEMONIC_FIRE",
        MarkOfDamned = "HERO_SKILL_DEMONIC_STRIKE", ExcruciatingStrike = "HERO_SKILL_CRITICAL_STRIKE", Hellwrath = "HERO_SKILL_DEMONIC_RETALIATION",
        SearingFires = "HERO_SKILL_DEMONIC_FLAME", QuickGating = "HERO_SKILL_QUICK_GATING", GateMaster = "HERO_SKILL_GATING_MASTERY",
        SwarmingGate = "HERO_SKILL_CRITICAL_GATING", ConsumeCorpse = "HERO_SKILL_CONSUME_CORPSE", SufferingStrike = "HERO_SKILL_WEAKENING_STRIKE",
        BarbSufferingStrike = "HERO_SKILL_BARBARIAN_WEAKENING_STRIKE", FireResistance = "HERO_SKILL_FIRE_PROTECTION",
        BarbFireResistance = "HERO_SKILL_BARBARIAN_FIRE_PROTECTION", BansheeHowl = "HERO_SKILL_DEATH_SCREAM", MarkOfNecromancer = "HERO_SKILL_SPIRIT_LINK",
        Boneward = "HERO_SKILL_SPELLPROOF_BONES", ChillingBones = "HERO_SKILL_CHILLING_BONES", ColdSteel = "HERO_SKILL_CHILLING_STEEL",
        ColdDeath = "HERO_SKILL_DEADLY_COLD", Twilight = "HERO_SKILL_TWILIGHT", HowlOfTerror = "HERO_SKILL_ABSOLUTE_FEAR",
        RainOfArrows = "HERO_SKILL_MULTISHOT", DeadeyeShot = "HERO_SKILL_SNIPE_DEAD", ImbueArrow = "HERO_SKILL_IMBUE_ARROW",
        ElvenLuck = "HERO_SKILL_ELVEN_LUCK", NaturesWrath = "HERO_SKILL_FOREST_RAGE", StormWind = "HERO_SKILL_STORM_WIND",
        BarbStormWind = "HERO_SKILL_BARBARIAN_STORM_WIND", FogVeil = "HERO_SKILL_FOG_VEIL", BarbFogVeil = "HERO_SKILL_BARBARIAN_FOG_VEIL",
        NaturesLuck = "HERO_SKILL_ABSOLUTE_LUCK", MarkOfWizard = "HERO_SKILL_MAGIC_BOND", MagicMirror = "HERO_SKILL_MAGIC_MIRROR",
        EmpoweredSpells = "HERO_SKILL_EMPOWERED_SPELLS", ElementalVision = "HERO_SKILL_ELEMENTAL_VISION", Ballista = "HERO_SKILL_BALLISTA",
        TripleBallista = "HERO_SKILL_TRIPLE_BALLISTA", FirstAid = "HERO_SKILL_FIRST_AID", PlagueTent = "HERO_SKILL_LAST_AID",
        Catapult = "HERO_SKILL_CATAPULT", FieryWrath = "HERO_SKILL_ANCIENT_SMITHY", BarbFieryWrath = "HERO_SKILL_BARBARIAN_ANCIENT_SMITHY",
        Ignite = "HERO_SKILL_SET_AFIRE", ManaBurst = "HERO_SKILL_SUN_FIRE", BarbManaBurst = "HERO_SKILL_BARBARIAN_SUN_FIRE",
        CorruptedSoil = "HERO_SKILL_SOIL_BURN", BarbCorruptedSoil = "HERO_SKILL_BARBARIAN_SOIL_BURN", SapMagic = "HERO_SKILL_MAGIC_CUSHION",
        SealOfDarkness = "HERO_SKILL_SUPRESS_DARK", SuppressLight = "HERO_SKILL_SUPRESS_LIGHT", Banish = "HERO_SKILL_UNSUMMON",
        ShrugDarkness = "HERO_SKILL_SHRUG_DARKNESS", EternalLight = "HERO_SKILL_ETERNAL_LIGHT", RefinedMana = "HERO_SKILL_ELITE_CASTERS",
        BarbRefinedMana = "HERO_SKILL_BARBARIAN_ELITE_CASTERS", Distract = "HERO_SKILL_DISTRACT", BarbDistract = "HERO_SKILL_BARBARIAN_DISTRACT",
        SwiftMind = "HERO_SKILL_QUICKNESS_OF_MIND", TeleportAssault = "HERO_SKILL_TELEPORT_ASSAULT", AuraOfSwiftness = "HERO_SKILL_FAST_AND_FURIOUS",
        PowerOfSpeed = "HERO_SKILL_POWER_OF_HASTE", PowerOfEndurance = "HERO_SKILL_POWER_OF_STONE", WarlocksLuck = "HERO_SKILL_LUCKY_SPELLS",
        ErraticMana = "HERO_SKILL_CHAOTIC_SPELLS", DarkRenewal = "HERO_SKILL_PAYBACK", Exorcism = "HERO_SKILL_ELEMENTAL_OVERKILL",
        RuneArmour = "HERO_SKILL_RUNIC_ARMOR", RunicMachines = "HERO_SKILL_RUNIC_MACHINES", OffensiveFormation = "HERO_SKILL_OFFENSIVE_FORMATION",
        DefensiveFormation = "HERO_SKILL_DEFENSIVE_FORMATION", DwarvenLuck = "HERO_SKILL_DWARVEN_LUCK", AbsoluteProtection = "HERO_SKILL_ABSOLUTE_PROTECTION",
        RunicAttunement = "HERO_SKILL_RUNIC_ATTUNEMENT", TapRunes = "HERO_SKILL_TAP_RUNES", FineRune = "HERO_SKILL_FINE_RUNE", StrongRune = "HERO_SKILL_STRONG_RUNE",
        Empathy = "HERO_SKILL_EMPATHY", ArtificialGlory = "HERO_SKILL_ARTIFICIAL_GLORY", MarchOfGolems = "HERO_SKILL_MARCH_OF_THE_MACHINES",
        Stamina = "HERO_SKILL_BODYBUILDING", StunningBlow = "HERO_SKILL_STUNNING_BLOW", PowerfulBlow = "HERO_SKILL_POWERFULL_BLOW",
        MemoryOfBlood = "HERO_SKILL_MEMORY_OF_OUR_BLOOD", AbsoluteRage = "HERO_SKILL_ABSOLUTE_RAGE", MightOverMagic = "HERO_SKILL_MIGHT_OVER_MAGIC",
        BattleElation = "HERO_SKILL_BATTLE_ELATION", Bloodfire = "HERO_SKILL_POWER_OF_BLOOD", LuckOfBarbarian = "HERO_SKILL_LUCK_OF_THE_BARBARIAN",
        ShoutTraining = "HERO_SKILL_VOICE_TRAINING", MightyShout = "HERO_SKILL_MIGHTY_VOICE", ShoutOfRage = "HERO_SKILL_VOICE_OF_RAGE",
        BackToTheVoid = "HERO_SKILL_DEATH_TO_NONEXISTENT", DefendUsAll = "HERO_SKILL_DEFEND_US_ALL", FamiliarGround = "HERO_SKILL_ROAD_HOME",
        CorruptDestructive = "HERO_SKILL_CORRUPT_DESTRUCTIVE", CorruptDark = "HERO_SKILL_CORRUPT_DARK", CorruptLight = "HERO_SKILL_CORRUPT_LIGHT",
        CorruptSummoning = "HERO_SKILL_CORRUPT_SUMMONING", WeakenDestructive = "HERO_SKILL_WEAKEN_DESTRUCTIVE", WeakenDark = "HERO_SKILL_WEAKEN_DARK",
        WeakenLight = "HERO_SKILL_WEAKEN_LIGHT", WeakenSummoning = "HERO_SKILL_WEAKEN_SUMMONING", DetainDestructive = "HERO_SKILL_DETAIN_DESTRUCTIVE",
        DetainDark = "HERO_SKILL_DETAIN_DARK", DetainLight = "HERO_SKILL_DETAIN_LIGHT", DetainSummoning = "HERO_SKILL_DETAIN_SUMMONING",
        SecretsOfDestruction = "HERO_SKILL_SECRETS_OF_DESTRUCTION";
}
