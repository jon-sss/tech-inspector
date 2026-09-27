using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TechInspector.Core.Catalog
{
    /// <summary>
    /// What the plugin knows about each of iRacing's settings: plain-English label and description,
    /// control type, range, default, tier, group, and where the description came from.
    /// Built by tools/build-catalog.mjs and embedded in this assembly.
    /// </summary>
    [DataContract]
    public sealed class SettingsCatalog
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "generated")] public string Generated { get; set; }
        [DataMember(Name = "areas")] public List<AreaDef> Areas { get; set; } = new List<AreaDef>();
        [DataMember(Name = "leads")] public List<string> Leads { get; set; } = new List<string>();
        [DataMember(Name = "sections")] public List<SectionDef> Sections { get; set; } = new List<SectionDef>();
        [DataMember(Name = "settings")] public List<SettingDef> Settings { get; set; } = new List<SettingDef>();

        private Dictionary<string, SettingDef> _byKey;
        private Dictionary<string, SectionDef> _sections;

        /// <summary>"renderer" for any rendererDX11*.ini file, otherwise the file name.</summary>
        public static string FileKey(string fileName) =>
            fileName.StartsWith("rendererDX11", StringComparison.OrdinalIgnoreCase) ? "renderer" : fileName.ToLowerInvariant();

        public SettingDef Find(string fileName, string section, string key)
        {
            if (_byKey == null)
                _byKey = Settings.GroupBy(s => Id(s.File, s.Section, s.Key), StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _byKey.TryGetValue(Id(FileKey(fileName), section, key), out var d) ? d : null;
        }

        public SectionDef FindSection(string fileName, string section)
        {
            if (_sections == null)
                _sections = Sections.GroupBy(s => s.File + "|" + s.Section, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _sections.TryGetValue(FileKey(fileName) + "|" + section, out var d) ? d : null;
        }

        private static string Id(string file, string section, string key) => file + "|" + section + "|" + key;

        public static SettingsCatalog Load(Stream json)
        {
            var serializer = new DataContractJsonSerializer(typeof(SettingsCatalog));
            return (SettingsCatalog)serializer.ReadObject(json);
        }

        /// <summary>The catalog embedded in this assembly.</summary>
        public static SettingsCatalog LoadEmbedded()
        {
            var asm = typeof(SettingsCatalog).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
            using (var s = asm.GetManifestResourceStream(name)) return Load(s);
        }
    }

    [DataContract]
    public sealed class AreaDef
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        /// <summary>Section keys in display order, as "renderer|Graphics Options" or "app.ini|Audio".</summary>
        [DataMember(Name = "sections")] public List<string> Sections { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class SectionDef
    {
        [DataMember(Name = "file")] public string File { get; set; }
        [DataMember(Name = "section")] public string Section { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        /// <summary>common, occasional or rare.</summary>
        [DataMember(Name = "use")] public string Use { get; set; }
        [DataMember(Name = "why")] public string Why { get; set; }
        /// <summary>The sim sets these itself (setup auto-load), so their values are never "unusual".</summary>
        [DataMember(Name = "managedBySim")] public bool ManagedBySim { get; set; }
    }

    [DataContract]
    public sealed class OptionDef
    {
        [DataMember(Name = "value")] public double Value { get; set; }
        [DataMember(Name = "label")] public string Label { get; set; }
        [DataMember(Name = "tip")] public string Tip { get; set; }
    }

    [DataContract]
    public sealed class DependencyDef
    {
        /// <summary>A key in the same section.</summary>
        [DataMember(Name = "key")] public string Key { get; set; }
        /// <summary>eq, gt or ge.</summary>
        [DataMember(Name = "op")] public string Op { get; set; }
        [DataMember(Name = "value")] public double Value { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
    }

    [DataContract]
    public sealed class SettingDef
    {
        [DataMember(Name = "file")] public string File { get; set; }
        [DataMember(Name = "section")] public string Section { get; set; }
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "label")] public string Label { get; set; }
        [DataMember(Name = "help")] public string Help { get; set; }
        /// <summary>bool, enum, num, range or text.</summary>
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "options")] public List<OptionDef> Options { get; set; }
        [DataMember(Name = "min")] public double? Min { get; set; }
        [DataMember(Name = "max")] public double? Max { get; set; }
        [DataMember(Name = "step")] public double? Step { get; set; }
        [DataMember(Name = "unit")] public string Unit { get; set; }
        [DataMember(Name = "std")] public double? Default { get; set; }
        [DataMember(Name = "readOnly")] public bool ReadOnly { get; set; }
        /// <summary>For keys that mean "reduce" or "disable": the toggle shows on when the value is 0.</summary>
        [DataMember(Name = "invert")] public bool Invert { get; set; }
        [DataMember(Name = "iniOnly")] public bool IniOnly { get; set; }
        [DataMember(Name = "requiredBySimHub")] public bool RequiredBySimHub { get; set; }
        /// <summary>Risk text. Shown as a red mark; changing the value asks first.</summary>
        [DataMember(Name = "danger")] public string Danger { get; set; }
        /// <summary>A cost trade-off. Shown only once the value is changed.</summary>
        [DataMember(Name = "caution")] public string Caution { get; set; }
        [DataMember(Name = "dependsOn")] public DependencyDef DependsOn { get; set; }
        [DataMember(Name = "unusualAbove")] public double? UnusualAbove { get; set; }
        [DataMember(Name = "unusualNote")] public string UnusualNote { get; set; }
        /// <summary>curated, research, reason or file.</summary>
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "refs")] public List<string> Refs { get; set; }
        [DataMember(Name = "displayScale")] public double? DisplayScale { get; set; }
        [DataMember(Name = "displayUnit")] public string DisplayUnit { get; set; }
        [DataMember(Name = "displayDecimals")] public int? DisplayDecimals { get; set; }
        /// <summary>core, more or expert.</summary>
        [DataMember(Name = "tier")] public string Tier { get; set; }
        [DataMember(Name = "group")] public string Group { get; set; }
        /// <summary>Hand-written entries keep their hand-set order within a section.</summary>
        [DataMember(Name = "order")] public int? Order { get; set; }
    }
}
