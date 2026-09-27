using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TechInspector.Core.IO
{
    /// <summary>
    /// A hand-made copy of one of iRacing's files left in the iRacing folder, such as
    /// "rendererDX11Monitor - Copy (2).ini", "rendererDX11Monitor.ini4" or "app_backup.ini".
    /// </summary>
    public sealed class StrayCopy
    {
        public string Path { get; internal set; }
        public string FileName => System.IO.Path.GetFileName(Path);
        /// <summary>The file it is a copy of, such as "rendererDX11Monitor.ini".</summary>
        public string OriginalName { get; internal set; }
        public DateTime Modified { get; internal set; }
        public long Bytes { get; internal set; }

        /// <summary>The copies in the folder not imported yet, oldest first.</summary>
        public static IReadOnlyList<StrayCopy> Find(string folder, ISet<string> imported = null)
        {
            if (folder == null || !Directory.Exists(folder)) return Array.Empty<StrayCopy>();
            var found = new List<StrayCopy>();
            foreach (var file in new DirectoryInfo(folder).GetFiles())
            {
                var original = OriginalOf(file.Name);
                if (original == null) continue;
                if (imported != null && imported.Contains(BackupStore.Signature(file.FullName, file.LastWriteTime))) continue;
                found.Add(new StrayCopy { Path = file.FullName, OriginalName = original, Modified = file.LastWriteTime, Bytes = file.Length });
            }
            return found.OrderBy(c => c.Modified).ToList();
        }

        /// <summary>The iRacing file a name copies, or null when it isn't a recognisable copy.</summary>
        public static string OriginalOf(string name)
        {
            foreach (var original in IracingPaths.CopyableFiles)
            {
                var stem = Regex.Escape(System.IO.Path.GetFileNameWithoutExtension(original));
                var ext = Regex.Escape(System.IO.Path.GetExtension(original));
                var full = Regex.Escape(original);
                var pattern =
                    $"^(?:{stem}(?: - Copy(?: \\(\\d+\\))?| \\(\\d+\\)|_backup|_old|_bak){ext}" +   // "x - Copy (2).ini", "x (1).ini", "x_backup.ini"
                    $"|{full}(?:\\d+|\\.bak|\\.old|\\.backup)" +                                   // "x.ini4", "x.ini.bak"
                    $"|{stem}\\.(?:bak|old))$";                                                   // "x.bak"
                if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)) return original;
            }
            return null;
        }
    }
}
