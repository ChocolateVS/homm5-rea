using System.IO.Compression;
using H5.Assets.Scenes;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Tests;

/// <summary>
/// User campaigns: H5_Game.exe 00c8bd00 mounts <c>UserCampaigns\*.h5c</c> with the user archives (after the duel
/// presets, before <c>UserMODs\*.h5u</c>), and 009d2ad0 reads every <c>#xpointer(/Campaign)</c> under UserCampaigns.
/// </summary>
public class UserCampaignTests
{
    [Fact]
    public void H5cArchivesAreMountedBeforeTheMods()
    {
        var root = Directory.CreateTempSubdirectory("h5c").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "UserMODs"));
            Directory.CreateDirectory(Path.Combine(root, "UserCampaigns"));
            File.WriteAllText(Path.Combine(root, "data", "data.pak"), "");
            File.WriteAllText(Path.Combine(root, "UserMODs", "m.h5u"), "");
            File.WriteAllText(Path.Combine(root, "UserCampaigns", "b.h5c"), "");
            File.WriteAllText(Path.Combine(root, "UserCampaigns", "a.h5c"), "");
            var order = PakLoadOrder.Enumerate(Path.Combine(root, "data"), Path.Combine(root, "UserMODs"), Path.Combine(root, "UserCampaigns"))
                .Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "data.pak", "a.h5c", "b.h5c", "m.h5u" }, order);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CampaignsInsideAMountedH5cAreListed()
    {
        var dir = Directory.CreateTempSubdirectory("h5c").FullName;
        var path = Path.Combine(dir, "Mine.h5c");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
                Put("UserCampaigns/Mine.xdb", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Campaign><InternalName>MINE</InternalName>" +
                    "<Missions><Item><MissionTag href=\"/Maps/Scenario/MINE1/map-tag.xdb#xpointer(/AdvMapDescTag)\"/></Item></Missions></Campaign>");
                Put("UserCampaigns/Other.xdb", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><CampaignsSet/>");
            }
            using var fs = new PakFileSystem();
            fs.Mount(path);
            var list = CampaignLoader.LoadUserCampaigns(new XdbLoader(fs));
            var c = Assert.Single(list);
            Assert.Equal("MINE", c.InternalName);
            Assert.Equal("MINE1", Assert.Single(c.Missions).MapId);
        }
        finally { Directory.Delete(dir, true); }
    }
}
