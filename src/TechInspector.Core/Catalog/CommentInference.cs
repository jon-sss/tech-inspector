using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TechInspector.Core.Catalog
{
    /// <summary>
    /// Works out a control for a key the catalog doesn't know (a new key from an iRacing update,
    /// or a VR file's own section), from iRacing's inline comment and the current value.
    /// Same rules as the prototype's generator: "0=off, 1=on" is a toggle, a list of modes is a
    /// dropdown, a stated range is a number, anything else is text.
    /// </summary>
    public static class CommentInference
    {
        private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly Regex ReadOnlyNote = new Regex(@"don'?t edit|system use only|do not edit", I);
        private static readonly Regex Pairs = new Regex(@"(-?\d+)\s*[=]\s*([^,=;]+?)(?=\s*[,;]|\s+-?\d+\s*=|\s*\)?\s*$)", I);
        private static readonly Regex NamePairs = new Regex(@"([A-Za-z]\w*)\s*=\s*(-?\d+)", I);
        private static readonly Regex DashPairs = new Regex(@"(\d+)\s*-\s*([A-Za-z][^,]*?)(?=\s*,|\s+or\s+\d|\s*\.?\s*$)", I);
        private static readonly Regex DefaultNote = new Regex(@"\((\d+) default\)|(\d+)\s*=\s*default", I);
        private static readonly Regex RangeTo = new Regex(@"(-?\d+(?:\.\d+)?)\s*to\s*\+?(-?\d+(?:\.\d+)?)", I);
        private static readonly Regex RangeDash = new Regex(@"^(-?\d+)\s*-\s*(\d+)\s*[:=]", I);
        private static readonly Regex Integer = new Regex(@"^-?\d+$");
        private static readonly Regex Boolish = new Regex(
            @"^(true if|enable|disable|show|hide|set (this )?to|should|does|is |use |force|allow|automatic|display|log |report|call out|remove|leave|mute|ask|pause|update|run |occasionally|motivational|keep|present|animate|play|send|detects|always|revert|if |vibrate|enables|include|apply|turn|let |reduce|record|choose|click|center|orient|fade|attempt|in looping|options search|compress|cut output|debounce|smooth)", I);
        private static readonly string[] OnOffWords = { "off", "on", "disabled", "enabled", "disable", "enable", "no", "yes", "false", "true", "none", "hide", "show" };

        /// <summary>Sections nobody should edit casually. Their keys land in the expert tier.</summary>
        public static readonly ISet<string> ExpertSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "XXX Dev Use Only", "Debug", "Communications", "AutoCfg", "Laser Scan", "SERVER", "Task", "FileLRUCache" };

        public static SettingDef Infer(string fileName, string section, string key, string value, string comment)
        {
            comment = (comment ?? "").Trim();
            var d = new SettingDef
            {
                File = SettingsCatalog.FileKey(fileName), Section = section, Key = key,
                Label = Humanize(key),
                Help = comment.Length > 0 ? comment : "No description in the file.",
                Type = "text", Source = "file",
                Tier = ExpertSections.Contains(section) ? "expert" : "more",
                ReadOnly = ReadOnlyNote.IsMatch(comment) || section.Equals("AutoCfg", StringComparison.OrdinalIgnoreCase),
            };

            var pairs = Pairs.Matches(comment).Cast<Match>().Select(m => (V: m.Groups[1].Value, L: m.Groups[2].Value)).ToList();
            if (pairs.Count < 2)
            {
                var np = NamePairs.Matches(comment).Cast<Match>().Select(m => (V: m.Groups[2].Value, L: m.Groups[1].Value)).ToList();
                if (np.Count >= 2) pairs = np;
            }
            if (pairs.Count < 2)
            {
                var dp = DashPairs.Matches(comment).Cast<Match>().Select(m => (V: m.Groups[1].Value, L: m.Groups[2].Value)).ToList();
                if (dp.Count >= 2) pairs = dp;
            }
            pairs = pairs.Select(p => (p.V, L: p.L.Trim().Trim('(', ')').Trim())).ToList();
            var labels = new HashSet<string>(pairs.Select(p => p.L.ToLowerInvariant()));

            var std = DefaultNote.Match(comment);
            if (std.Success) d.Default = double.Parse(std.Groups[1].Success ? std.Groups[1].Value : std.Groups[2].Value, CultureInfo.InvariantCulture);
            var range = RangeTo.Match(comment);
            if (!range.Success) range = RangeDash.Match(comment);

            if (!TryNumber(value, out var n)) return d;   // not numeric: stays text
            bool isInt = Integer.IsMatch(value.Trim());
            var pairValues = pairs.Select(p => int.Parse(p.V, CultureInfo.InvariantCulture)).ToList();

            if (comment.IndexOf("bitwise", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                d.Type = "num";   // a sum of flags, not a single choice
            }
            else if (pairs.Count > 0 && labels.All(l => l == "min" || l == "max" || l == "default"))
            {
                d.Type = "num"; d.Min = pairValues.Min(); d.Max = pairValues.Max();
            }
            else if (range.Success && pairs.Count > 0 && Num(range.Groups[2].Value) > pairValues.Max())
            {
                d.Type = "num"; d.Min = Num(range.Groups[1].Value); d.Max = Num(range.Groups[2].Value);
            }
            else if (pairs.Count >= 2 && isInt && pairValues.Contains((int)n))
            {
                var distinct = pairValues.Distinct().OrderBy(v => v).ToList();
                bool onOff = distinct.SequenceEqual(new[] { 0, 1 }) &&
                             labels.All(l => OnOffWords.Any(w => Regex.IsMatch(l, @"\b" + w + @"\b", I)));
                if (onOff) d.Type = "bool";
                else
                {
                    d.Type = "enum";
                    d.Options = pairs.OrderBy(p => int.Parse(p.V, CultureInfo.InvariantCulture))
                                     .Select(p => new OptionDef { Value = int.Parse(p.V, CultureInfo.InvariantCulture), Label = p.L.TrimEnd('.').Trim() })
                                     .ToList();
                }
            }
            else if (isInt && (n == 0 || n == 1) && (Boolish.IsMatch(comment) || comment.EndsWith("?") || comment.Length == 0) && !range.Success)
            {
                d.Type = "bool";
            }
            else
            {
                d.Type = "num";
                if (range.Success) { d.Min = Num(range.Groups[1].Value); d.Max = Num(range.Groups[2].Value); }
                if (!isInt) d.Step = Math.Abs(n) < 5 ? 0.05 : 0.5;
                if (comment.Contains("dB") || key.EndsWith("_db", StringComparison.OrdinalIgnoreCase) || key.StartsWith("loudness", StringComparison.Ordinal)) d.Unit = "dB";
                else if (comment.Contains("(mm)")) d.Unit = "mm";
                else if (comment.Contains("(MB)") || comment.Contains(" MB")) d.Unit = "MB";
            }
            return d;
        }

        /// <summary>"reduceFramerate_WhenFocusLost" → "Reduce framerate when focus lost"; acronyms stay upper case.</summary>
        public static string Humanize(string key)
        {
            var k = key.Replace('_', ' ');
            k = Regex.Replace(k, "(?<=[a-z0-9])(?=[A-Z])", " ");
            k = Regex.Replace(k, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
            var words = k.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => (w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c))) || w.Any(char.IsDigit) ? w : w.ToLowerInvariant());
            var s = string.Join(" ", words);
            return s.Length == 0 ? key : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        internal static bool TryNumber(string s, out double n) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);

        private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }
}
