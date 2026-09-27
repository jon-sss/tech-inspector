using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TechInspector.Core.Ini;
using TechInspector.Core.Settings;

namespace TechInspector.Core.IO
{
    /// <summary>Who made a backup. Automatic ones roll off; the others stay until deleted.</summary>
    public enum BackupKind { Auto, Manual, Imported }

    /// <summary>One backup: a folder holding copies of the files and a small manifest.</summary>
    public sealed class Backup
    {
        public string Id { get; internal set; }
        public string Folder { get; internal set; }
        public int Number { get; internal set; }
        public DateTime Created { get; internal set; }
        public string Reason { get; internal set; }
        /// <summary>The name the user gave it, or null.</summary>
        public string Name { get; internal set; }
        public BackupKind Kind { get; internal set; }
        /// <summary>The folder the files came from; copies keep their paths relative to it.</summary>
        public string Base { get; internal set; }
        /// <summary>For an imported copy: the file it was made from.</summary>
        public string Source { get; internal set; }
        public string SourceFile => Source?.Split('|')[0];
        /// <summary>Where each file goes back to, as full paths.</summary>
        public IReadOnlyList<string> Files { get; internal set; }

        /// <summary>Kept until deleted: backups the user took or imported.</summary>
        public bool Kept => Kind != BackupKind.Auto;

        /// <summary>Where the copy of an original file sits inside this backup.</summary>
        public string CopyOf(string original)
        {
            var rel = BackupStore.Relative(original, Base);
            return Path.Combine(Folder, rel ?? Path.GetFileName(original));
        }

        /// <summary>The file's name as shown: relative to the iRacing folder ("profiles\x.ini").</summary>
        public string ShortName(string original) => BackupStore.Relative(original, Base) ?? Path.GetFileName(original);

        public long Bytes => Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
    }

    /// <summary>How a file in a backup differs from the file there now.</summary>
    public enum FileState { Same, Changed, MissingNow }

    public sealed class FileDiff
    {
        public string Original { get; internal set; }
        public string Name { get; internal set; }
        public FileState State { get; internal set; }
        /// <summary>For .ini files: the settings that differ. Empty for other files.</summary>
        public IReadOnlyList<KeyDiff> Keys { get; internal set; } = Array.Empty<KeyDiff>();
    }

    /// <summary>One setting that differs. Then is the backup's value, Now the current one; null where the key is absent.</summary>
    public sealed class KeyDiff
    {
        public string Section { get; internal set; }
        public string Key { get; internal set; }
        public string Then { get; internal set; }
        public string Now { get; internal set; }
    }

    /// <summary>
    /// Keeps backups of iRacing's files in the plugin's own data folder, never inside the iRacing
    /// folder (the sim would treat stray files there as its own).
    /// </summary>
    public sealed class BackupStore
    {
        private const string Manifest = "backup.txt";
        /// <summary>How many automatic backups are kept. Ones the user took or imported don't count.</summary>
        public const int KeepAuto = 30;

        private readonly string _root;

