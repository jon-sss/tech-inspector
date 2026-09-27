using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TechInspector.Core.Settings;

namespace TechInspector.Core.Health
{
    public enum Rating { Bad, Concerning, Good }

    /// <summary>One thing to look at on the Overview. The id is stable, so it can be ignored.</summary>
    public sealed class Finding
    {
        public string Id { get; set; }
        public Rating Rating { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        /// <summary>The setting a "Go to setting" link opens, when there is one.</summary>
        public Setting Setting { get; set; }
    }

    /// <summary>What the checks need from the machine, so tests can supply their own.</summary>
    public sealed class HealthInputs
    {
        public string DocumentsFolder { get; set; }
        public string InstallFolder { get; set; }
        /// <summary>Free bytes on the drive holding a path, or null when unknown.</summary>
        public Func<string, long?> FreeBytes { get; set; } = FreeSpace;
        public TelemetryFolder Telemetry { get; set; }

        public static long? FreeSpace(string path)
        {
            try { return string.IsNullOrEmpty(path) ? (long?)null : new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
            catch (Exception) { return null; }
        }
    }

    public sealed class HealthReport
    {
        /// <summary>Settings whose value differs from iRacing's default or trips a hand-written check.</summary>
        public List<Setting> Unusual { get; } = new List<Setting>();
        public List<Finding> Findings { get; } = new List<Finding>();
        public List<string> Passed { get; } = new List<string>();
        public int InertCount { get; set; }
        public TelemetryFolder Telemetry { get; set; }
        public bool TelemetryLarge { get; set; }
    }

    /// <summary>
    /// The Overview's checks. Each is cheap and reads only the files and the drives, so it can
    /// run whenever the Overview opens.
    /// </summary>
    public static class HealthCheck
    {
        /// <summary>A telemetry folder above this is worth a look while auto-logging is on.</summary>
        public const long TelemetryWarnBytes = 5_000_000_000;
        /// <summary>Below this, iRacing updates and replay spooling can fail.</summary>
        public const long LowDiskBytes = 15_000_000_000;

        public static HealthReport Run(SettingsSession session, HealthInputs env)
        {
            var r = new HealthReport { Telemetry = env.Telemetry };

            r.Unusual.AddRange(session.Settings.Where(s => s.IsUnusual)
                .OrderBy(s => s.Def.Default.HasValue ? 1 : 0)          // hand-flagged checks first
                .ThenBy(s => s.Tier == "core" ? 0 : 1)                  // then settings shown by default
                .ThenBy(s => s.SectionInfo.AreaIndex).ThenBy(s => s.Line.LineNumber));
            r.InertCount = session.Settings.Count(s => s.IsInert);

            // SimHub reads iRacing through its memory telemetry. Off means every dashboard goes quiet.
            var mem = session.Find(IracingPaths.App, "Misc", "irsdkEnableMem");
            if (mem != null && Setting.SameValue(mem.Draft, "0"))
                r.Findings.Add(new Finding { Id = "telemetry-off", Rating = Rating.Bad, Setting = mem,
                    Title = "SimHub can't read iRacing's telemetry",
                    Detail = "Live telemetry is off in app.ini, so SimHub's dashboards, overlays and effects get nothing from iRacing." });
            else if (mem != null) r.Passed.Add("SimHub can read iRacing telemetry");

            var readOnly = session.FileNames.Where(f => { try { return new FileInfo(session.PathOf(f)).IsReadOnly; } catch { return false; } }).ToList();
            foreach (var f in readOnly)
                r.Findings.Add(new Finding { Id = "readonly:" + f.ToLowerInvariant(), Rating = Rating.Concerning,
                    Title = f + " is read-only",
                    Detail = "iRacing can't save its settings to it, and Tech Inspector can't write your changes." });
            if (readOnly.Count == 0) r.Passed.Add("No settings files are read-only");

            if (IracingPaths.IsInOneDrive(env.DocumentsFolder))
                r.Findings.Add(new Finding { Id = "onedrive", Rating = Rating.Concerning,
                    Title = "Your iRacing folder is inside OneDrive",
                    Detail = "OneDrive can lock files while iRacing saves them, which loses settings. Pause syncing while you race." });
            else if (env.DocumentsFolder != null) r.Passed.Add("Documents folder is not in OneDrive");

            // Free space: the install drive (updates) and the settings drive (replays, telemetry).
            var drives = new List<(string Root, string Role)>();
            void AddDrive(string path, string role)
            {
                if (string.IsNullOrEmpty(path)) return;
                var root = Path.GetPathRoot(Path.GetFullPath(path)).ToUpperInvariant();
                var at = drives.FindIndex(d => d.Root == root);
                if (at >= 0) drives[at] = (root, drives[at].Role + " and " + role);
                else drives.Add((root, role));
            }
            AddDrive(env.InstallFolder, "the iRacing install drive");
            AddDrive(env.DocumentsFolder, "the drive with your iRacing settings");
            foreach (var (root, role) in drives)
            {
                var free = env.FreeBytes(root);
                if (free == null) continue;
                var name = root.TrimEnd('\\');
                if (free < LowDiskBytes)
                    r.Findings.Add(new Finding { Id = "disk:" + name, Rating = Rating.Concerning,
                        Title = $"{Gb(free.Value)} free on {name}, {role}",
                        Detail = role.Contains("install") ? "Large iRacing updates can fail on a nearly full drive." : "Replays, telemetry and backups need room on this drive." });
                else r.Passed.Add($"{Gb(free.Value)} free on {name}");
            }

            // Telemetry: .ibt files pile up while auto-logging is on.
            var autoLog = session.Find(IracingPaths.App, "Misc", "irsdkAutoLogDisk");
            if (env.Telemetry != null && autoLog != null && Setting.SameValue(autoLog.Draft, "1"))
            {
                if (env.Telemetry.TotalBytes >= TelemetryWarnBytes) r.TelemetryLarge = true;
                else r.Passed.Add($"Telemetry folder is {Gb(env.Telemetry.TotalBytes)}");
            }
            return r;
        }

        public static string Gb(long bytes) => bytes >= 1e12
            ? (bytes / 1e12).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " TB"
            : (bytes / 1e9).ToString(bytes >= 10e9 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }
}
