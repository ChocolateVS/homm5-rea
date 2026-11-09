using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;

namespace H5.AdvNet;

/// <summary>
/// The MPWait item state (CMPWaitItem +0x28): the host's lobby count (00720080) takes 0 / 3 / 5 as humans, 1 as
/// computers, 4 as not counted and the rest (2) as closed; 0071d590 refuses the start while a row is 0 or 4 and counts
/// the rows in 3 that a connection holds; 0071d3e0 toggles 0 ↔ 3 on a held row.
/// </summary>
public enum NetSlotState { Human = 0, Computer = 1, Closed = 2, Ready = 3, Open = 4 }

/// <summary>One row of the network setup as it travels in the room settings (<see cref="AdvNetSettings.Setup"/>).</summary>
public sealed class NetSetupRow
{
    public int MapPlayer { get; set; }
    public NetSlotState State { get; set; }
    public string Name { get; set; } = "";
    public string? Race { get; set; }
    public string? Hero { get; set; }
    public int Bonus { get; set; }
    /// <summary>Lobby player id holding the row; -1 none.</summary>
    public int Owner { get; set; } = -1;
}

/// <summary>
/// The network game setup behind UI/MPWait (CMPUbiComWaitHostLogic, vtable 00e3a5dc, over the game-setup model 00ca0240):
/// one row per active map player with the item state, colour, faction, starting hero and bonus. The host is the
/// authority: clients send change requests (CMPWaitChangeRequest 00711580: row, kind, direction) that
/// <see cref="Request"/> applies like 0071f3d0, and every peer rebuilds the rows from the room settings.
/// Race / hero / bonus / colour rules are the custom game's (<see cref="CustomGameSetup"/>, the same model calls).
/// </summary>
public sealed class NetGameSetup
{
    /// <summary>Lock bits of CMPWaitLockButtonsUICommand (007111d0, built by 0071e7b0): set = the arrows are disabled.</summary>
    public const int LockState = 2, LockColour = 4, LockRace = 8, LockHero = 0x10, LockBonus = 0x20;

    public CustomGameSetup Model { get; }
    public IReadOnlyList<SetupSlot> Rows => Model.Slots;

    NetGameSetup(CustomGameSetup model) => Model = model;

    /// <summary>
    /// A new room's rows. GAP (the host logic's initial fill is not traced): the host takes the first row that can be
    /// human, the other rows that can be human are open (state 4), the rest computers.
    /// </summary>
    public static NetGameSetup Create(AdvMap map, AdventureDb db, int hostId, string hostName)
    {
        var g = new NetGameSetup(new CustomGameSetup(map, db, hostName, false));
        bool seated = false;
        foreach (var s in g.Rows)
        {
            s.Owner = -1;
            if (!seated && s.CanBeHuman) { g.Seat(s, hostId, hostName); seated = true; }
            else g.Set(s, s.CanBeHuman ? NetSlotState.Open : NetSlotState.Computer);
        }
        return g;
    }

    /// <summary>Rebuilds the rows sent by the host (choices per map player) over the local copy of the map.</summary>
    public static NetGameSetup FromRows(AdvMap map, AdventureDb db, IEnumerable<NetSetupRow> rows)
    {
        var g = new NetGameSetup(new CustomGameSetup(map, db, "", false));
        var byMp = rows.ToDictionary(r => r.MapPlayer);
        foreach (var s in g.Rows)
        {
            if (!byMp.TryGetValue(s.MapPlayer, out var r)) continue;
            g.Set(s, r.State);
            s.Owner = r.Owner;
            s.Name = r.Name;
            s.Race = r.Race;
            s.Hero = r.Hero;
            s.Bonus = r.Bonus;
            g.Model.Revalidate(s);
        }
        return g;
    }

    public List<NetSetupRow> ToRows() => Rows.Select(s => new NetSetupRow
    {
        MapPlayer = s.MapPlayer, State = s.NetState, Name = s.Name, Race = s.Race, Hero = s.Hero, Bonus = s.Bonus, Owner = s.Owner,
    }).ToList();

    void Set(SetupSlot s, NetSlotState st)
    {
        s.NetState = st;
        s.State = st switch
        {
            NetSlotState.Human or NetSlotState.Ready => SetupSlot.SlotState.Human,
            NetSlotState.Closed => SetupSlot.SlotState.Closed,
            NetSlotState.Open => SetupSlot.SlotState.Open,
            _ => SetupSlot.SlotState.Computer,
        };
        if (st is not (NetSlotState.Human or NetSlotState.Ready)) { s.Name = ""; s.Owner = -1; }
    }

    void Seat(SetupSlot s, int player, string name)
    {
        Set(s, NetSlotState.Human);
        s.Owner = player;
        s.Name = name;
    }

    public SetupSlot? RowOf(int player) => Rows.FirstOrDefault(s => s.Owner == player);

    /// <summary>
    /// A player joins the room: it takes the first open row (GAP: the slot choice on connect is not traced). False: no open row.
    /// </summary>
    public bool Join(int player, string name)
    {
        if (RowOf(player) != null) return true;
        var s = Rows.FirstOrDefault(r => r.NetState == NetSlotState.Open);
        if (s == null) return false;
        Seat(s, player, name);
        return true;
    }

    /// <summary>A player leaves: its row is open again (GAP: 0071d8e0 / 0071d830's row reset not decoded).</summary>
    public void Leave(int player)
    {
        foreach (var s in Rows.Where(r => r.Owner == player)) Set(s, NetSlotState.Open);
    }

    /// <summary>The ready toggle of a held row (0071d3e0 with a connection: 0 ↔ 3).</summary>
    public bool SetReady(int player, bool ready)
    {
        if (RowOf(player) is not { } s) return false;
        var st = ready ? NetSlotState.Ready : NetSlotState.Human;
        if (s.NetState == st) return false;
        s.NetState = st;
        return true;
    }

