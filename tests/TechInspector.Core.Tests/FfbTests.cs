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

    // ── per-car memory ──

    private static readonly DateTime T0 = new DateTime(2026, 11, 3, 20, 0, 0);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private sealed class Rig
    {
        public readonly Dictionary<string, float> Saved = new(StringComparer.OrdinalIgnoreCase);
        public readonly StrengthMemory Memory;
        public readonly List<StrengthAction> All = new();
        public Rig() => Memory = new StrengthMemory(c => Saved.TryGetValue(c, out var v) ? v : null);

        public IReadOnlyList<StrengthAction> Step(double s, string car, float? nm, bool onTrack = true)
        {
            var actions = Memory.Step(car, car?.ToUpperInvariant(), nm, onTrack, At(s));
            foreach (var a in actions) if (a.Kind == StrengthActionKind.Store) Saved[a.Car] = a.Nm;
            All.AddRange(actions);
            return actions;
        }

        public IEnumerable<StrengthAction> Of(StrengthActionKind kind) => All.Where(a => a.Kind == kind);
    }

    [Fact]
    public void Puts_a_cars_saved_strength_back_when_it_loads()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        Assert.Empty(rig.Step(0, "mx5", 6.0f, onTrack: false));   // in the garage: iRacing ignores the keys there
        var a = rig.Step(0.25, "mx5", 6.0f);
        var apply = Assert.Single(a);
        Assert.Equal(StrengthActionKind.Apply, apply.Kind);
        Assert.Equal(9.2f, apply.Nm);
        Assert.True(rig.Memory.Applying);
        Assert.Equal(9.2f, rig.Memory.Target);

        // The key presses walk the strength over; none of the steps is the user's change.
        for (float nm = 6.5f; nm < 9.2f; nm += 0.5f) rig.Step(1 + nm, "mx5", nm);
        rig.Step(11, "mx5", 9.2f);
        Assert.True(rig.Memory.Applying);
        rig.Memory.EndApply();
        Assert.False(rig.Memory.Applying);
        for (double s = 11.25; s < 20; s += 0.25) rig.Step(s, "mx5", 9.2f);
        Assert.Empty(rig.Of(StrengthActionKind.Store));
        Assert.Single(rig.Of(StrengthActionKind.Apply));
    }

    [Fact]
    public void Changes_while_the_car_loads_are_iRacings_not_the_users()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        rig.Step(0, "mx5", 6.0f, onTrack: false);
        rig.Step(1, "mx5", 7.0f, onTrack: false);
        rig.Step(5, "mx5", 7.0f, onTrack: false);
        Assert.Empty(rig.All);
        Assert.Equal(9.2f, Assert.Single(rig.Step(6, "mx5", 7.0f)).Nm);
    }

    [Fact]
    public void A_failed_set_is_tried_again_the_next_time_in_the_car()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        rig.Step(0, "mx5", 6.0f);
        rig.Memory.EndApply(retry: true);
        rig.Step(1, "mx5", 6.0f);
        rig.Step(5, "mx5", 6.0f);
        Assert.Single(rig.Of(StrengthActionKind.Apply));       // not while still in the car
        rig.Step(6, "mx5", 6.0f, onTrack: false);              // out
        Assert.Empty(rig.Of(StrengthActionKind.Store));        // 6.0 isn't the user's choice
        rig.Step(7, "mx5", 6.0f);                              // and back in
        Assert.Equal(2, rig.Of(StrengthActionKind.Apply).Count());
    }

    [Fact]
    public void A_change_by_hand_after_a_failed_set_wins()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        rig.Step(0, "mx5", 6.0f);
        rig.Memory.EndApply(retry: true);
        rig.Step(1, "mx5", 8.0f);
        rig.Step(4, "mx5", 8.0f);
        Assert.Equal(8.0f, rig.Saved["mx5"]);
        rig.Step(5, "mx5", 8.0f, onTrack: false);
        rig.Step(6, "mx5", 8.0f);
        Assert.Single(rig.Of(StrengthActionKind.Apply));
    }

    [Fact]
    public void After_a_set_ends_short_of_the_target_that_value_is_not_saved()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        rig.Step(0, "mx5", 6.0f);
        rig.Step(1, "mx5", 9.0f);   // the closest step the keys reach
        rig.Memory.EndApply();
        for (double s = 1.25; s < 10; s += 0.25) rig.Step(s, "mx5", 9.0f);
        Assert.Empty(rig.Of(StrengthActionKind.Store));
        Assert.Equal(9.2f, rig.Saved["mx5"]);
        rig.Step(11, "mx5", 9.5f);  // then the user changes it
        rig.Step(14, "mx5", 9.5f);
        Assert.Equal(9.5f, rig.Saved["mx5"]);
    }

    [Fact]
    public void No_set_when_the_sim_already_has_the_saved_strength()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        Assert.Empty(rig.Step(0, "mx5", 9.23f));
        Assert.False(rig.Memory.Applying);
    }

    [Fact]
    public void Waits_for_the_first_strength_before_deciding()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        Assert.Empty(rig.Step(0, "mx5", null));
        Assert.Equal(StrengthActionKind.Apply, Assert.Single(rig.Step(0.25, "mx5", 6.0f)).Kind);
    }

    [Fact]
    public void A_set_that_never_ends_times_out()
    {
        var rig = new Rig();
        rig.Saved["mx5"] = 9.2f;
        rig.Step(0, "mx5", 6.0f);
        rig.Step(StrengthMemory.ApplyTimeout.TotalSeconds - 1, "mx5", 6.0f);
        Assert.True(rig.Memory.Applying);
        rig.Step(StrengthMemory.ApplyTimeout.TotalSeconds + 1, "mx5", 6.0f);
        Assert.False(rig.Memory.Applying);
        Assert.Empty(rig.Of(StrengthActionKind.Store));
        Assert.Equal(9.2f, rig.Saved["mx5"]);
    }

    [Fact]
    public void Saves_a_change_once_it_settles()
    {
        var rig = new Rig();
        rig.Saved["gt3"] = 7.0f;
        rig.Step(0, "gt3", 7.0f);
        // A held key: the strength climbs for a second, then holds.
        float nm = 7.0f;
        for (double s = 0.25; s <= 1.0; s += 0.25) rig.Step(s, "gt3", nm += 0.2f);
        Assert.Empty(rig.Of(StrengthActionKind.Store));
        rig.Step(2.0, "gt3", nm);
        Assert.Empty(rig.Of(StrengthActionKind.Store));
        rig.Step(3.0, "gt3", nm);
        var store = Assert.Single(rig.Of(StrengthActionKind.Store));
        Assert.Equal("gt3", store.Car);
        Assert.Equal("GT3", store.Name);
        Assert.Equal(nm, store.Nm);
        rig.Step(10, "gt3", nm);
        Assert.Single(rig.Of(StrengthActionKind.Store));
    }

    [Fact]
    public void Exiting_the_car_saves_an_unsettled_change()
    {
        var rig = new Rig();
        rig.Saved["gt3"] = 7.0f;
        rig.Step(0, "gt3", 7.0f);
        rig.Step(0.25, "gt3", 8.0f);
        var store = Assert.Single(rig.Step(0.5, "gt3", 8.0f, onTrack: false));
        Assert.Equal(StrengthActionKind.Store, store.Kind);
        Assert.Equal(8.0f, store.Nm);
        rig.Step(5, "gt3", 8.0f, onTrack: false);
        Assert.Single(rig.Of(StrengthActionKind.Store));
    }

    [Fact]
    public void A_new_car_is_saved_when_exited_even_unchanged()
    {
        var rig = new Rig();
        rig.Step(0, "f4", 6.5f);
        rig.Step(1, "f4", 6.5f);
        Assert.Empty(rig.All);
        rig.Step(2, "f4", 6.5f, onTrack: false);
        Assert.Equal(6.5f, rig.Saved["f4"]);
    }

    [Fact]
    public void Switching_cars_saves_the_old_one_and_sets_the_new_one()
    {
        var rig = new Rig();
        rig.Saved["gt3"] = 7.0f;
        rig.Step(0, "gt3", 7.0f);
        rig.Step(0.25, "gt3", 7.5f);
        rig.Saved["mx5"] = 9.2f;
        var a = rig.Step(0.5, "mx5", 7.5f);
        Assert.Equal(new[] { StrengthActionKind.Store, StrengthActionKind.Apply }, a.Select(x => x.Kind));
        Assert.Equal(7.5f, rig.Saved["gt3"]);
        Assert.Equal(9.2f, a[1].Nm);
    }

    [Fact]
    public void Stopping_the_sim_saves_whats_pending()
    {
        var rig = new Rig();
        rig.Saved["gt3"] = 7.0f;
        rig.Step(0, "gt3", 7.0f);
        rig.Step(0.25, "gt3", 6.0f);
        var store = Assert.Single(rig.Memory.Stop(At(0.5)));
        Assert.Equal(6.0f, store.Nm);
        Assert.Null(rig.Memory.Car);
        Assert.Empty(rig.Memory.Stop(At(1)));
    }
}