        public BackupStore(string root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string Root => _root;

        /// <summary>
        /// Copies the given files into a new backup folder. Missing files are skipped. Files under
        /// <paramref name="baseFolder"/> keep their relative paths, so a folder such as profiles\ fits.
        /// </summary>
        public Backup Create(string reason, IEnumerable<string> files, DateTime? now = null, string baseFolder = null,
                             BackupKind kind = BackupKind.Auto, string name = null)
        {
            var b = CreateCore(reason, files.Select(f => (f, f)), now ?? DateTime.Now, baseFolder, kind, name, null);
            if (kind == BackupKind.Auto) Prune();
            return b;
        }

        /// <summary>Turns a hand-made copy into a backup of the file it copies, dated by the copy's own time. The copy stays.</summary>
        public Backup Import(StrayCopy copy, string baseFolder)
        {
            var original = Path.Combine(baseFolder, copy.OriginalName);
            return CreateCore("Imported from " + copy.FileName, new[] { (copy.Path, original) }, copy.Modified,
                              baseFolder, BackupKind.Imported, null, Signature(copy.Path, copy.Modified));
        }

        /// <summary>Copies already imported, by path and file time, so they aren't offered again.</summary>
        public ISet<string> ImportedSources() =>
            new HashSet<string>(List().Where(b => b.Source != null).Select(b => b.Source), StringComparer.OrdinalIgnoreCase);

        internal static string Signature(string path, DateTime modified) =>
            path + "|" + modified.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

        private Backup CreateCore(string reason, IEnumerable<(string From, string Original)> files, DateTime when,
                                  string baseFolder, BackupKind kind, string name, string source)
        {
            int number = NextNumber();
            Directory.CreateDirectory(_root);
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var folder = Path.Combine(_root, id);
            for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(_root, id + "-" + n);
            Directory.CreateDirectory(folder);

            var copied = new List<string>();
            foreach (var (from, original) in files.GroupBy(f => f.Original, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (!File.Exists(from)) continue;
                var target = Path.Combine(folder, Relative(original, baseFolder) ?? Path.GetFileName(original));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(from, target, overwrite: false);
                copied.Add(original);
            }

            var sb = new StringBuilder();
            void Line(string k, string v) { if (v != null) sb.Append(k).Append('=').Append(OneLine(v)).Append("\r\n"); }
            Line("number", number.ToString(CultureInfo.InvariantCulture));
            Line("created", when.ToString("o", CultureInfo.InvariantCulture));
            Line("kind", kind.ToString().ToLowerInvariant());
            Line("reason", reason ?? "");
            Line("name", string.IsNullOrWhiteSpace(name) ? null : name.Trim());
            Line("base", baseFolder);
            Line("source", source);
            foreach (var f in copied) Line("file", f);
            File.WriteAllText(Path.Combine(folder, Manifest), sb.ToString(), new UTF8Encoding(false));

            return Read(folder);
        }

        /// <summary>All backups, newest first.</summary>
        public IReadOnlyList<Backup> List()
        {
            if (!Directory.Exists(_root)) return Array.Empty<Backup>();
            var all = Directory.GetDirectories(_root)
                               .Where(d => File.Exists(Path.Combine(d, Manifest)))
                               .Select(Read)
                               .ToList();
            // Backups from before numbering get their numbers once, in the order they were taken.
            int next = all.Count == 0 ? 0 : all.Max(b => b.Number);
            foreach (var b in all.Where(b => b.Number == 0).OrderBy(b => b.Created))
            {
                b.Number = ++next;
                try { File.AppendAllText(Path.Combine(b.Folder, Manifest), "number=" + b.Number + "\r\n", new UTF8Encoding(false)); }
                catch (IOException) { /* numbered again next time */ }
            }
            return all.OrderByDescending(b => b.Created).ThenByDescending(b => b.Number).ToList();
        }

        public long TotalBytes => Directory.Exists(_root)
            ? new DirectoryInfo(_root).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

        private int NextNumber()
        {
            var all = List();
            return all.Count == 0 ? 1 : all.Max(b => b.Number) + 1;
        }

        /// <summary>
        /// Puts a backup's files back where they came from (all of them, or only <paramref name="only"/>).
        /// Takes a fresh backup of the current files first, so a restore can itself be undone.
        /// Returns that fresh backup.
        /// </summary>
        public Backup Restore(Backup backup, IEnumerable<string> only = null, string reason = null)
        {
            var files = only == null
                ? backup.Files.ToList()
                : backup.Files.Where(f => only.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            var before = Create(string.IsNullOrEmpty(reason) ? "Before restoring backup #" + backup.Number : reason, files, baseFolder: backup.Base);
            foreach (var original in files)
            {
                var copy = backup.CopyOf(original);
                if (!File.Exists(copy)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                SafeFileWriter.WriteAtomic(original, File.ReadAllBytes(copy));
            }
            return before;
        }

        /// <summary>Removes automatic backups beyond the newest <see cref="KeepAuto"/>.</summary>
        public void Prune()
        {
            foreach (var old in List().Where(b => b.Kind == BackupKind.Auto).Skip(KeepAuto))
            {
                try { Directory.Delete(old.Folder, recursive: true); }
                catch (IOException) { /* in use: removed next time */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>How each file in the backup differs from the file there now.</summary>
        public static IReadOnlyList<FileDiff> Compare(Backup backup)
        {
            var result = new List<FileDiff>();
            foreach (var original in backup.Files)
            {
                var copy = backup.CopyOf(original);
                if (!File.Exists(copy)) continue;
                var d = new FileDiff { Original = original, Name = backup.ShortName(original) };
                if (!File.Exists(original)) d.State = FileState.MissingNow;
                else
                {
                    var then = File.ReadAllBytes(copy);
                    var now = File.ReadAllBytes(original);
                    if (then.SequenceEqual(now)) d.State = FileState.Same;
                    else if (original.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                    {
                        d.Keys = DiffIni(IniDocument.Parse(then), IniDocument.Parse(now));
                        // Only comments, spacing or order differ: no setting would change.
                        d.State = d.Keys.Count == 0 ? FileState.Same : FileState.Changed;
                    }
                    else d.State = FileState.Changed;
                }
                result.Add(d);
            }
            return result;
        }

        /// <summary>Settings whose values differ, in the backup's order, then keys only the current file has.</summary>
        public static IReadOnlyList<KeyDiff> DiffIni(IniDocument then, IniDocument now)
        {
            Dictionary<string, IniLine> Map(IniDocument doc) =>
                doc.Entries.GroupBy(e => e.Section + "|" + e.Key, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var a = Map(then);
            var b = Map(now);
            var diffs = new List<KeyDiff>();
            foreach (var kv in a)
            {
                b.TryGetValue(kv.Key, out var cur);
                if (cur != null && Setting.SameValue(kv.Value.Value, cur.Value)) continue;
                diffs.Add(new KeyDiff { Section = kv.Value.Section, Key = kv.Value.Key, Then = kv.Value.DisplayValue, Now = cur?.DisplayValue });
            }
            foreach (var kv in b.Where(kv => !a.ContainsKey(kv.Key)))
                diffs.Add(new KeyDiff { Section = kv.Value.Section, Key = kv.Value.Key, Then = null, Now = kv.Value.DisplayValue });
            return diffs;
        }

        /// <summary>The path relative to the folder, or null when it isn't inside it.</summary>
        internal static string Relative(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path)) return null;
            var f = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(f, StringComparison.OrdinalIgnoreCase) ? path.Substring(f.Length) : null;
        }

        private static Backup Read(string folder)
        {
            var files = new List<string>();
            var b = new Backup { Id = Path.GetFileName(folder), Folder = folder, Created = Directory.GetCreationTime(folder), Reason = "" };
            foreach (var line in File.ReadAllLines(Path.Combine(folder, Manifest), Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var k = line.Substring(0, eq);
                var v = line.Substring(eq + 1);
                switch (k)
                {
                    case "created" when DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c): b.Created = c; break;
                    case "number" when int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n): b.Number = n; break;
                    case "kind": b.Kind = v == "manual" ? BackupKind.Manual : v == "imported" ? BackupKind.Imported : BackupKind.Auto; break;
                    case "reason": b.Reason = v; break;
                    case "name": b.Name = v; break;
                    case "base": b.Base = v; break;
                    case "source": b.Source = v; break;
                    case "file": files.Add(v); break;
                }
            }
            b.Files = files;
            return b;
        }

        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");
    }
}
