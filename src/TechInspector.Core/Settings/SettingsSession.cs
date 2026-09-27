using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TechInspector.Core.Catalog;
using TechInspector.Core.Ini;
using TechInspector.Core.IO;

namespace TechInspector.Core.Settings
{
    /// <summary>
    /// The settings the plugin shows at one time: one renderer file (the chosen display mode) plus
    /// app.ini and core.ini, merged with the catalog, with pending changes kept apart from the files.
    /// </summary>
    public sealed class SettingsSession
    {
        private readonly Dictionary<string, IniDocument> _docs = new Dictionary<string, IniDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Setting> _byId = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, string> _ignored;
        private readonly List<Setting> _settings = new List<Setting>();
        private readonly List<SectionInfo> _sections = new List<SectionInfo>();

        private SettingsSession(string folder, string rendererFile, SettingsCatalog catalog, IDictionary<string, string> ignored)
        {
            Folder = folder;
            RendererFile = rendererFile;
            Catalog = catalog;
            _ignored = ignored ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Folder { get; }
        public string RendererFile { get; }
        public SettingsCatalog Catalog { get; }
        public IReadOnlyList<Setting> Settings => _settings;
        /// <summary>Sections in display order: by focus area, then common before rare, then the area's own order.</summary>
        public IReadOnlyList<SectionInfo> Sections => _sections;
        public IReadOnlyList<string> AreaTitles => Catalog.Areas.Select(a => a.Title).ToList();
        public IEnumerable<string> FileNames => _docs.Keys;
        public IEnumerable<Setting> Pending => _settings.Where(s => s.IsChanged);

        public string PathOf(string fileName) => Path.Combine(Folder, fileName);
        public IniDocument Document(string fileName) => _docs.TryGetValue(fileName, out var d) ? d : null;

        public static SettingsSession Load(string folder, string rendererFile, SettingsCatalog catalog, IDictionary<string, string> ignored = null)
        {
            var session = new SettingsSession(folder, rendererFile, catalog, ignored);
            foreach (var name in new[] { rendererFile, IracingPaths.App, IracingPaths.Core })
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) session.Add(name, IniDocument.Load(path));
            }
            session.SortSections();
            return session;
        }

        /// <summary>Builds a session from documents already in memory. Used by tests.</summary>
        public static SettingsSession FromDocuments(string rendererFile, SettingsCatalog catalog, IDictionary<string, IniDocument> docs)
        {
            var session = new SettingsSession("", rendererFile, catalog, null);
            foreach (var kv in docs) session.Add(kv.Key, kv.Value);
            session.SortSections();
            return session;
        }

