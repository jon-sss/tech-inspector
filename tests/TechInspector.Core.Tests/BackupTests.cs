using System.Text;
using TechInspector.Core.IO;
using Xunit;

namespace TechInspector.Core.Tests;

/// <summary>Snapshots, compare, pruning and import. Everything happens in a temp folder.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ti-bk-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_dir, "iRacing");
    private string G(string name) => Path.Combine(Game, name);

    public BackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "profiles", "hud"));
        File.WriteAllText(G("app.ini"), "[Graphics]\r\nfullScreen=1\r\nwidth=2560\r\n", Encoding.Latin1);
        File.WriteAllText(G("rendererDX11Monitor.ini"), "[Scene]\r\nFOV=72\r\n", Encoding.Latin1);
        File.WriteAllText(G("controls.cfg"), "binary-ish");
        File.WriteAllText(G(@"profiles\hud\layout.ini"), "[Box]\r\nx=1\r\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BackupStore Store() => new(Path.Combine(_dir, "backups"));

    [Fact]
    public void A_snapshot_holds_settings_controls_and_the_profiles_folder()
    {
        var files = IracingPaths.BackupFiles(Game);
        Assert.Contains(G("app.ini"), files);
        Assert.Contains(G("controls.cfg"), files);
        Assert.Contains(G(@"profiles\hud\layout.ini"), files);
        Assert.DoesNotContain(G("joyCalib.yaml"), files);   // not there, so not listed

        var b = Store().Create("Manual backup", files, baseFolder: Game, kind: BackupKind.Manual, name: "Before the season");
        Assert.True(File.Exists(Path.Combine(b.Folder, "profiles", "hud", "layout.ini")));
        Assert.Equal(@"profiles\hud\layout.ini", b.ShortName(G(@"profiles\hud\layout.ini")));
        Assert.Equal("Before the season", b.Name);
        Assert.Equal(BackupKind.Manual, b.Kind);
        Assert.Equal(1, b.Number);
    }

    [Fact]
    public void Restores_only_the_chosen_files_and_backs_them_up_first()
    {
        var store = Store();
        var b = store.Create("x", IracingPaths.BackupFiles(Game), baseFolder: Game);
        File.WriteAllText(G("app.ini"), "[Graphics]\r\nfullScreen=0\r\n");
        File.WriteAllText(G(@"profiles\hud\layout.ini"), "changed");

        var before = store.Restore(b, only: new[] { G(@"profiles\hud\layout.ini") });

        Assert.Equal("[Box]\r\nx=1\r\n", File.ReadAllText(G(@"profiles\hud\layout.ini")));
        Assert.Equal("[Graphics]\r\nfullScreen=0\r\n", File.ReadAllText(G("app.ini")));   // not chosen: untouched
        Assert.Equal(new[] { G(@"profiles\hud\layout.ini") }, before.Files);
        Assert.Equal("changed", File.ReadAllText(before.CopyOf(G(@"profiles\hud\layout.ini"))));
        Assert.Equal("Before restoring backup #1", before.Reason);
    }

    [Fact]
    public void Compare_lists_changed_settings_and_other_changed_files()
    {
        var b = Store().Create("x", IracingPaths.BackupFiles(Game), baseFolder: Game);
        File.WriteAllText(G("app.ini"), "; a comment now\r\n[Graphics]\r\nfullScreen=0\r\nwidth=2560.000\r\nheight=1440\r\n", Encoding.Latin1);
        File.WriteAllText(G("controls.cfg"), "rebound");
        File.Delete(G(@"profiles\hud\layout.ini"));

        var diff = BackupStore.Compare(b).ToDictionary(d => d.Name);

        var app = diff["app.ini"];
        Assert.Equal(FileState.Changed, app.State);
        Assert.Equal(2, app.Keys.Count);   // width 2560 = 2560.000 is no difference
        Assert.Contains(app.Keys, k => k.Key == "fullScreen" && k.Then == "1" && k.Now == "0");
        Assert.Contains(app.Keys, k => k.Key == "height" && k.Then == null && k.Now == "1440");
        Assert.Equal(FileState.Same, diff["rendererDX11Monitor.ini"].State);
        Assert.Equal(FileState.Changed, diff["controls.cfg"].State);
        Assert.Empty(diff["controls.cfg"].Keys);
        Assert.Equal(FileState.MissingNow, diff[@"profiles\hud\layout.ini"].State);
    }

    [Fact]
    public void Keeps_the_newest_automatic_backups_and_every_one_the_user_took()
    {
        var store = Store();
        var mine = store.Create("Manual backup", new[] { G("app.ini") }, DateTime.Now.AddDays(-60), Game, BackupKind.Manual);
        for (int i = 0; i < BackupStore.KeepAuto + 3; i++)
            store.Create("Before writing 1 change", new[] { G("app.ini") }, DateTime.Now.AddMinutes(-100 + i), Game);

        var all = store.List();
        Assert.Equal(BackupStore.KeepAuto, all.Count(b => b.Kind == BackupKind.Auto));
        Assert.Contains(all, b => b.Id == mine.Id);
        Assert.Equal(BackupStore.KeepAuto + 4, all.Max(b => b.Number));   // numbers keep counting after pruning
        Assert.Equal(all.Max(b => b.Number), all[0].Number);             // newest first
    }

    [Fact]
    public void Numbers_older_backups_that_have_none()
    {
        var root = Path.Combine(_dir, "backups");
        var old = Path.Combine(root, "20250101-000000-000");
        Directory.CreateDirectory(old);
        File.Copy(G("app.ini"), Path.Combine(old, "app.ini"));
        File.WriteAllText(Path.Combine(old, "backup.txt"), $"created=2025-01-01T00:00:00\r\nreason=Before writing 1 change\r\nfile={G("app.ini")}\r\n");

        var store = new BackupStore(root);
        var legacy = store.List().Single();
        Assert.Equal(1, legacy.Number);
        Assert.Equal(Path.Combine(old, "app.ini"), legacy.CopyOf(G("app.ini")));   // no base: flat copies
        Assert.Equal(2, store.Create("x", new[] { G("app.ini") }, baseFolder: Game).Number);
        Assert.Equal(1, store.List().Single(b => b.Id == legacy.Id).Number);
    }

    [Theory]
    [InlineData("rendererDX11Monitor - Copy.ini", "rendererDX11Monitor.ini")]
    [InlineData("rendererDX11Monitor - Copy (2).ini", "rendererDX11Monitor.ini")]
    [InlineData("rendererDX11Monitor.ini1", "rendererDX11Monitor.ini")]
    [InlineData("rendererDX11Monitor.ini4", "rendererDX11Monitor.ini")]
    [InlineData("rendererDX11_backup.ini", "rendererDX11.ini")]
    [InlineData("app_backup.ini", "app.ini")]
    [InlineData("app.ini.bak", "app.ini")]
    [InlineData("controls - Copy.cfg", "controls.cfg")]
    [InlineData("rendererDX11OpenXR (1).ini", "rendererDX11OpenXR.ini")]
    [InlineData("rendererDX11Monitor.ini", null)]
    [InlineData("rendererDX11.ini", null)]
    [InlineData("fueldata.ini", null)]
    [InlineData("app.ini.tmp", null)]
    [InlineData("rendererDX11MonitorX.ini", null)]
    public void Recognises_hand_made_copies(string name, string original) =>
        Assert.Equal(original, StrayCopy.OriginalOf(name));

    [Fact]
    public void Imports_a_copy_as_a_backup_of_its_original_once()
    {
        var copy = G("rendererDX11Monitor - Copy (2).ini");
        File.WriteAllText(copy, "[Scene]\r\nFOV=65\r\n");
        var dated = new DateTime(2025, 11, 27, 5, 8, 59);
        File.SetLastWriteTime(copy, dated);
        var store = Store();

        var found = StrayCopy.Find(Game, store.ImportedSources());
        var c = Assert.Single(found);
        Assert.Equal("rendererDX11Monitor.ini", c.OriginalName);

        var b = store.Import(c, Game);
        Assert.Equal(BackupKind.Imported, b.Kind);
        Assert.Equal(dated, b.Created);
        Assert.Equal(new[] { G("rendererDX11Monitor.ini") }, b.Files);
        Assert.Equal("[Scene]\r\nFOV=65\r\n", File.ReadAllText(b.CopyOf(G("rendererDX11Monitor.ini"))));
        Assert.True(File.Exists(copy));   // the copy stays where it was
        Assert.Empty(StrayCopy.Find(Game, store.ImportedSources()));

        // Restoring puts the copy's content back as the real file.
        store.Restore(b);
        Assert.Equal("[Scene]\r\nFOV=65\r\n", File.ReadAllText(G("rendererDX11Monitor.ini")));
    }

    [Fact]
    public void Finds_the_copies_in_the_live_folder_without_touching_it()
    {
        var live = IracingPaths.FindDocumentsFolder();
        if (live == null) return;
        var found = StrayCopy.Find(live);
        Assert.All(found, c => Assert.Contains(c.OriginalName, IracingPaths.CopyableFiles));
        Assert.DoesNotContain(found, c => IracingPaths.CopyableFiles.Contains(c.FileName, StringComparer.OrdinalIgnoreCase));
    }
}
