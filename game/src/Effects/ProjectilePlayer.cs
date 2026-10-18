using Godot;
using H5.Assets.Effects;
using H5.Assets.Model;
using H5Reborn.Render;

namespace H5Reborn.Effects;

/// <summary>
/// Plays an original (Shot): a flying missile (model and/or trail effect) or a chain of
/// line-segment effects, then the strike visual, calling <c>onHit</c> at impact.
///
/// Missile flight: duration = distance / Speed (Speed is world units per millisecond in
/// the data, e.g. arrows 0.03), with a parabolic arc whose apex is Height × distance
/// (arrows 0.03, axes 0.1, catapult 0.2, Scatter Shot 0.3). This reading is inferred from
/// the data ranges; the exe routine wasn't located (see docs/formats/effects.md).
/// </summary>
public partial class ProjectilePlayer : Node3D
{
    ShotDef _shot = null!;
    Vector3 _from, _to;
    float _duration, _apex, _t;
    Action? _onHit;
    Node3D? _missile;
    EffectPlayer? _trail;
    bool _hit;

    /// <summary>
    /// Fires a shot from <paramref name="shooter"/> (its facing gives forward/right for the
    /// start offsets) towards world point <paramref name="to"/>. Returns null if the shot is unknown.
    /// </summary>
    public static ProjectilePlayer? Fire(string shotXdb, Node3D world, Node3D shooter, Vector3 to, Action? onHit = null)
    {
        var shot = EffectAssets.Loader.LoadShot(shotXdb);
        if (shot == null) { onHit?.Invoke(); return null; }

        var basis = shooter.GlobalBasis.Orthonormalized();
        var fwd = -basis.Z; // creature models face -Z after the Z-up → Y-up conversion and LookAt
        var right = basis.X;
        var m = shot.Missile;
        var from = shooter.GlobalPosition + fwd * m.ForwardOffset + Vector3.Up * m.StartZOffset + right * m.RightOffset;

        var p = new ProjectilePlayer { _shot = shot, _from = from, _to = to, _onHit = onHit, Name = "Shot" };
        world.AddChild(p);
        p.Start(world);
        return p;
    }

    /// <summary>Fires between two world points without a shooter node.</summary>
    public static ProjectilePlayer? Fire(string shotXdb, Node3D world, Vector3 from, Vector3 to, Action? onHit = null)
    {
        var shot = EffectAssets.Loader.LoadShot(shotXdb);
        if (shot == null) { onHit?.Invoke(); return null; }
        var p = new ProjectilePlayer { _shot = shot, _from = from, _to = to, _onHit = onHit, Name = "Shot" };
        world.AddChild(p);
        p.Start(world);
        return p;
    }

    void Start(Node3D world)
    {
        if (_shot.StartEffectPath != null)
        {
            float yaw = _shot.RotateStartEffectToTarget ? YawTowards(_from, _to) : 0f;
            EffectPlayer.PlayAt(_shot.StartEffectPath, world, _from, yaw);
        }

        float dist = _from.DistanceTo(_to);
        var m = _shot.Missile;
        bool missile = m.ModelPath != null || m.EffectPath != null;
        if (_shot.Line.Effects.Count > 0)
            PlayLine(world, dist);

        if (missile && m.Speed > 0f)
        {
            _duration = dist / (m.Speed * 1000f);
            _apex = m.Height * dist;
            if (m.ModelPath != null && Game.Instance.Fs.Exists(m.ModelPath))
            {
                _missile = ModelBuilder.Build(new ModelLoader(Game.Instance.Xdb).Load(m.ModelPath));
                AddChild(_missile);
            }
            if (m.EffectPath != null)
            {
                var holder = new Node3D { Name = "Trail" };
                AddChild(holder);
                _trail = EffectPlayer.Play(m.EffectPath, holder);
                if (_trail != null) _trail.AutoFree = false;
                _missile ??= holder;
            }
            UpdateFlight(0f);
        }
        else
        {
            // Instant / line shots: strike after the line has been drawn plus StrikeDelay.
            _duration = LineDuration(dist) + _shot.StrikeDelayMs / 1000f;
        }
    }

    float LineDuration(float dist)
    {
        var l = _shot.Line;
        if (l.Effects.Count == 0 || l.SegmentLength <= 0f) return 0f;
        int segments = Math.Max(1, (int)MathF.Ceiling(dist / l.SegmentLength));
        return segments * l.StepDelayMs / 1000f;
    }

