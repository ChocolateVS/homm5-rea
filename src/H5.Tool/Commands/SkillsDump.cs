namespace H5.Tool.Commands;

sealed class SkillsCommand : ICommand
{
    public string Name => "skills";
    public string Help => "skills  - list hero skills/perks with names and types";

    public int Run(ToolContext ctx, string[] args)
    {
        foreach (var s in Db.Get(ctx).Skills.Values)
            Console.WriteLine($"{s.Id,-40} {s.SkillType,-24} {s.BasicSkillId,-30} {s.Name}");
        return 0;
    }
}
