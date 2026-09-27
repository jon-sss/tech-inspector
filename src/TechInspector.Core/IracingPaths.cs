using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TechInspector.Core
{
    /// <summary>Where iRacing keeps its settings, and which of its files the plugin edits.</summary>
    public static class IracingPaths
    {
        public const string App = "app.ini";
        public const string Core = "core.ini";
        public const string RendererTemplate = "rendererDX11.ini";

        /// <summary>One renderer file per display mode. Monitor is the one most people use.</summary>
        public static readonly IReadOnlyList<string> RendererModes = new[]
        {
            "rendererDX11Monitor.ini", "rendererDX11OpenXR.ini", "rendererDX11Oculus.ini", "rendererDX11OpenVR.ini",
        };

        /// <summary>
        /// The iRacing folder under the user's Documents. Windows reports the real location when
        /// Documents has been moved to another drive or into OneDrive. Returns null if it isn't there.
        /// </summary>
        public static string FindDocumentsFolder(string documents = null)
        {
            documents = documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) return null;
            var folder = Path.Combine(documents, "iRacing");
            return Directory.Exists(folder) ? folder : null;
        }

        /// <summary>The settings files present in the folder, in the order the plugin shows them.</summary>
        public static IReadOnlyList<string> SettingsFiles(string folder)
        {
            var names = RendererModes.Concat(new[] { App, Core, RendererTemplate });
            return names.Select(n => Path.Combine(folder, n)).Where(File.Exists).ToList();
        }

        /// <summary>Files the plugin doesn't edit but people lose: bindings, calibration and cameras.</summary>
        public static readonly IReadOnlyList<string> KeptFiles = new[] { "controls.cfg", "joyCalib.yaml", "camera.ini" };

        /// <summary>HUD layouts and control profiles.</summary>
        public const string ProfilesFolder = "profiles";

        /// <summary>The files people copy by hand before changing them, which import can turn into backups.</summary>
        public static readonly IReadOnlyList<string> CopyableFiles =
            RendererModes.Concat(new[] { RendererTemplate, App, Core }).Concat(KeptFiles).ToList();

        /// <summary>
        /// Everything a backup holds: the settings files, controls.cfg, joyCalib.yaml, camera.ini
        /// and the profiles folder.
        /// </summary>
        public static IReadOnlyList<string> BackupFiles(string folder)
        {
            var files = SettingsFiles(folder).Concat(KeptFiles.Select(n => Path.Combine(folder, n)).Where(File.Exists)).ToList();
            var profiles = Path.Combine(folder, ProfilesFolder);
            if (Directory.Exists(profiles))
                files.AddRange(Directory.GetFiles(profiles, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            return files;
        }

        /// <summary>True when the path is inside a OneDrive folder, which can lock files mid-write.</summary>
        public static bool IsInOneDrive(string path) =>
            path != null && path.IndexOf(Path.DirectorySeparatorChar + "OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