        private void Add(string fileName, IniDocument doc)
        {
            _docs[fileName] = doc;
            var sections = new Dictionary<string, SectionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in doc.Entries)
            {
                if (!sections.TryGetValue(line.Section, out var info))
                {
                    info = DescribeSection(fileName, line.Section);
                    sections[line.Section] = info;
                    _sections.Add(info);
                }
                var def = Catalog.Find(fileName, line.Section, line.Key);
                var setting = new Setting(this, fileName, line, def ?? CommentInference.Infer(fileName, line.Section, line.Key, line.DisplayValue, line.Comment), def != null, info);
                if (_byId.ContainsKey(setting.Id)) continue;   // a key repeated within a section: the first one is what iRacing reads
                _byId[setting.Id] = setting;
                _settings.Add(setting);
            }
            // A section whose every setting is expert-only counts as rare.
            foreach (var info in sections.Values)
                if (_settings.Where(s => s.SectionInfo == info).All(s => s.IsExpert)) info.IsRare = true;
        }

        private SectionInfo DescribeSection(string fileName, string section)
        {
            var def = Catalog.FindSection(fileName, section);
            var key = SettingsCatalog.FileKey(fileName) + "|" + section;
            int areaIndex = -1, order = 1000;
            for (int i = 0; i < Catalog.Areas.Count && areaIndex < 0; i++)
            {
                int at = Catalog.Areas[i].Sections.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) { areaIndex = i; order = at; }
            }
            bool renderer = SettingsCatalog.FileKey(fileName) == "renderer";
            if (areaIndex < 0) areaIndex = renderer ? 0 : Catalog.Areas.Count - 1;   // new renderer sections (VR files) sit with graphics
            return new SectionInfo
            {
                FileName = fileName,
                Section = section,
                Name = def?.Name ?? section,
                Area = Catalog.Areas.Count > 0 ? Catalog.Areas[areaIndex].Title : "",
                AreaIndex = areaIndex,
                Order = order,
                IsRare = def != null ? def.Use == "rare" : !renderer,
                ManagedBySim = def?.ManagedBySim ?? false,
            };
        }

        private void SortSections()
        {
            var sorted = _sections.OrderBy(s => s.AreaIndex).ThenBy(s => s.IsRare).ThenBy(s => s.Order).ToList();
            _sections.Clear();
            _sections.AddRange(sorted);
        }

        public Setting Find(string fileName, string section, string key) =>
            _byId.TryGetValue(fileName + "|" + section + "|" + key, out var s) ? s : null;

        public Setting FindById(string id) => _byId.TryGetValue(id, out var s) ? s : null;

        public IEnumerable<Setting> In(SectionInfo section) => _settings.Where(s => s.SectionInfo == section);

        /// <summary>
        /// Order within a section: the section's lead setting (a master switch or level) first,
        /// hand-written entries in their set order, then the rest as they appear in the file.
        /// </summary>
        public int SortKey(Setting s)
        {
            int lead = Catalog.Leads.FindIndex(l => string.Equals(l, s.Section + "|" + s.Key, StringComparison.OrdinalIgnoreCase));
            if (lead >= 0) return lead - 100;
            if (s.Def.Order.HasValue) return s.Def.Order.Value;
            return 10000 + s.Line.LineNumber;
        }

        /// <summary>
        /// Sets a pending value. Returns an error message instead when the value doesn't fit the
        /// setting; numbers outside the known range are clamped rather than refused.
        /// </summary>
        public string SetDraft(Setting s, string value)
        {
            if (s.IsReadOnly) return "iRacing marks this setting as not to be edited.";
            value = (value ?? "").Trim();
            switch (s.Def.Type)
            {
                case "bool":
                    if (value != "0" && value != "1") return "Must be on or off.";
                    break;
                case "enum":
                case "num":
                case "range":
                    if (!CommentInference.TryNumber(value, out var n)) return "Must be a number.";
                    if (s.Def.Min.HasValue && n < s.Def.Min.Value) n = s.Def.Min.Value;
                    if (s.Def.Max.HasValue && n > s.Def.Max.Value) n = s.Def.Max.Value;
                    value = KeepStyle(s.Applied, n);
                    break;
                default:
                    if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0 || value.Contains("\t;")) return "Can't contain a line break or a tab followed by ';'.";
                    break;
            }
            s.Draft = Setting.SameValue(value, s.Applied) ? s.Applied : value;
            return null;
        }

        public void Discard(Setting s) => s.Draft = s.Applied;

        public void DiscardAll()
        {
            foreach (var s in _settings) s.Draft = s.Applied;
        }

        /// <summary>
        /// Writes the pending changes. Each file is read again from disk first and only the changed
        /// lines are set on that fresh copy, so anything iRacing wrote since loading is kept. The
        /// files are backed up before anything is written, and each file is replaced in one step.
        /// </summary>
        public WriteResult Write(BackupStore store, Func<bool> simRunning = null, string reason = null)
        {
            var pending = Pending.ToList();
            if (pending.Count == 0) return new WriteResult();
            if (simRunning != null && simRunning())
                throw new InvalidOperationException("iRacing is running. It rewrites its files when it closes, so changes are written after that.");

            var byFile = pending.GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new WriteResult { Count = pending.Count };
            // The backup holds everything, not only the files written, so any backup can put the whole setup back.
            result.Backup = store.Create(
                reason ?? $"Before writing {pending.Count} {(pending.Count == 1 ? "change" : "changes")}",
                byFile.Select(g => PathOf(g.Key)).Concat(string.IsNullOrEmpty(Folder) ? Enumerable.Empty<string>() : IracingPaths.BackupFiles(Folder)),
                baseFolder: Folder);

            foreach (var g in byFile)
            {
                var path = PathOf(g.Key);
                var fresh = IniDocument.Load(path);
                foreach (var s in g)
                {
                    if (fresh.TrySet(s.Section, s.Key, s.Draft)) result.Written.Add(s.Id);
                    else result.Skipped.Add(s.Id);   // iRacing removed the key since loading
                }
                if (fresh.IsDirty) SafeFileWriter.WriteAtomic(path, fresh.ToBytes());
                result.Files.Add(g.Key);
            }
            return result;
        }

        /// <summary>iRacing's default, when the catalog or the comment states one.</summary>
        public string DefaultOf(Setting s) => s.Def.Default.HasValue ? KeepStyle(s.Applied, s.Def.Default.Value) : null;

        public bool IsIgnored(Setting s) => _ignored.TryGetValue(s.Id, out var v) && Setting.SameValue(v, s.Draft);

        /// <summary>Accepts the current value: it isn't flagged as unusual until it changes.</summary>
        public void Ignore(Setting s) => _ignored[s.Id] = s.Draft;

        public void Unignore(Setting s) => _ignored.Remove(s.Id);

        public IDictionary<string, string> IgnoredValues => _ignored;

        /// <summary>
        /// Writes a number the way the file already writes that key: "0.600000" stays six decimals,
        /// an integer stays an integer, so a changed line looks like iRacing wrote it.
        /// </summary>
        internal static string KeepStyle(string current, double value)
        {
            var c = (current ?? "").Trim();
            int dot = c.IndexOf('.');
            if (dot >= 0 && CommentInference.TryNumber(c, out _))
            {
                int decimals = c.Length - dot - 1;
                return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
            }
            if (Math.Abs(value - Math.Round(value)) < 1e-9) return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            return Setting.Format(value);
        }
    }

    /// <summary>What a write did: the backup taken first, the files written, and any keys that had disappeared.</summary>
    public sealed class WriteResult
    {
        public Backup Backup { get; internal set; }
        public int Count { get; internal set; }
        public List<string> Files { get; } = new List<string>();
        public List<string> Written { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
    }
}
