using Godot;
using H5.AdvNet;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The turn timer of a local game with a time limit (view vtable 00e29d5c, made by 0060ecf0 from the adventure
/// screen 00598f90 only when the world's limit is set): TIME_LIMIT_TIMER_WINDOW (UIGameRoot →
/// UI/AdventureScreen/TurnTimeNotify/Timer) with TimeView's time_min / time_sec (0060edf0), and the
/// TurnTimeLimitMsgs of UI/uiconsts (Time, MsgFileRef, FadeParams) as TurnTimeNotify fade messages (0060e8f0).
/// </summary>
public partial class TurnTimerView : Control
{
    const string Consts = "/UI/uiconsts.(UIGameConstsH5).xdb";

    public TurnTimer Timer { get; }
    readonly UiScreen _timer;
    readonly List<(int Time, string? Text, int In, int Stable, int Out)> _msgs = new();
    readonly List<int> _times = new();
    Control? _fade;

    public TurnTimerView(int minutes)
    {
        Name = "TurnTimer";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        Timer = new TurnTimer(minutes);
        _timer = new UiScreen("UI/AdventureScreen/TurnTimeNotify/Timer.(WindowSimple).xdb");
        AddChild(_timer);
        LoadMessages();
    }

    void LoadMessages()
    {
        var root = Game.Instance.Xdb.Load(Consts).Root;
        foreach (var it in root?.Element("TurnTimeLimitMsgs")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            int time = (int)Math.Round(double.Parse((string?)it.Element("Time") ?? "0", System.Globalization.CultureInfo.InvariantCulture));
            var msgHref = (string?)it.Element("MsgFileRef")?.Attribute("href");
            string? text = string.IsNullOrEmpty(msgHref) ? null : UiResources.Instance.Text(XdbRef.Parse(msgHref, Consts).Path);
            int fin = 0, stable = 0, fout = 0;
            var fadeHref = (string?)it.Element("FadeParams")?.Attribute("href");
            if (!string.IsNullOrEmpty(fadeHref) && Game.Instance.Xdb.Load(XdbRef.Parse(fadeHref, Consts).Path).Root is { } f)
            {
                fin = (int?)f.Element("FadeInTime") ?? 0;
                stable = (int?)f.Element("StableTime") ?? 0;
                fout = (int?)f.Element("FadeOutTime") ?? 0;
            }
            _msgs.Add((time, text, fin, stable, fout));
            _times.Add(time);
        }
    }

    static long Now => (long)Godot.Time.GetTicksMsec();

    /// <summary>0060ec30: the view follows a new current player.</summary>
    public void NewTurn() => Timer.NewTurn(Now);

    /// <summary>CCloseCombat (004e0f10): the combat's duration does not count against the turn.</summary>
    public void AddCombat(long ms) => Timer.AddCombat(ms);

    public bool Expired => Timer.Expired(Now);

    /// <summary>0060edf0 per frame while a human is to move; the window is hidden otherwise.</summary>
    public void Update(bool humanTurn)
    {
        _timer.Visible = humanTurn;
        if (!humanTurn) return;
        long left = Timer.TimeLeft(Now);
        var (min, sec, _) = TurnTimer.Display(left);
        if (_timer.Find("TimeView") is { } tv)
        {
            tv.SetValue("time_min", min);
            tv.SetValue("time_sec", sec);
        }
        int i = Timer.NextMessage(left, _times);
        if (i >= 0 && _msgs[i].Text is { } text) ShowFade(text, _msgs[i].In, _msgs[i].Stable, _msgs[i].Out);
    }

    /// <summary>The FadeParams window (TurnTimeNotify) with its TextView value; a newer message replaces the last.</summary>
    void ShowFade(string text, int fadeIn, int stable, int fadeOut)
    {
        if (_fade != null && IsInstanceValid(_fade)) _fade.QueueFree();
        var ui = new UiScreen("UI/AdventureScreen/TurnTimeNotify/TurnTimeNotify.(WindowSimple).xdb");
        ui.Find("TextView")?.SetValue("value", text);
        AddChild(ui);
        _fade = ui;
        ui.Modulate = new Color(1, 1, 1, fadeIn > 0 ? 0 : 1);
        var tw = ui.CreateTween();
        if (fadeIn > 0) tw.TweenProperty(ui, "modulate:a", 1f, fadeIn / 1000.0);
        tw.TweenInterval(Math.Max(stable, 1) / 1000.0);
        if (fadeOut > 0) tw.TweenProperty(ui, "modulate:a", 0f, fadeOut / 1000.0);
        tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(ui)) ui.QueueFree(); }));
    }
}
