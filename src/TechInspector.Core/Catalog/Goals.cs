using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using TechInspector.Core.Settings;

namespace TechInspector.Core.Catalog
{
    /// <summary>
    /// A common fix: the handful of settings behind one thing drivers come to change, such as
    /// frame rate or the spotter. Settings are named "file|section|key", with "renderer" standing
    /// for whichever graphics file is open.
    /// </summary>
    [DataContract]
    public sealed class GoalDef
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "summary")] public string Summary { get; set; }
        [DataMember(Name = "settings")] public List<string> Settings { get; set; } = new List<string>();
        /// <summary>When set, the goal lives on another tab and opens it instead of listing settings.</summary>
        [DataMember(Name = "tab")] public string Tab { get; set; }

        /// <summary>The goal's settings present in the loaded files, in the goal's order.</summary>
        public IReadOnlyList<Setting> Resolve(SettingsSession session)
        {
            var found = new List<Setting>();
            foreach (var id in Settings ?? new List<string>())
            {
                var p = id.Split('|');
                if (p.Length != 3) continue;
                var file = p[0] == "renderer" ? session.RendererFile : p[0];
                var s = session.Find(file, p[1], p[2]);
                if (s != null) found.Add(s);
            }
            return found;
        }
    }

    [DataContract]
    public sealed class GoalList
    {
        [DataMember(Name = "goals")] public List<GoalDef> Goals { get; set; } = new List<GoalDef>();

        public static GoalList Load(Stream json) => (GoalList)new DataContractJsonSerializer(typeof(GoalList)).ReadObject(json);

        /// <summary>The goals embedded in this assembly.</summary>
        public static GoalList LoadEmbedded()
        {
            var asm = typeof(GoalList).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("goals.json", StringComparison.OrdinalIgnoreCase));
            using (var s = asm.GetManifestResourceStream(name)) return Load(s);
        }
    }
}
