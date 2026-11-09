using H5.Adventure.Map;

namespace H5.Adventure.State;

/// <summary>
/// A caravan on the adventure map (TotE CAdvMapCaravan, vtables 00e96494 / 00e96434 …, built by 00ce5b30 → 00ce5880,
/// shared MapObjects/Caravans/Caravan.xdb). It carries creatures bought (or moved) from a source town or dwelling to
/// a destination town of its owner and walks there day by day (00ce4de0).
/// </summary>
public sealed class CaravanState
{
    public int Id;
    /// <summary>The owning player (caravan interface vf 0x24 = +0x174).</summary>
    public int Owner;
    public Tile Pos;
    /// <summary>The destination tile, the town's entrance (interface vf 0x20 = +0x170); invalid (X &lt; 0) once arrived.</summary>
    public Tile Dest;
    /// <summary>The destination town object (for the hire screen's statistics), -1 for a script caravan without one.</summary>
    public int DestTown = -1;
    /// <summary>The source town or dwelling object, -1 for a caravan made by a script (CreateCaravan).</summary>
    public int Source = -1;
    /// <summary>Days left (+0x178, 00ce4d40): (route cost − 1) / speed + 1; −1 = stopped (no route, T_CARAVANS_STOPPED_*).</summary>
    public int DaysLeft = -1;
    /// <summary>Days the caravan could not move (+0x198, 00ce4d40 / 00ce4de0).</summary>
    public int Stuck;
    /// <summary>The army (+0x164, 7 slots, 00ce5880).</summary>
    public Stack?[] Army = H5.Adventure.State.Army.Empty();
    /// <summary>The script name given to CreateCaravan (005e32c0), "" otherwise.</summary>
    public string Name = "";

    public bool Stopped => DaysLeft == -1;
}
