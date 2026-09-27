using System.Text;
using TechInspector.Core.Catalog;
using TechInspector.Core.Ini;
using TechInspector.Core.Settings;
using TechInspector.Core.Triples;
using Xunit;

namespace TechInspector.Core.Tests;

public class TripleTests
{
    private static readonly SettingsCatalog Catalog = SettingsCatalog.LoadEmbedded();

    [Fact]
    public void Ideal_side_angle_points_each_side_screen_at_the_eyes()
    {
        // A 32" 16:9 monitor: 708 mm visible, 724 mm with 8 mm bezels, eyes 640 mm away.
        var g = TripleGeometry.From(new TripleInputs { NumMonitors = 3, ScreenWidth = 708, MonitorWidth = 724, ViewingDist = 640, ScreenAngles = 59 });
        Assert.Equal(59, g.Ideal);                           // 2·atan(362/640) = 58.9°
        Assert.InRange(g.CentreFov, 57.8, 58.1);             // 2·atan(354/640)
        Assert.InRange(g.Fov, 170, 180);                     // at the ideal angle the outer edges end almost beside the eyes
        Assert.Equal(3, g.Panels.Count);
    }

    [Fact]
    public void Three_screens_worth_of_width_pushes_the_ideal_past_the_limit()
    {
        // This PC's file: 2120 mm per screen at 640 mm. The ideal would be 118°, so it clamps to 65.
        var g = TripleGeometry.From(new TripleInputs { NumMonitors = 3, ScreenWidth = 2120, MonitorWidth = 2120, ViewingDist = 640, ScreenAngles = 50 });
        Assert.True(g.IdealIsClamped);
        Assert.Equal(65, g.Ideal);
        Assert.True(g.Fov > 180);
    }

    [Fact]
    public void Single_screen_has_one_panel_and_its_own_fov()
    {
        var g = TripleGeometry.From(new TripleInputs { NumMonitors = 1, ScreenWidth = 800, MonitorWidth = 820, ViewingDist = 700, ScreenAngles = 50 });
        Assert.Single(g.Panels);
        Assert.Equal(g.CentreFov, g.Fov, 6);
    }

    [Fact]
    public void Screen_size_calculator_gives_the_visible_width()
    {
        Assert.Equal(708, TripleGeometry.VisibleWidthMm(32, 16, 9));
        Assert.Equal(598, TripleGeometry.VisibleWidthMm(27, 16, 9));
    }

    private static SettingsSession Session() => SettingsSession.FromDocuments(LayoutGuard.MonitorFile, Catalog, new Dictionary<string, IniDocument>
    {
        [LayoutGuard.MonitorFile] = IniDocument.Parse(Encoding.Latin1.GetBytes(
            "[Display]\r\nwindowedXPos=-2560                               \t; x\r\nwindowedWidth=7680                              \t; w\r\n" +
            "[MonitorSetup]\r\nNumMonitors=3                                   \t; 1 or 3\r\nScreenAngles=50                                 \t; deg\r\n")),
    });

    [Fact]
    public void The_guard_spots_what_iRacing_reset_and_stages_it_back()
    {
        var s = Session();
        var saved = LayoutGuard.Capture(s);
        Assert.Equal("-2560", saved["Display|windowedXPos"]);

        // iRacing resets the window to the centre screen on exit.
        var reset = SettingsSession.FromDocuments(LayoutGuard.MonitorFile, Catalog, new Dictionary<string, IniDocument>
        {
            [LayoutGuard.MonitorFile] = IniDocument.Parse(Encoding.Latin1.GetBytes(
                "[Display]\r\nwindowedXPos=0                                  \t; x\r\nwindowedWidth=3440                              \t; w\r\n" +
                "[MonitorSetup]\r\nNumMonitors=3                                   \t; 1 or 3\r\nScreenAngles=50                                 \t; deg\r\n")),
        });
        var diff = LayoutGuard.Differences(reset, saved);
        Assert.Equal(new[] { "Display|windowedXPos", "Display|windowedWidth" }, diff.Select(d => d.Key));
        Assert.Equal(2, LayoutGuard.StageRestore(reset, saved));
        Assert.Equal("-2560", reset.Find(LayoutGuard.MonitorFile, "Display", "windowedXPos")!.Draft);
        Assert.Empty(LayoutGuard.Differences(s, saved));
    }
}
