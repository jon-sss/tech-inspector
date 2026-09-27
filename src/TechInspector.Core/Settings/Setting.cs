using System;
using System.Globalization;
using System.Linq;
using TechInspector.Core.Catalog;
using TechInspector.Core.Ini;

namespace TechInspector.Core.Settings
{
    /// <summary>One setting as the plugin shows it: the line in the file, what the catalog says about it, and any pending change.</summary>
    public sealed class Setting
    {
        internal Setting(SettingsSession session, string fileName, IniLine line, SettingDef def, bool fromCatalog, SectionInfo section)
        {
            Session = session;
            FileName = fileName;
            Line = line;
            Def = def;
            FromCatalog = fromCatalog;
            SectionInfo = section;
            Draft = Applied;
        }

        public SettingsSession Session { get; }
        public string FileName { get; }
        public IniLine Line { get; }
        public SettingDef Def { get; }
        /// <summary>False when the control was worked out from iRacing's comment because the catalog didn't know the key.</summary>
        public bool FromCatalog { get; }
        public SectionInfo SectionInfo { get; }

        public string Id => FileName + "|" + Line.Section + "|" + Line.Key;
        public string Section => Line.Section;
        public string Key => Line.Key;
        public string Label => Def.Label;
        public string Help => Def.Help;
        public string Tier => Def.Tier ?? "more";
        public bool IsExpert => Tier == "expert";
        public string Group => Def.Group;
        /// <summary>iRacing's inline note from the file.</summary>
        public string Note => Line.Comment?.Trim();

        /// <summary>The value in the file now.</summary>
        public string Applied => Line.DisplayValue;

        /// <summary>The value the user wants. Equal to <see cref="Applied"/> unless there's a pending change.</summary>
        public string Draft { get; internal set; }

        public bool IsChanged => !SameValue(Draft, Applied);

        public bool IsReadOnly => Def.ReadOnly;

        /// <summary>A value that differs from iRacing's default, or trips a hand-written check (such as a screen width of three screens).</summary>
        public bool IsUnusual
        {
            get
            {
                if (Session.IsIgnored(this)) return false;
                if (Def.UnusualAbove.HasValue) return CommentInference.TryNumber(Draft, out var v) && v > Def.UnusualAbove.Value;
                if (!Def.Default.HasValue || SectionInfo.ManagedBySim) return false;
                return !SameValue(Draft, Format(Def.Default.Value));
            }
        }

        public string UnusualNote => Def.UnusualNote ?? (Def.Default.HasValue ? $"Default is {Show(Format(Def.Default.Value))}." : null);

        /// <summary>True when a parent setting switches this one off, e.g. the frame rate cap while the limit is off.</summary>
        public bool IsInert
        {
            get
            {
                var dep = Def.DependsOn;
                if (dep == null) return false;
                var parent = Session.Find(FileName, Section, dep.Key);
                if (parent == null || !CommentInference.TryNumber(parent.Draft, out var v)) return false;
                switch (dep.Op)
                {
                    case "gt": return !(v > dep.Value);
                    case "ge": return !(v >= dep.Value);
                    default: return Math.Abs(v - dep.Value) > 1e-9;
                }
            }
        }

        public string InertMessage => Def.DependsOn?.Message;

        /// <summary>For toggles: whether the switch shows as on (inverted keys are on at 0).</summary>
        public bool IsOn(string value) => Def.Invert ? SameValue(value, "0") : SameValue(value, "1");

        /// <summary>A value as the user reads it: On/Off, the option's label, or the number with its unit.</summary>
        public string Show(string value)
        {
            if (value == null) return "";
            switch (Def.Type)
            {
                case "bool": return IsOn(value) ? "On" : "Off";
                case "enum":
                    var o = Def.Options?.FirstOrDefault(x => SameValue(value, Format(x.Value)));
                    return o != null ? o.Label : value;
                case "text": return value.Length == 0 ? "(empty)" : value;
            }
            if (!CommentInference.TryNumber(value, out var n)) return value;
            if (Def.DisplayScale.HasValue)
                return (n * Def.DisplayScale.Value).ToString("F" + (Def.DisplayDecimals ?? 2), CultureInfo.InvariantCulture) + " " + Def.DisplayUnit;
            var shown = Format(n);   // "0.190000" reads as 0.19; the file keeps its own format
            if (string.IsNullOrEmpty(Def.Unit)) return shown;
            return Def.Unit == "°" || Def.Unit == "%" ? shown + Def.Unit : shown + " " + Def.Unit;
        }

        /// <summary>Numbers compare by value ("0.600000" equals "0.6"); everything else exactly.</summary>
        public static bool SameValue(string a, string b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (CommentInference.TryNumber(a, out var x) && CommentInference.TryNumber(b, out var y)) return Math.Abs(x - y) < 1e-9;
            return false;
        }

        public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public override string ToString() => Id + "=" + Draft;
    }

    /// <summary>A section as shown: its friendly name, focus area and position.</summary>
    public sealed class SectionInfo
    {
        public string FileName { get; internal set; }
        public string Section { get; internal set; }
        public string Name { get; internal set; }
        public string Area { get; internal set; }
        public int AreaIndex { get; internal set; }
        public int Order { get; internal set; }
        /// <summary>Few drivers ever open it; listed last in its area.</summary>
        public bool IsRare { get; internal set; }
        public bool ManagedBySim { get; internal set; }
        public string Key => FileName + "|" + Section;
    }
}
