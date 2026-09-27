using System.Text;
using TechInspector.Core.Catalog;
using TechInspector.Core.IO;
using TechInspector.Core.Settings;
using Xunit;

namespace TechInspector.Core.Tests;

/// <summary>Writes go to a temp copy of an iRacing folder, never the real one.</summary>
public sealed class WriteTests : IDisposable
{
    private static readonly SettingsCatalog Catalog = SettingsCatalog.LoadEmbedded();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ti-write-" + Guid.NewGuid().ToString("N"));
    private string Ini => Path.Combine(_dir, "iRacing");
    private string Backups => Path.Combine(_dir, "backups");

    private const string Renderer =
        "[Graphics Options]\r\n" +
        "LimitFrameRate=1                                \t; 0=no limit, 1=use DesiredFPSLimit\r\n" +
        "DesiredFPSLimit=100                             \t; fps\r\n";
    private const string App =
        "[Audio]\r\n" +
        "loudnessEngine=0                        \t; Volume adjustment for engines in dB\r\n" +
        "loudnessTires=0                         \t; Volume adjustment for tires in dB\r\n";

    public WriteTests()
    {
        Directory.CreateDirectory(Ini);
        File.WriteAllBytes(Path.Combine(Ini, "rendererDX11Monitor.ini"), Encoding.Latin1.GetBytes(Renderer));
        File.WriteAllBytes(Path.Combine(Ini, "app.ini"), Encoding.Latin1.GetBytes(App));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Read(string name) => Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Ini, name)));
    private SettingsSession Load() => SettingsSession.Load(Ini, "rendererDX11Monitor.ini", Catalog);

    [Fact]
    public void Writes_only_the_changed_lines_after_a_backup()
    {
        var s = Load();
        s.SetDraft(s.Find("rendererDX11Monitor.ini", "Graphics Options", "DesiredFPSLimit")!, "144");
        var result = s.Write(new BackupStore(Backups));

        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "rendererDX11Monitor.ini" }, result.Files);
        Assert.Equal(Renderer.Replace("DesiredFPSLimit=100 ", "DesiredFPSLimit=144 "), Read("rendererDX11Monitor.ini"));
        Assert.Equal(App, Read("app.ini"));                                  // untouched
        Assert.Equal(Renderer, File.ReadAllText(Path.Combine(result.Backup.Folder, "rendererDX11Monitor.ini"), Encoding.Latin1));
    }

    [Fact]
    public void Keeps_what_iRacing_wrote_after_the_page_loaded()
    {
        var s = Load();
        s.SetDraft(s.Find("app.ini", "Audio", "loudnessEngine")!, "-6");
        // iRacing exits and rewrites app.ini with a new tyre volume while the change waits.
        File.WriteAllText(Path.Combine(Ini, "app.ini"), App.Replace("loudnessTires=0 ", "loudnessTires=3 "), Encoding.Latin1);

        s.Write(new BackupStore(Backups));

        var app = Read("app.ini");
        Assert.Contains("loudnessEngine=-6 ", app);
        Assert.Contains("loudnessTires=3 ", app);
    }

    [Fact]
    public void Undo_restores_the_files_exactly()
    {
        var s = Load();
        s.SetDraft(s.Find("app.ini", "Audio", "loudnessEngine")!, "-6");
        s.SetDraft(s.Find("rendererDX11Monitor.ini", "Graphics Options", "LimitFrameRate")!, "0");
        var store = new BackupStore(Backups);
        var result = s.Write(store);
        Assert.Equal(2, result.Files.Count);

        store.Restore(result.Backup);
        Assert.Equal(App, Read("app.ini"));
        Assert.Equal(Renderer, Read("rendererDX11Monitor.ini"));
    }

    [Fact]
    public void Refuses_to_write_while_iRacing_runs()
    {
        var s = Load();
        s.SetDraft(s.Find("app.ini", "Audio", "loudnessEngine")!, "-6");
        Assert.Throws<InvalidOperationException>(() => s.Write(new BackupStore(Backups), simRunning: () => true));
        Assert.Equal(App, Read("app.ini"));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void A_key_iRacing_removed_is_skipped_not_added()
    {
        var s = Load();
        s.SetDraft(s.Find("app.ini", "Audio", "loudnessTires")!, "2");
        File.WriteAllText(Path.Combine(Ini, "app.ini"), "[Audio]\r\nloudnessEngine=0                        \t; Volume adjustment for engines in dB\r\n", Encoding.Latin1);

        var result = s.Write(new BackupStore(Backups));
        Assert.Single(result.Skipped);
        Assert.DoesNotContain("loudnessTires", Read("app.ini"));
    }
}