    void PlayLine(Node3D world, float dist)
    {
        // A chain of effects placed every SegmentLength along the line, skipping the first
        // SkipSegmentsNumber segments, each started StepDelay after the previous one and
        // living EffectLifeTime.
        var l = _shot.Line;
        float seg = l.SegmentLength > 0f ? l.SegmentLength : dist;
        int count = Math.Max(1, (int)MathF.Ceiling(dist / seg));
        float yaw = YawTowards(_from, _to);
        var dir = dist > 0f ? (_to - _from) / dist : Vector3.Forward;
        for (int i = l.SkipSegmentsNumber; i < count; i++)
        {
            var pos = _from + dir * Math.Min(dist, (i + 0.5f) * seg);
            var effect = l.Effects[i % l.Effects.Count];
            float delay = (i - l.SkipSegmentsNumber) * l.StepDelayMs / 1000f;
            float life = l.EffectLifeTimeMs / 1000f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () =>
            {
                var fx = EffectPlayer.PlayAt(effect, world, pos, yaw);
                if (fx != null && life > 0f) GetTree().CreateTimer(life).Timeout += () => { if (IsInstanceValid(fx)) fx.Stop(); };
            };
        }
    }

    static float YawTowards(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return Mathf.Atan2(-d.X, -d.Z);
    }

    void UpdateFlight(float t)
    {
        if (_missile == null || _duration <= 0f) return;
        float s = Math.Clamp(t / _duration, 0f, 1f);
        var pos = _from.Lerp(_to, s) + Vector3.Up * (4f * _apex * s * (1f - s));
        // Face along the velocity (derivative of the arc).
        var vel = (_to - _from) + Vector3.Up * (4f * _apex * (1f - 2f * s));
        _missile.GlobalPosition = pos;
        if (vel.LengthSquared() > 1e-6f)
            _missile.LookAt(pos + vel, Mathf.Abs(vel.Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up);
    }

    /// <summary>Preview: places the shot at <paramref name="time"/> seconds of flight and holds it.</summary>
    public void HoldAt(float time)
    {
        ProcessMode = ProcessModeEnum.Disabled;
        _t = time;
        UpdateFlight(time);
        if (_trail != null && IsInstanceValid(_trail)) { _trail.TimeScale = 0f; _trail.Seek(time); }
    }

    /// <summary>Total flight time in seconds (distance / speed).</summary>
    public float FlightTime => _duration;

    public override void _Process(double delta)
    {
        _t += (float)delta;
        UpdateFlight(_t);
        if (!_hit && _t >= _duration)
        {
            _hit = true;
            if (_missile != null && _missile != _trail?.GetParent()) _missile.Visible = false;
            _trail?.Stop();
            Strike();
        }
        if (_hit && (_trail == null || !IsInstanceValid(_trail) || _t > _duration + 3f))
            QueueFree();
    }

    void Strike()
    {
        _onHit?.Invoke();
        if (_shot.StrikeVisualPath != null && GetParent() is Node3D world)
            SpellVisualPlayer.Play(_shot.StrikeVisualPath, world, _to);
    }
}

/// <summary>Plays a (SpellVisual): its effect after HitDelay, its sound, and reports the hit.</summary>
public static class SpellVisualPlayer
{
    public static EffectPlayer? Play(string spellVisualXdb, Node3D world, Vector3 at, Action? onHit = null)
    {
        var sv = EffectAssets.Loader.LoadSpellVisual(spellVisualXdb);
        if (sv == null) { onHit?.Invoke(); return null; }
        if (sv.SoundPath != null) Audio.SoundLibrary.Play(sv.SoundPath, world);
        EffectPlayer? fx = sv.EffectPath != null ? EffectPlayer.PlayAt(sv.EffectPath, world, at) : null;
        if (onHit != null)
        {
            if (sv.HitDelay > 0f) world.GetTree().CreateTimer(sv.HitDelay).Timeout += onHit;
            else onHit();
        }
        return fx;
    }

    /// <summary>Plays the first visual of a spell (GameMechanics/Spell/... xdb) at a world point.</summary>
    public static EffectPlayer? PlaySpell(string spellXdb, Node3D world, Vector3 at, Action? onHit = null)
    {
        var visuals = EffectAssets.Loader.LoadSpellVisuals(spellXdb);
        if (visuals.Count == 0) { onHit?.Invoke(); return null; }
        return Play(visuals[0].Path, world, at, onHit);
    }
}
