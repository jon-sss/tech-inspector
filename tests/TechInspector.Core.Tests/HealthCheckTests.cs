using System.Text;
using TechInspector.Core.Catalog;
using TechInspector.Core.Health;
using TechInspector.Core.Ini;
using TechInspector.Core.Settings;
using Xunit;

namespace TechInspector.Core.Tests;

public class HealthCheckTests
{
    private static readonly SettingsCatalog Catalog = SettingsCatalog.LoadEmbedded();

    private static SettingsSession Session(string memTelemetry = "1", string autoLog = "1") =>
        SettingsSession.FromDocuments("rendererDX11Monitor.ini", Catalog, new Dictionary<string, IniDocument>
        {
            ["rendererDX11Monitor.ini"] = IniDocument.Parse(Encoding.Latin1.GetBytes(
                "[Graphics Options]\r\nSharpeningClamp=9                               \t; sharpening clamp (0=min, 10=default, 100=max)\r\n" +
                "[MonitorSetup]\r\nScreenWidth=2120                                \t; (mm) width of visible screen area\r\n")),
            ["app.ini"] = IniDocument.Parse(Encoding.Latin1.GetBytes(
                $"[Misc]\r\nirsdkEnableMem={memTelemetry}                        \t; enable memory based telemetry\r\n" +
                $"irsdkAutoLogDisk={autoLog}                      \t; Automatically log disk telemetry\r\n")),
        });

    private static HealthInputs Env(long freeBytes = 200_000_000_000, long telemetryBytes = 0, string docs = @"D:\Documents\iRacing") => new()
    {
        DocumentsFolder = docs,
        InstallFolder = @"E:\iRacing",
        FreeBytes = _ => freeBytes,
        Telemetry = TelemetryFolder.FromFiles(@"D:\Documents\iRacing\telemetry",
            telemetryBytes == 0 ? Array.Empty<TelemetryFolder.IbtFile>() : new[] { new TelemetryFolder.IbtFile { Path = "a.ibt", Bytes = telemetryBytes, Modified = DateTime.Now } }),
    };

    [Fact]
    public void Unusual_values_list_hand_flagged_checks_first()
    {
        var r = HealthCheck.Run(Session(), Env());
        Assert.Equal(new[] { "ScreenWidth", "SharpeningClamp" }, r.Unusual.Select(s => s.Key));
    }

    [Fact]
    public void Telemetry_off_is_bad_because_SimHub_goes_quiet()
    {
        var r = HealthCheck.Run(Session(memTelemetry: "0"), Env());
        var f = Assert.Single(r.Findings, x => x.Id == "telemetry-off");
        Assert.Equal(Rating.Bad, f.Rating);
        Assert.Equal("irsdkEnableMem", f.Setting!.Key);
        Assert.DoesNotContain("SimHub can read iRacing telemetry", r.Passed);
    }

    [Fact]
    public void Low_disk_space_is_reported_once_per_drive_with_its_role()
    {
        var r = HealthCheck.Run(Session(), Env(freeBytes: 10_000_000_000));
        Assert.Equal(new[] { "10 GB free on E:, the iRacing install drive", "10 GB free on D:, the drive with your iRacing settings" },
                     r.Findings.Where(f => f.Id.StartsWith("disk:")).Select(f => f.Title));
        var ok = HealthCheck.Run(Session(), Env());
        Assert.Contains("200 GB free on E:", ok.Passed);
    }

    [Fact]
    public void A_big_telemetry_folder_counts_only_while_auto_logging_is_on()
    {
        Assert.True(HealthCheck.Run(Session(), Env(telemetryBytes: 7_290_000_000)).TelemetryLarge);
        Assert.False(HealthCheck.Run(Session(autoLog: "0"), Env(telemetryBytes: 7_290_000_000)).TelemetryLarge);
        Assert.Contains("Telemetry folder is 1.2 GB", HealthCheck.Run(Session(), Env(telemetryBytes: 1_200_000_000)).Passed);
    }

    [Fact]
    public void OneDrive_is_flagged()
    {
        var r = HealthCheck.Run(Session(), Env(docs: @"C:\Users\x\OneDrive\Documents\iRacing"));
        Assert.Contains(r.Findings, f => f.Id == "onedrive");
    }

    [Fact]
    public void Telemetry_picks_by_age_and_never_the_newest_file()
    {
        var now = new DateTime(2026, 9, 26);
        var t = TelemetryFolder.FromFiles("x", new[]
        {
            new TelemetryFolder.IbtFile { Path = "new.ibt", Bytes = 1, Modified = now.AddHours(-1) },
            new TelemetryFolder.IbtFile { Path = "week.ibt", Bytes = 1, Modified = now.AddDays(-10) },
            new TelemetryFolder.IbtFile { Path = "old.ibt", Bytes = 1, Modified = now.AddDays(-45) },
        });
        Assert.Equal(new[] { "old.ibt" }, t.OlderThan(30, now).Select(f => f.Path));
        Assert.Equal(new[] { "week.ibt", "old.ibt" }, t.OlderThan(0, now).Select(f => f.Path));
        Assert.Equal(3, t.OlderThan(0, now, keepNewest: false).Count);
    }
}
