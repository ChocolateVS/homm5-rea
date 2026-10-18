using H5.Core.Ui;
using H5.Core.Xdb;

namespace H5.Tool.Commands;

/// <summary>
/// `h5tool ui-commands &lt;screen.xdb|prefix&gt;` lists every button state's commands per trigger
/// (click = OnEnterState, right = CommandsOnRightClick, dbl = commandsOnLDblKlick) and each
/// window's tooltip, so client handlers can be audited against what the original UI sends.
/// With a prefix (no ".xdb"), every (WindowScreen) under it is scanned and only the distinct
/// command names per trigger are printed.
/// </summary>
sealed class UiCommandsCommand : ICommand
{
    public string Name => "ui-commands";
    public string Help => "ui-commands <screen.xdb|prefix>  - button commands per trigger (click/right/dbl) + tooltips";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var loader = new UiLoader(new XdbLoader(ctx.Fs)) { LoadTexts = false };
        if (args[0].EndsWith(".xdb", StringComparison.OrdinalIgnoreCase))
        {
            Dump(loader.Load(args[0]), 0);
            return 0;
        }
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in ctx.Fs.Enumerate(args[0]).Where(p => p.Contains("(WindowScreen)", StringComparison.OrdinalIgnoreCase)).Order())
        {
            UiWindow w;
            try { w = loader.Load(path); }
            catch (Exception e) { Console.WriteLine($"! {path}: {e.Message}"); continue; }
            var local = new SortedSet<string>(StringComparer.Ordinal);
            Collect(w, local);
            if (local.Count == 0) continue;
            Console.WriteLine(path);
            foreach (var s in local) { Console.WriteLine("  " + s); distinct.Add(s); }
        }
        Console.WriteLine($"== {distinct.Count} distinct trigger:command pairs");
        return 0;
    }

    static void Collect(UiWindow w, SortedSet<string> into)
    {
        foreach (var st in w.ButtonStates)
        {
            foreach (var c in st.OnEnter) into.Add($"click:{c.Kind}:{c.Argument}");
            foreach (var c in st.OnRightClick) into.Add($"right:{c.Kind}:{c.Argument}");
            foreach (var c in st.OnDoubleClick) into.Add($"dbl:{c.Kind}:{c.Argument}");
        }
        foreach (var c in w.Children) Collect(c, into);
    }

    static void Dump(UiWindow w, int depth)
    {
        string Cmds(List<UiCommand> l) => string.Join(",", l.Select(c => $"{c.Kind}:{c.Argument}"));
        var parts = new List<string>();
        for (int i = 0; i < w.ButtonStates.Count; i++)
        {
            var st = w.ButtonStates[i];
            var s = new List<string>();
            if (st.OnEnter.Count > 0) s.Add("click=" + Cmds(st.OnEnter));
            if (st.OnRightClick.Count > 0) s.Add("right=" + Cmds(st.OnRightClick));
            if (st.OnDoubleClick.Count > 0) s.Add("dbl=" + Cmds(st.OnDoubleClick));
            if (s.Count > 0) parts.Add($"[{i}:{st.Name}] " + string.Join(" ", s));
        }
        if (parts.Count > 0 || w.TooltipPath != null)
            Console.WriteLine($"{new string(' ', depth * 2)}{w.Type} '{w.Name}'{(w.TooltipPath != null ? $" tip={w.TooltipPath}" : "")} {string.Join(" | ", parts)}");
        foreach (var c in w.Children) Dump(c, depth + 1);
    }
}
