using System.Text;
using System.Text.Json;
using TechInspector.Core.Catalog;
using TechInspector.Core.Ini;
using TechInspector.Core.Settings;
using Xunit;

namespace TechInspector.Core.Tests;

public class CatalogAndSessionTests
{
    private static readonly SettingsCatalog Catalog = SettingsCatalog.LoadEmbedded();

    private const string Renderer =
        "[Graphics Options]\r\n" +
        "LimitFrameRate=1                                \t; 0=no limit, 1=use DesiredFPSLimit\r\n" +
        "DesiredFPSLimit=100                             \t; fps\r\n" +
        "\r\n" +
        "[MonitorSetup]\r\n" +
        "ScreenWidth=2120                                \t; (mm) width of visible screen area\r\n" +
        "\r\n" +
        "[Brand New Section]\r\n" +
        "SomeToggle=1                                    \t; 0=off, 1=on\r\n";

    private const string App =
        "[Adaptive]\r\n" +
        "raceLineWidth=0.600000                  \t; Width of the race line in meters\r\n" +
        "\r\n" +
        "[SetupAutoLoad]\r\n" +
        "specracer=0\r\n";

    private static SettingsSession Session() => SettingsSession.FromDocuments("rendererDX11Monitor.ini", Catalog, new Dictionary<string, IniDocument>
    {
        ["rendererDX11Monitor.ini"] = IniDocument.Parse(Encoding.Latin1.GetBytes(Renderer)),
        ["app.ini"] = IniDocument.Parse(Encoding.Latin1.GetBytes(App)),
    });

    [Fact]
    public void The_embedded_catalog_loads()
    {
        Assert.Equal(623, Catalog.Settings.Count);
        Assert.Equal(8, Catalog.Areas.Count);
        Assert.Equal("Graphics and display", Catalog.Areas[0].Title);
        var fps = Catalog.Find("rendererDX11OpenXR.ini", "Graphics Options", "DesiredFPSLimit")!;   // any renderer file
        Assert.Equal("Frame rate cap", fps.Label);
        Assert.Equal("LimitFrameRate", fps.DependsOn!.Key);
        Assert.Equal("Screen geometry", Catalog.FindSection("rendererDX11Monitor.ini", "MonitorSetup")!.Name);
    }

    [Fact]
    public void Unknown_keys_get_a_control_from_the_comment_and_sit_in_their_area()
    {
        var s = Session();
        var toggle = s.Find("rendererDX11Monitor.ini", "Brand New Section", "SomeToggle")!;
        Assert.False(toggle.FromCatalog);
        Assert.Equal("bool", toggle.Def.Type);
        Assert.Equal("Some toggle", toggle.Label);
        Assert.Equal("Graphics and display", toggle.SectionInfo.Area);
    }

    [Fact]
    public void A_parent_switch_makes_its_child_inert()
    {
        var s = Session();
        var cap = s.Find("rendererDX11Monitor.ini", "Graphics Options", "DesiredFPSLimit")!;
        Assert.False(cap.IsInert);
        Assert.Null(s.SetDraft(s.Find("rendererDX11Monitor.ini", "Graphics Options", "LimitFrameRate")!, "0"));
        Assert.True(cap.IsInert);
        Assert.Equal("No effect: frame rate limit is off.", cap.InertMessage);
    }

    [Fact]
    public void Unusual_values_can_be_ignored_until_they_change()
    {
        var s = Session();
        var width = s.Find("rendererDX11Monitor.ini", "MonitorSetup", "ScreenWidth")!;
        Assert.True(width.IsUnusual);
        Assert.Equal("About three screens' width.", width.UnusualNote);
        s.Ignore(width);
        Assert.False(width.IsUnusual);
        s.SetDraft(width, "2200");
        Assert.True(width.IsUnusual);
        s.SetDraft(width, "700");
        Assert.False(width.IsUnusual);
    }

    [Fact]
    public void Settings_the_sim_manages_are_never_unusual()
    {
        var auto = Session().Find("app.ini", "SetupAutoLoad", "specracer")!;
        Assert.Equal("0", auto.Draft);
        Assert.False(auto.IsUnusual);
    }

    [Fact]
    public void Drafts_are_validated_clamped_and_written_in_the_files_style()
    {
        var s = Session();
        var cap = s.Find("rendererDX11Monitor.ini", "Graphics Options", "DesiredFPSLimit")!;
        Assert.Equal("Must be a number.", s.SetDraft(cap, "fast"));
        Assert.Null(s.SetDraft(cap, "9000"));
        Assert.Equal("500", cap.Draft);                  // clamped to the catalog's maximum
        Assert.True(cap.IsChanged);
        s.SetDraft(cap, "100.0");
        Assert.False(cap.IsChanged);                     // same number, no change

        var line = s.Find("app.ini", "Adaptive", "raceLineWidth")!;
        s.SetDraft(line, "0.8");
        Assert.Equal("0.800000", line.Draft);            // keeps the file's six decimals
    }

    [Fact]
    public void Values_show_as_people_read_them()
    {
        var s = Session();
        Assert.Equal("On", s.Find("rendererDX11Monitor.ini", "Graphics Options", "LimitFrameRate")!.Show("1"));
        Assert.Equal("100 fps", s.Find("rendererDX11Monitor.ini", "Graphics Options", "DesiredFPSLimit")!.Show("100"));
        Assert.Equal("2120 mm", s.Find("rendererDX11Monitor.ini", "MonitorSetup", "ScreenWidth")!.Show("2120"));
    }

    // ── The C# inference must match the prototype generator it was ported from. ──

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TechInspector.sln"))) return d.FullName;
        return null;
    }

    [Fact]
    public void Inference_matches_the_prototype_generator_for_every_key_on_this_PC()
    {
        var root = RepoRoot();
        var folder = IracingPaths.FindDocumentsFolder();
        var prototype = root == null ? null : Path.Combine(root, "prototype", "settingsdata.js");
        if (folder == null || prototype == null || !File.Exists(prototype)) return;   // the prototype isn't published
        var js = File.ReadAllText(prototype);
        var json = js.Substring(js.IndexOf('['), js.LastIndexOf(']') - js.IndexOf('[') + 1);
        using var data = JsonDocument.Parse(json);

        var docs = new Dictionary<string, IniDocument>();
        int compared = 0;
        var mismatches = new List<string>();
        foreach (var e in data.RootElement.EnumerateArray())
        {
            var file = e.GetProperty("file").GetString()!;
            if (!docs.TryGetValue(file, out var doc)) docs[file] = doc = IniDocument.Load(Path.Combine(folder, file));
            var sec = e.GetProperty("sec").GetString()!;
            var key = e.GetProperty("key").GetString()!;
            var line = doc.Find(sec, key);
            if (line == null || line.DisplayValue != e.GetProperty("raw").GetString()) continue;   // the file changed since the prototype was built
            var d = CommentInference.Infer(file, sec, key, line.DisplayValue, line.Comment);
            compared++;
            var want = e.GetProperty("type").GetString();
            if (d.Type != want) mismatches.Add($"{sec}.{key}: {d.Type} vs {want}");
            else if (want == "enum")
            {
                var wantOpts = e.GetProperty("opts").EnumerateArray().Select(o => o[0].GetInt32()).ToList();
                if (!wantOpts.SequenceEqual(d.Options!.Select(o => (int)o.Value))) mismatches.Add($"{sec}.{key}: options differ");
            }
        }
        Assert.True(compared > 500, $"only {compared} keys compared");
        Assert.Empty(mismatches);
    }
}
