using System.Text;
using TechInspector.Core.Ffb;
using Xunit;

namespace TechInspector.Core.Tests;

public class FfbTests
{
    // ── key bindings in controls.cfg ──

    /// <summary>A controls.cfg with one action, laid out as iRacing writes it.</summary>
    private static byte[] Cfg(string action, int device, int key, byte mods, string before = "\0\0\0\0")
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes("GFCC"));
        b.AddRange(BitConverter.GetBytes(20));
        b.AddRange(new byte[40]);
        b.AddRange(Encoding.ASCII.GetBytes(before + action + "\0"));
        b.AddRange(BitConverter.GetBytes(1));
        b.AddRange(BitConverter.GetBytes(6));
        b.AddRange(BitConverter.GetBytes(device));
        b.AddRange(BitConverter.GetBytes(key));
        b.AddRange(new byte[] { 0, 0, mods, 0 });
        b.AddRange(new byte[60]);
        return b.ToArray();
    }

    [Fact]
    public void Reads_a_keyboard_binding_with_modifiers()
    {
        var k = ControlBindings.Find(Cfg("IncFFB", 4, 0x27, 0x0C), ControlBindings.IncreaseFfb);
        Assert.NotNull(k);
        Assert.Equal(0x27, k.Key);
        Assert.True(k.Ctrl);
        Assert.False(k.Shift);
        Assert.False(k.Alt);
        Assert.True(k.CanPress);
        Assert.Equal("Ctrl+Right", k.ToString());
        Assert.Equal("Ctrl+Alt+Shift+S", ControlBindings.Find(Cfg("X", 4, 0x53, 0x3F), "X").ToString());
    }

    [Fact]
    public void Unbound_wheel_and_unknown_bindings_read_as_nothing_or_unpressable()
    {
        Assert.Null(ControlBindings.Find(Cfg("IncFFB", 0, 0, 0), ControlBindings.IncreaseFfb));       // unbound
        Assert.Null(ControlBindings.Find(Cfg("IncFFB", 2, 0x1F, 0), ControlBindings.IncreaseFfb));    // a wheel button
        Assert.Null(ControlBindings.Find(Cfg("IncFFB", 4, 0x41, 0), ControlBindings.DecreaseFfb));    // another action
        Assert.Null(ControlBindings.Find(Cfg("MyIncFFB", 4, 0x41, 0, before: "\0"), ControlBindings.IncreaseFfb));
        Assert.False(ControlBindings.Find(Cfg("IncFFB", 4, 0xC5, 0), ControlBindings.IncreaseFfb).CanPress);   // iRacing's F1
        Assert.Null(ControlBindings.Find(new byte[] { 1, 2, 3 }, ControlBindings.IncreaseFfb));
    }

    [Fact]
    public void Uses_the_control_profile_named_in_app_ini()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ti-ffb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            Assert.Equal(Path.Combine(folder, "controls.cfg"), ControlBindings.ActivePath(folder));
            File.WriteAllText(Path.Combine(folder, "app.ini"), "[ControlProfiles]\r\nGlobal=Baseline\r\n");
            Assert.Equal(Path.Combine(folder, "controls.cfg"), ControlBindings.ActivePath(folder));   // no such profile yet
            var profile = Path.Combine(folder, "profiles", "controls", "Baseline");
            Directory.CreateDirectory(profile);
            File.WriteAllBytes(Path.Combine(profile, "controls.cfg"), Cfg("IncFFB", 4, 0x41, 0));
            Assert.Equal(Path.Combine(profile, "controls.cfg"), ControlBindings.ActivePath(folder));
            Assert.Equal("A", ControlBindings.Find(ControlBindings.Read(ControlBindings.ActivePath(folder)), "IncFFB").ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>This PC's controls.cfg, read-only: iRacing binds Auto FFB to Ctrl+A out of the box. Skipped without iRacing.</summary>
    [Fact]
    public void Reads_the_live_controls_cfg()
    {
        var folder = IracingPaths.FindDocumentsFolder();
        var cfg = folder == null ? null : ControlBindings.Read(ControlBindings.ActivePath(folder));
        if (cfg == null) return;
        var auto = ControlBindings.Find(cfg, "AutoFFB");
        if (auto != null) Assert.Equal("Ctrl+A", auto.ToString());
    }
}
