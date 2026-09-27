using System;
using System.Collections.Generic;
using System.Linq;
using TechInspector.Core.Settings;

namespace TechInspector.Core.Triples
{
    /// <summary>
    /// The saved triple layout: the [MonitorSetup] geometry and the [Display] window keys that
    /// iRacing tends to reset (after a driver update, with the side screens off, or on a crash).
    /// </summary>
    public static class LayoutGuard
    {
        public const string MonitorFile = "rendererDX11Monitor.ini";

        /// <summary>The guarded keys as "Section|key", in the order they're shown.</summary>
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            "MonitorSetup|NumMonitors", "MonitorSetup|MonitorType", "MonitorSetup|RadiusOfCurvature", "MonitorSetup|ScreenWidth", "MonitorSetup|MonitorWidth",
            "MonitorSetup|ViewingDist", "MonitorSetup|ScreenAngles", "MonitorSetup|EnableSMPSurround", "MonitorSetup|RenderViewPerMonitor", "MonitorSetup|BezelProtectionPct",
            "Display|windowedXPos", "Display|windowedYPos", "Display|windowedWidth", "Display|windowedHeight", "Display|border",
        };

        public sealed class Difference
        {
            public string Key { get; set; }
            public Setting Setting { get; set; }
            public string Saved { get; set; }
            public string InFile { get; set; }
        }

        public static IEnumerable<Setting> Settings(SettingsSession s) =>
            Keys.Select(k => { var p = k.Split('|'); return s.Find(MonitorFile, p[0], p[1]); }).Where(x => x != null);

        /// <summary>The layout as it is in the file now.</summary>
        public static Dictionary<string, string> Capture(SettingsSession s) =>
            Settings(s).ToDictionary(x => x.Section + "|" + x.Key, x => x.Applied, StringComparer.OrdinalIgnoreCase);

        /// <summary>Guarded keys whose value in the file differs from the saved layout.</summary>
        public static List<Difference> Differences(SettingsSession s, IDictionary<string, string> saved)
        {
            var list = new List<Difference>();
            if (saved == null || saved.Count == 0) return list;
            foreach (var x in Settings(s))
            {
                if (!saved.TryGetValue(x.Section + "|" + x.Key, out var want)) continue;
                if (!Setting.SameValue(want, x.Applied)) list.Add(new Difference { Key = x.Section + "|" + x.Key, Setting = x, Saved = want, InFile = x.Applied });
            }
            return list;
        }

        /// <summary>Queues the saved values as pending changes; the normal write backs up and writes them.</summary>
        public static int StageRestore(SettingsSession s, IDictionary<string, string> saved)
        {
            int n = 0;
            foreach (var d in Differences(s, saved))
                if (s.SetDraft(d.Setting, d.Saved) == null) n++;
            return n;
        }

        public static TripleInputs Inputs(SettingsSession s, bool pending = true)
        {
            string V(string key) { var x = s.Find(MonitorFile, "MonitorSetup", key); return x == null ? null : pending ? x.Draft : x.Applied; }
            double D(string key, double fallback) => CommentInferenceNumber(V(key), fallback);
            return new TripleInputs
            {
                NumMonitors = (int)D("NumMonitors", 1),
                Curved = D("MonitorType", 0) == 1,
                RadiusOfCurvature = D("RadiusOfCurvature", 1800),
                ScreenWidth = D("ScreenWidth", 700),
                MonitorWidth = D("MonitorWidth", 720),
                ViewingDist = D("ViewingDist", 700),
                ScreenAngles = D("ScreenAngles", 50),
            };
        }

        private static double CommentInferenceNumber(string v, double fallback) =>
            Catalog.CommentInference.TryNumber(v, out var n) ? n : fallback;
    }
}
