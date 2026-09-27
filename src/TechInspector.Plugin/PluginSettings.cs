using System;
using System.Collections.Generic;

namespace TechInspector.Plugin
{
    /// <summary>
    /// The plugin's own preferences, saved by SimHub as JSON in its data folder. Nothing here is
    /// ever written to iRacing's files.
    /// </summary>
    public class PluginSettings
    {
        /// <summary>The graphics file shown on the Settings tab.</summary>
        public string RendererFile { get; set; } = "rendererDX11Monitor.ini";

        public bool ShowMore { get; set; }
        public bool ShowExpert { get; set; }
        public bool ShowIniLines { get; set; }

        /// <summary>Settings the user hid, by id (file|section|key).</summary>
        public HashSet<string> HiddenIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The saved triple layout ("Section|key" → value), put back when iRacing changes it.</summary>
        public Dictionary<string, string> SavedLayout { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public DateTime? SavedLayoutWhen { get; set; }
        /// <summary>Restore the saved layout automatically after iRacing closes, when it changed it.</summary>
        public bool GuardAutoRestore { get; set; } = true;

        /// <summary>Overview findings the user chose to ignore, by finding id.</summary>
        public HashSet<string> IgnoredFindings { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>When above 0, telemetry files older than this many days go to the Recycle Bin each time iRacing closes.</summary>
        public int TelemetryAutoCleanDays { get; set; }

        /// <summary>Unusual values the user accepted: id → the value accepted. Flagged again if it changes.</summary>
        public Dictionary<string, string> IgnoredValues { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
