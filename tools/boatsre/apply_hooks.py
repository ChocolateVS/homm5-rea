"""Applies the boats/water client hooks to a pristine copy of AdvWorld.cs / AdventureScreen.cs.
Usage: python apply_hooks.py <AdvWorld.cs> <AdventureScreen.cs>   (files are edited in place)
Lets the hooks be staged/verified without other agents' uncommitted hunks in the same files."""
import sys

def edit(p, old, new):
    t = open(p, encoding='utf-8').read()
    if new in t: return
    assert t.count(old) == 1, (p, old[:70], t.count(old))
    open(p, 'w', encoding='utf-8').write(t.replace(old, new))

w, a = sys.argv[1], sys.argv[2]
edit(w, '''    public AdvCamera Camera { get; private set; } = null!;''', '''    public AdvCamera Camera { get; private set; } = null!;
    /// <summary>Ships (see <see cref="Water.BoatLayer"/>).</summary>
    public Water.BoatLayer? Boats { get; private set; }''')
edit(w, '''            _terrain[f] = AdvTerrain.Build(floor.Data);
            _floors[f].AddChild(_terrain[f]);''', '''            _terrain[f] = AdvTerrain.Build(floor.Data);
            _floors[f].AddChild(_terrain[f]);
            if (Water.SeaPlane.Build(floor, Unit) is { } sea) { _floors[f].AddChild(sea); _seaLevel[f] = Water.SeaPlane.Level; }''')
edit(w, '''        foreach (var h in _session.S.Heroes) SyncHero(h);

        _pathRoot = new Node3D { Name = "Path" };''', '''        foreach (var h in _session.S.Heroes) SyncHero(h);
        Boats = new Water.BoatLayer(this, _session);
        AddChild(Boats);
        Boats.Sync();

        _pathRoot = new Node3D { Name = "Path" };''')
edit(w, '''    public Vector3 StandPoint(Tile t) => StandPointAt(TileCenter(t));''', '''    public Vector3 StandPoint(Tile t) => AboveSea(StandPointAt(TileCenter(t)), t.Floor);

    /// <summary>Sea surface height per floor (see <see cref="Water.SeaPlane"/>), -inf without sea.</summary>
    readonly float[] _seaLevel = { float.NegativeInfinity, float.NegativeInfinity };

    /// <summary>Heroes, ships, path markers and sea objects stay on the water surface, not the sea bed.</summary>
    public Vector3 AboveSea(Vector3 p, int floor) => floor is >= 0 and < 2 && p.Y < _seaLevel[floor] ? new Vector3(p.X, _seaLevel[floor], p.Z) : p;''')
edit(w, '''    public Vector3 StandPointAt(float mapX, float mapY, int floor) =>
        StandPointAt(new Vector3(mapX * Unit, HeightAt(floor, mapX, mapY), -mapY * Unit));''', '''    public Vector3 StandPointAt(float mapX, float mapY, int floor) =>
        AboveSea(StandPointAt(new Vector3(mapX * Unit, HeightAt(floor, mapX, mapY), -mapY * Unit)), floor);''')
edit(w, '''        var pos = TileCenter(o.Pos);''', '''        var pos = o.Shared.WaterBased ? AboveSea(TileCenter(o.Pos), o.Pos.Floor) : TileCenter(o.Pos);''')
edit(w, '''        RefreshShroud();
    }

    // ---------------------------------------------------------------- lighting''', '''        RefreshShroud();
        Boats?.OnFloorChanged();
    }

    // ---------------------------------------------------------------- lighting''')

edit(w, '''        for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        foreach (var h in _session.S.Heroes) SyncHero(h);
    }''', '''        for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        foreach (var h in _session.S.Heroes) SyncHero(h);
        Boats?.Sync();
    }''')

edit(a, '''            AdvCursors.Set(AdvCursor.Use, days);
        else AdvCursors.Set(AdvCursor.Move, days);''', '''            AdvCursors.Set(AdvCursor.Use, days);
        else AdvCursors.Set(Water.BoatLayer.MoveCursor(Session.Game, hero, t), days);''')
edit(a, '''            case HeroMoved m:
                await AnimateMove(m, player);
                if (s.Hero(m.HeroId) is { } hm) World.SyncHero(hm);
                Hud.Refresh();
                break;''', '''            case HeroMoved m:
                await AnimateMove(m, player);
                if (s.Hero(m.HeroId) is { } hm) World.SyncHero(hm);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case HeroEmbarked he:
                // The boarding step: walk onto the ship, then the ship takes the hero aboard.
                await AnimateMove(new HeroMoved(he.HeroId, new[] { he.From, he.To }, he.MovePointsLeft), player);
                if (s.Hero(he.HeroId) is { } hb) World.SyncHero(hb);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case HeroDisembarked hd:
                World.Boats?.Sync();
                await AnimateMove(new HeroMoved(hd.HeroId, new[] { hd.From, hd.To }, hd.MovePointsLeft), player);
                if (s.Hero(hd.HeroId) is { } hl) World.SyncHero(hl);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case BoatCreated or BoatMoved:
                World.Boats?.Sync();
                Hud.UpdateMinimap();
                break;''')
edit(a, '''                case "join_free":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), AdvTexts.Get(AdvTexts.CreaturesJoin), null, y => { accept = y; done(); }));
                    break;''', '''                case "join_free":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), AdvTexts.Get(AdvTexts.CreaturesJoin), null, y => { accept = y; done(); }));
                    break;
                case "shipyard":
                    await Dialog(done => Water.BoatLayer.ShipyardOffer(_dialogLayer, Session.S.Players.First(p => p.Index == Session.S.Hero(c.Hero)!.Owner), y => { accept = y; done(); }));
                    break;''')
print('hooks applied')
