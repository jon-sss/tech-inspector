using TechInspector.Core.IO;
using Xunit;

namespace TechInspector.Core.Tests;

/// <summary>All writes here go to a fresh temp folder, never to iRacing's folder.</summary>
public sealed class SafeWriteAndBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ti-tests-" + Guid.NewGuid().ToString("N"));

    public SafeWriteAndBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Writes_a_new_file_and_replaces_an_existing_one_without_leftovers()
    {
        SafeFileWriter.WriteAtomic(P("a.ini"), new byte[] { 1, 2, 3 });
        SafeFileWriter.WriteAtomic(P("a.ini"), new byte[] { 4, 5 });
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(P("a.ini")));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Refuses_a_read_only_file_and_leaves_it_untouched()
    {
        File.WriteAllBytes(P("ro.ini"), new byte[] { 7 });
        File.SetAttributes(P("ro.ini"), FileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => SafeFileWriter.WriteAtomic(P("ro.ini"), new byte[] { 8 }));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(P("ro.ini")));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Backs_up_lists_and_restores_with_an_undo_backup()
    {
        var ini = P("app.ini");
        File.WriteAllText(ini, "v1");
        var store = new BackupStore(P("backups"));

        var b1 = store.Create("Before writing 1 change", new[] { ini, P("missing.ini") }, DateTime.Now.AddHours(-1));
        Assert.Equal(new[] { ini }, b1.Files);
        Assert.Equal("Before writing 1 change", b1.Reason);

        File.WriteAllText(ini, "v2");
        var undo = store.Restore(b1);

        Assert.Equal("v1", File.ReadAllText(ini));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(undo.Folder, "app.ini")));
        Assert.Equal(2, store.List().Count);
        Assert.Equal(undo.Id, store.List()[0].Id);   // newest first
    }
}
