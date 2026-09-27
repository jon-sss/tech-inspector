using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TechInspector.Plugin
{
    /// <summary>
    /// Moves files to the Windows Recycle Bin in one batch, and puts them back. Nothing is ever
    /// deleted outright: the user can always restore from the Recycle Bin themselves.
    /// </summary>
    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        /// <summary>Sends files or folders to the Recycle Bin. Returns the ones that are gone afterwards.</summary>
        public static List<string> Send(IList<string> paths)
        {
            var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (existing.Count == 0) return new List<string>();
            int code = -1;
            RunSta(() =>
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = string.Join("\0", existing) + "\0\0",   // a double-null-terminated list
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                };
                code = SHFileOperation(ref op);
            });
            if (code != 0) SimHub.Logging.Current.Warn($"Tech Inspector: Recycle Bin move returned {code}");
            return existing.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        }

        /// <summary>
        /// Restores files from the Recycle Bin by their original paths, through Explorer's own
        /// "Restore". Returns how many came back.
        /// </summary>
        public static int Restore(IList<string> originalPaths)
        {
            var wanted = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
            int restored = 0;
            RunSta(() =>
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic bin = shell.NameSpace(10);   // the Recycle Bin
                foreach (dynamic item in bin.Items())
                {
                    string from = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                    string name = item.Name as string;
                    if (from == null || name == null) continue;
                    // Explorer may hide known extensions in Name; the item's own path keeps the real one.
                    var ext = Path.GetExtension(item.Path as string ?? "");
                    if (ext.Length > 0 && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
                    var full = Path.Combine(from, name);
                    if (!wanted.Contains(full) && !wanted.Contains(full + ".ibt")) continue;
                    item.InvokeVerb("undelete");
                    restored++;
                }
            });
            return restored;
        }

        /// <summary>Shell calls want a single-threaded apartment; run them on one.</summary>
        private static void RunSta(Action action)
        {
            Exception error = null;
            var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw error;
        }
    }
}
