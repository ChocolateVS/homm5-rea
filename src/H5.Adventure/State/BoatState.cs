using H5.Adventure.Map;

namespace H5.Adventure.State;

/// <summary>
/// A ship on the adventure map (the original's CAdvMapShip). Ships belong to nobody: any hero
/// may board an empty one. While a hero sails, the ship moves with him.
/// </summary>
public sealed class BoatState
{
    public int Id;
    public Tile Pos;
    /// <summary>Ship model: 0 Galley, 1 Galleon, 2 Junk (H5_Game.exe FUN_008dd470 table order).</summary>
    public int Model;
    /// <summary>The sailing hero, -1 when the ship is empty.</summary>
    public int HeroId = -1;
    /// <summary>Map object the ship was placed as in the editor, -1 for built/created ships.</summary>
    public int MapObject = -1;
    /// <summary>Facing in radians (the editor's Rot for placed ships; updated as the ship sails).</summary>
    public float Rot;

    public static readonly string[] ModelShared =
    {
        "MapObjects/Ship_Galley.(AdvMapBuildingShared).xdb",
        "MapObjects/Ship_Galeon.(AdvMapBuildingShared).xdb",
        "MapObjects/Ship_Junk.(AdvMapBuildingShared).xdb",
    };
}
