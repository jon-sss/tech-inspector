using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TechInspector.Core.Health
{
    /// <summary>
    /// The .ibt files in iRacing's telemetry folder: sizes and dates only. Moving files to the
    /// Recycle Bin happens in the plugin, which runs on Windows.
    /// </summary>
    public sealed class TelemetryFolder
    {
        public sealed class IbtFile
        {
            public string Path { get; set; }
            public long Bytes { get; set; }
            public DateTime Modified { get; set; }
        }

        public string Folder { get; private set; }
        public IReadOnlyList<IbtFile> Files { get; private set; } = Array.Empty<IbtFile>();
        public long TotalBytes => Files.Sum(f => f.Bytes);
        public DateTime? Oldest => Files.Count == 0 ? (DateTime?)null : Files.Min(f => f.Modified);

        public static TelemetryFolder Scan(string iracingFolder)
        {
            var folder = Path.Combine(iracingFolder ?? "", "telemetry");
            var t = new TelemetryFolder { Folder = folder };
            if (!Directory.Exists(folder)) return t;
            t.Files = new DirectoryInfo(folder).EnumerateFiles("*.ibt")
                .Select(f => new IbtFile { Path = f.FullName, Bytes = f.Length, Modified = f.LastWriteTime })
                .OrderByDescending(f => f.Modified).ToList();
            return t;
        }

        /// <summary>
        /// Files older than the given number of days; 0 means all of them. The newest file is never
        /// picked when <paramref name="keepNewest"/> is set: iRacing may still be writing it.
        /// </summary>
        public List<IbtFile> OlderThan(int days, DateTime now, bool keepNewest = true)
        {
            var newest = keepNewest ? Files.FirstOrDefault() : null;
            return Files.Where(f => f != newest && (days <= 0 || (now - f.Modified).TotalDays > days)).ToList();
        }

        public static TelemetryFolder FromFiles(string folder, IEnumerable<IbtFile> files) =>
            new TelemetryFolder { Folder = folder, Files = files.OrderByDescending(f => f.Modified).ToList() };
    }
}
