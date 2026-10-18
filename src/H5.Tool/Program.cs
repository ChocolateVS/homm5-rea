using H5.Core.IO;

namespace H5.Tool;

/// <summary>
/// A developer command. Implementations are discovered by reflection, so a new
/// command is just a new file: add a class implementing ICommand.
/// </summary>
public interface ICommand
{
    string Name { get; }
    string Help { get; }
    int Run(ToolContext ctx, string[] args);
}

public sealed class ToolContext : IDisposable
{
    readonly Lazy<PakFileSystem> _fs;
    public GameInstall Install { get; }
    public PakFileSystem Fs => _fs.Value;

    public ToolContext(GameInstall install)
    {
        Install = install;
        _fs = new Lazy<PakFileSystem>(() => PakFileSystem.OpenInstall(install));
    }

    public void Dispose() { if (_fs.IsValueCreated) _fs.Value.Dispose(); }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var commands = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (ICommand)Activator.CreateInstance(t)!)
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        if (args.Length == 0 || !commands.TryGetValue(args[0], out var cmd))
        {
            Console.WriteLine("usage: h5tool <command> [args]  (set H5_GAME_DIR to override the install path)");
            foreach (var c in commands.Values.OrderBy(c => c.Name))
                Console.WriteLine($"  {c.Name,-24} {c.Help}");
            return args.Length == 0 ? 0 : 1;
        }

        using var ctx = new ToolContext(GameInstall.Locate());
        return cmd.Run(ctx, args[1..]);
    }
}

sealed class ExtractCommand : ICommand
{
    public string Name => "extract";
    public string Help => "extract <prefix> <outDir>  - copy archive entries under a path prefix to disk";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var outDir = Path.GetFullPath(args[1]);
        int n = 0;
        foreach (var p in ctx.Fs.Enumerate(args[0]))
        {
            var dst = Path.GetFullPath(Path.Combine(outDir, p));
            if (!dst.StartsWith(outDir, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, ctx.Fs.ReadAllBytes(p));
            n++;
        }
        Console.WriteLine($"extracted {n} files");
        return 0;
    }
}
