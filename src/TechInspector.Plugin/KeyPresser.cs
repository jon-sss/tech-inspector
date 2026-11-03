using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TechInspector.Core.Ffb;

namespace TechInspector.Plugin
{
    /// <summary>
    /// Presses a key binding the way a keyboard would, by scancode, which games read. Windows sends
    /// it to the window in front, so callers check <see cref="SimInFront"/> first.
    /// </summary>
    public static class KeyPresser
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int lParam, System.Text.StringBuilder name, int size);

        private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8, MAPVK_VK_TO_VSC = 0;
        private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

        /// <summary>True when the window in front belongs to the sim.</summary>
        public static bool SimInFront()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                GetWindowThreadProcessId(hwnd, out var pid);
                using (var p = Process.GetProcessById((int)pid))
                    return string.Equals(p.ProcessName, TechInspectorPlugin.SimProcess, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The key's name on this keyboard layout ("+" or "#" on a German one), or null.</summary>
        public static string KeyName(byte vk)
        {
            uint scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) return null;
            int lParam = (int)(scan << 16) | (vk >= 0x21 && vk <= 0x2E ? 1 << 24 : 0);
            var sb = new System.Text.StringBuilder(32);
            return GetKeyNameText(lParam, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }

        /// <summary>Holds the modifiers, taps the key for a few frames, then lets go in reverse order.</summary>
        public static bool Press(KeyBinding k)
        {
            var mods = new System.Collections.Generic.List<byte>();
            if (k.Ctrl) mods.Add(VK_CONTROL);
            if (k.Alt) mods.Add(VK_MENU);
            if (k.Shift) mods.Add(VK_SHIFT);
            bool ok = true;
            foreach (var m in mods) ok &= Send(m, false);
            ok &= Send(k.Key, false);
            Thread.Sleep(40);
            ok &= Send(k.Key, true);
            for (int i = mods.Count - 1; i >= 0; i--) ok &= Send(mods[i], true);
            return ok;
        }

        private static bool Send(byte vk, bool up)
        {
            uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
            // Page Up/Down and the arrows sit on the extended part of the keyboard.
            if (vk >= 0x21 && vk <= 0x28) flags |= KEYEVENTF_EXTENDEDKEY;
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
            };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }
    }
}
