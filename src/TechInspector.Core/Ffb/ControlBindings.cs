using System;
using System.IO;
using System.Text;

namespace TechInspector.Core.Ffb
{
    /// <summary>
    /// A keyboard binding: a Windows virtual-key code plus modifiers. Read from controls.cfg, or
    /// pressed by the user on the plugin's page, which gives the key's name too.
    /// </summary>
    public sealed class KeyBinding
    {
        public byte Key { get; set; }
        public bool Shift { get; set; }
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        /// <summary>The key's name as Windows gives it for the keyboard layout, for a key the user pressed.</summary>
        public string Label { get; set; }

        /// <summary>
        /// Letters, digits, arrows and a few navigation keys use Windows' key codes in controls.cfg.
        /// Others (F-keys, the numpad, + or # on a German keyboard) use iRacing's own codes, so
        /// only a key the user pressed for the plugin can stand in for them.
        /// </summary>
        public bool CanPress => Name(Key) != null || !string.IsNullOrEmpty(Label);

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            sb.Append(!string.IsNullOrEmpty(Label) ? Label : Name(Key) ?? $"key 0x{Key:X2}");
            return sb.ToString();
        }

        private static string Name(byte k)
        {
            if (k >= 0x41 && k <= 0x5A || k >= 0x30 && k <= 0x39) return ((char)k).ToString();
            switch (k)
            {
                case 0x09: return "Tab";
                case 0x20: return "Space";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                default: return null;
            }
        }
    }

    /// <summary>
    /// Reads keyboard bindings out of iRacing's binary controls.cfg. Each action is stored as its
    /// name, a null, then ints: 1, 6, the device (4 for the keyboard) and the key code, with the
    /// modifiers two bytes later (0x03 Shift, 0x0C Ctrl, 0x30 Alt). Read-only.
    /// </summary>
    public static class ControlBindings
    {
        public const string FileName = "controls.cfg";
        public const string IncreaseFfb = "IncFFB";
        public const string DecreaseFfb = "DecFFB";

        private const int Keyboard = 4;

        /// <summary>
        /// The controls.cfg iRacing uses: the one in the control profile named by app.ini's
        /// [ControlProfiles] Global, else the one in the iRacing folder itself.
        /// </summary>
        public static string ActivePath(string iracingFolder)
        {
            try
            {
                var app = Path.Combine(iracingFolder, IracingPaths.App);
                var profile = File.Exists(app) ? Ini.IniDocument.Load(app).Find("ControlProfiles", "Global")?.Value?.Trim() : null;
                if (!string.IsNullOrEmpty(profile) && profile.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    var inProfile = Path.Combine(iracingFolder, IracingPaths.ProfilesFolder, "controls", profile, FileName);
                    if (File.Exists(inProfile)) return inProfile;
                }
            }
            catch (IOException) { /* fall back to the folder's own file */ }
            catch (UnauthorizedAccessException) { }
            return Path.Combine(iracingFolder, FileName);
        }

        /// <summary>The file's bytes, shared with iRacing, which may hold it open; null when unreadable.</summary>
        public static byte[] Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>The action's keyboard binding; null when it's unbound, bound to a wheel button, or not found.</summary>
        public static KeyBinding Find(byte[] cfg, string action)
        {
            if (cfg == null || cfg.Length < 8 || cfg[0] != 'G' || cfg[1] != 'F' || cfg[2] != 'C' || cfg[3] != 'C') return null;
            var name = Encoding.ASCII.GetBytes(action + "\0");
            for (int i = IndexOf(cfg, name, 8); i >= 0; i = IndexOf(cfg, name, i + 1))
            {
                // Whole names only: "IncFFB" mustn't match the end of a longer one.
                if (char.IsLetterOrDigit((char)cfg[i - 1]) && !EndsWithPidVid(cfg, i)) continue;
                int at = i + name.Length;
                if (at + 19 > cfg.Length) return null;
                if (BitConverter.ToInt32(cfg, at + 8) != Keyboard) return null;
                byte key = (byte)BitConverter.ToInt32(cfg, at + 12);
                if (key == 0) return null;
                byte mods = cfg[at + 18];
                return new KeyBinding { Key = key, Shift = (mods & 0x03) != 0, Ctrl = (mods & 0x0C) != 0, Alt = (mods & 0x30) != 0 };
            }
            return null;
        }

        /// <summary>A joystick binding's device id ("PIDVID") can run straight into the next action's name.</summary>
        private static bool EndsWithPidVid(byte[] b, int i) =>
            i >= 6 && Encoding.ASCII.GetString(b, i - 6, 6) == "PIDVID";

        private static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Math.Max(from, 1); i <= hay.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
