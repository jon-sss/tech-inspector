using TechInspector.Core.Catalog;
using TechInspector.Core.Settings;
using Xunit;

namespace TechInspector.Core.Tests;

public class GoalTests
{
    private static readonly SettingsCatalog Catalog = SettingsCatalog.LoadEmbedded();
    private static readonly GoalList Goals = GoalList.LoadEmbedded();

    [Fact]
    public void Every_goal_names_settings_the_catalog_describes()
    {
        Assert.NotEmpty(Goals.Goals);
        foreach (var g in Goals.Goals)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            Assert.False(string.IsNullOrWhiteSpace(g.Summary));
            if (g.Tab != null) { Assert.Empty(g.Settings); continue; }
            Assert.InRange(g.Settings.Count, 3, 7);
            foreach (var id in g.Settings)
            {
                var p = id.Split('|');
                Assert.Equal(3, p.Length);
                Assert.True(Catalog.Find(p[0], p[1], p[2]) != null, $"{g.Id}: {id} is not in the catalog");
            }
        }
        Assert.Equal(Goals.Goals.Count, Goals.Goals.Select(g => g.Id).Distinct().Count());
    }

    [Fact]
    public void Goals_resolve_against_the_open_graphics_file()
    {
        var folder = IracingPaths.FindDocumentsFolder();
        if (folder == null) return;
        var session = SettingsSession.Load(folder, "rendererDX11Monitor.ini", Catalog);
        var fps = Goals.Goals.Single(g => g.Id == "frame-rate").Resolve(session);
        Assert.Contains(fps, s => s.Key == "LimitFrameRate" && s.FileName == "rendererDX11Monitor.ini");
        Assert.Contains(fps, s => s.FileName == "app.ini");
    }
}