    /// <summary>
    /// 0071e7b0: the lock bits a viewer's row arrows get. A client may change colour / race / hero / bonus only on its own
    /// row while it is not ready (state 0); the host on its own row while not ready and on computer rows (state 1)
    /// (the exe tests row 0 for the host's own row). Each kind also needs the model's permission (vf 0x2c / 0x34 / 0x3c /
    /// 0x44). The host's state arrows are locked only on a computer row that can be neither human (+0x20) nor closed
    /// (+0x5c). GAP: a client's state bit is lost in the decompile; its own row's state arrows (the ready toggle,
    /// 0071f3d0 case 1) are left enabled, the others locked.
    /// </summary>
    public int Locks(int row, int viewer, int host)
    {
        var s = Rows[row];
        bool mine;
        int mask = 0;
        if (viewer != host)
        {
            mine = s.Owner == viewer && s.NetState == NetSlotState.Human;
            if (s.Owner != viewer) mask |= LockState;
        }
        else
        {
            mine = (s.Owner == host && s.NetState == NetSlotState.Human) || (s.Owner < 0 && s.NetState == NetSlotState.Computer);
            if (s.Owner < 0 && s.NetState == NetSlotState.Computer && !s.CanBeHuman && !s.CanClose) mask |= LockState;
        }
        if (!mine || !Model.CanChange(row, CustomGameSetup.Change.Colour)) mask |= LockColour;
        if (!mine || !Model.CanChange(row, CustomGameSetup.Change.Race)) mask |= LockRace;
        if (!mine || !Model.CanChange(row, CustomGameSetup.Change.Hero)) mask |= LockHero;
        if (!mine || !Model.CanChange(row, CustomGameSetup.Change.Bonus)) mask |= LockBonus;
        return mask;
    }

    static int LockOf(CustomGameSetup.Change kind) => kind switch
    {
        CustomGameSetup.Change.State => LockState, CustomGameSetup.Change.Colour => LockColour, CustomGameSetup.Change.Race => LockRace,
        CustomGameSetup.Change.Hero => LockHero, _ => LockBonus,
    };

    /// <summary>
    /// The host's handling of a change request (0071f3d0, switch on the kind) — true when the rows changed.
    /// 1 state: on a row a connection holds, its holder toggles ready (0071d3e0); the host's arrow on another player's
    /// row is the kick question (kick_header / kick_text, asked on the host's screen, not here); on a free row only the
    /// host steps the state (0071d3e0), and a row that becomes closed is cleared (0071ec40). 2 colour (model vf 0x2c /
    /// 0x30), 3 race (vf 0x34 / 0x38), 4 hero (vf 0x3c / 0x40), 5 bonus (vf 0x44 / 0x48), each only when not locked for
    /// the sender.
    /// </summary>
    public bool Request(int sender, int host, int row, CustomGameSetup.Change kind, bool right)
    {
        if (row < 0 || row >= Rows.Count || !Enum.IsDefined(kind)) return false;
        if ((Locks(row, sender, host) & LockOf(kind)) != 0) return false;
        var s = Rows[row];
        if (kind != CustomGameSetup.Change.State)
        {
            var before = Snapshot();
            Model.Apply(row, kind, right, s.Name);
            return Snapshot() != before;
        }
        if (s.Owner >= 0)
            return s.Owner == sender && SetReady(sender, s.NetState != NetSlotState.Ready);
        if (sender != host) return false;
        var next = Step(s, right);
        if (next == s.NetState) return false;
        Set(s, next);
        if (next == NetSlotState.Closed) s.Name = ""; // 0071ec40 (model vf 0x64 / the race loop there: GAP)
        return true;
    }

    /// <summary>
    /// 0071d3e0 for a row no connection holds (left = direction byte 0). Item flags +0x20 / +0x21 / +0x22 are read as
    /// can-be-closed / can-be-human / can-be-computer (UNVERIFIED: where the item takes them from is not traced).
    /// computer → open (left, or when it cannot close) else closed; closed → computer (left) else open; open → closed (left)
    /// else computer; each only between states the row allows.
    /// </summary>
    static NetSlotState Step(SetupSlot s, bool right)
    {
        bool left = !right, close = s.CanClose, human = s.CanBeHuman, comp = s.CanBeComputer;
        switch (s.NetState)
        {
            case NetSlotState.Computer when close || human:
                return (left || !close) && human ? NetSlotState.Open : NetSlotState.Closed;
            case NetSlotState.Closed when human || comp:
                return !((left && comp) || !human) ? NetSlotState.Open : NetSlotState.Computer;
            case NetSlotState.Open when comp || close:
                return !((left && close) || !comp) ? NetSlotState.Computer : NetSlotState.Closed;
            default:
                return s.NetState;
        }
    }

    string Snapshot() => string.Join(";", Rows.Select(s => $"{s.NetState},{s.Owner},{s.Name},{s.Race},{s.Hero},{s.Bonus}"));

    /// <summary>
    /// 0071d590 (host logic vf 0x18, the Start test): the model's two-team test (vf 0x4c), no row human-not-ready (0) or
    /// open (4), and more than one ready row (3) that a connection holds — the host's own row included.
    /// </summary>
    public bool CanStart() =>
        Model.CanStart()
        && !Rows.Any(s => s.NetState is NetSlotState.Human or NetSlotState.Open)
        && Rows.Count(s => s.NetState == NetSlotState.Ready && s.Owner >= 0) > 1;

    /// <summary>The engine's start setup (00c9fa80 applies the bonuses): closed rows are left out.</summary>
    public GameSetup ToGameSetup(ulong seed, int difficulty) => Model.ToGameSetup(seed, difficulty);
}
