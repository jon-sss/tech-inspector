using TechInspector.Core.Ini;
using Xunit;

namespace TechInspector.Core.Tests;

/// <summary>
/// Checks against the real files in this PC's iRacing folder. Read-only: files are loaded into
/// memory and nothing is written back. Skipped quietly on machines without iRacing.
/// </summary>
public class LiveFileTests
{
    public static IEnumerable<object[]> LiveFiles()
    {
        var folder = IracingPaths.FindDocumentsFolder();
        // xunit fails a theory with no rows, so machines without iRacing get one empty row that skips.
        if (folder == null) { yield return new object[] { null! }; yield break; }
        foreach (var f in Directory.GetFiles(folder, "*.ini").OrderBy(f => f))
            yield return new object[] { Path.GetFileName(f) };
    }

    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(IracingPaths.FindDocumentsFolder()!, name));

    [Theory]
    [MemberData(nameof(LiveFiles))]
    public void Every_ini_file_round_trips_byte_for_byte(string name)
    {
        if (name == null) return;
        var bytes = Read(name);
        Assert.Equal(bytes, IniDocument.Parse(bytes).ToBytes());
    }

    /// <summary>
    /// Rebuilds every entry from its key, value and comment. If the writer's layout rules differ
    /// from iRacing's anywhere, this is where it shows.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveFiles))]
    public void Rebuilding_every_line_reproduces_iRacings_layout(string name)
    {
        if (name == null) return;
        var bytes = Read(name);
        var doc = IniDocument.Parse(bytes);
        doc.RebuildAllEntries();
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void Finds_the_settings_files()
    {
        var folder = IracingPaths.FindDocumentsFolder();
        if (folder == null) return;
        var files = IracingPaths.SettingsFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Contains("app.ini", files);
        Assert.Contains("core.ini", files);
    }
}
