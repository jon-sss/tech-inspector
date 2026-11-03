using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TechInspector.Core.Ffb;
using KeyBinding = TechInspector.Core.Ffb.KeyBinding;

namespace TechInspector.Plugin.UI
{
    /// <summary>The wheel strength saved per car: the switch, what iRacing has now, and the saved list.</summary>
    public partial class FfbTab : UserControl
    {
        private Workspace _ws;
        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        public FfbTab()
        {
            InitializeComponent();
            _tick.Tick += (s, e) => Render();
            PreviewKeyDown += CaptureKey;
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { Render(); _tick.Start(); }
                else _tick.Stop();
            };
        }

        public void Attach(Workspace ws)
        {
            _ws = ws;
            _ws.Plugin.Ffb.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(Render));
            _ws.Reloaded += (s, e) => Render();
            Render();
        }

        private TechInspectorPlugin Plugin => _ws?.Plugin;

        private sealed class CarRow
        {
            public string Car { get; set; }
            public string Name { get; set; }
            public string Strength { get; set; }
            public string When { get; set; }
            public bool Current { get; set; }
        }

        private void Render()
        {
            if (Plugin == null || !IsVisible && Rows.ItemsSource != null) return;
            try { RenderNow(); }
            catch (Exception ex)
            {
                _tick.Stop();   // don't repeat a failure every second
                SimHub.Logging.Current.Error("Tech Inspector: could not show the force feedback tab", ex);
            }
        }

        private void RenderNow()
        {
            var settings = Plugin.Settings;
            var ffb = Plugin.Ffb;
            PerCar.IsChecked = settings.FfbPerCar;

            var lines = new List<StatusLine>();
            if (Plugin.SimRunning && ffb.CurrentCar != null)
            {
                var now = ffb.CurrentNm.HasValue ? ffb.CurrentNm.Value.ToString("0.0") + " Nm" : "not reported yet";
                lines.Add(StatusLine.Ok($"{ffb.CurrentCarName ?? ffb.CurrentCar} loaded, max force {now}."));
            }
            else if (Plugin.SimRunning) lines.Add(StatusLine.Ok("iRacing is running. Waiting for a car to load."));
            else lines.Add(StatusLine.Ok("iRacing is closed. Strengths are saved and set while you drive."));
            if (!settings.FfbPerCar) lines.Add(StatusLine.Warn("Off. Strengths aren't saved or set."));
            else
            {
                if (ffb.Status != null) lines.Add(ffb.StatusIsProblem ? StatusLine.Warn(ffb.Status) : StatusLine.Ok(ffb.Status));
                lines.Add(KeysLine());
            }
            StatusLines.ItemsSource = lines;

            List<CarRow> rows;
            lock (settings)
                rows = settings.FfbMaxForceByCar
                    .Select(kv => new CarRow
                    {
                        Car = kv.Key,
                        Name = string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name,
                        Strength = kv.Value.Nm.ToString("0.0") + " Nm",
                        When = kv.Value.When == default ? "" : $"{kv.Value.When:d MMM} at {kv.Value.When:HH:mm}",
                        Current = Plugin.SimRunning && string.Equals(kv.Key, ffb.CurrentCar, StringComparison.OrdinalIgnoreCase),
                    })
                    .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            Rows.ItemsSource = rows;
            EmptyNote.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RowsHeader.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private string _keysText;
        private bool _keysOk;
        private DateTime _keysRead;

        private bool _ownKeys;

        /// <summary>
        /// The keys that will be pressed, read from controls.cfg every few seconds. The key buttons
        /// only show when iRacing's bindings can't be read.
        /// </summary>
        private StatusLine KeysLine()
        {
            if (DateTime.UtcNow - _keysRead > TimeSpan.FromSeconds(5))
            {
                _keysRead = DateTime.UtcNow;
                var k = FfbKeeper.Keys(Plugin);
                _keysOk = k.Ready;
                _ownKeys = k.Unreadable;
                _keysText =
                    k.Unbound ? "iRacing's strength up and down controls aren't bound to keys. Bind them in iRacing, then restart it so it saves them."
                    : k.Ready && !k.Unreadable ? $"Sets the strength with your keys from iRacing, {k.Up} and {k.Down}."
                    : k.Ready ? $"Sets the strength with {k.Up} and {k.Down}, set below."
                    : "Tech Inspector can't read the keys bound to iRacing's strength controls. Bind letters, digits or arrows in iRacing, or press the same keys below.";
            }
            OwnKeys.Visibility = _ownKeys ? Visibility.Visible : Visibility.Collapsed;
            if (_capturing == null)
            {
                UpKey.Content = Plugin.Settings.FfbKeyUp?.ToString() ?? "Set key";
                DownKey.Content = Plugin.Settings.FfbKeyDown?.ToString() ?? "Set key";
            }
            ClearKeys.Visibility = Plugin.Settings.FfbKeyUp != null || Plugin.Settings.FfbKeyDown != null ? Visibility.Visible : Visibility.Collapsed;
            return _keysOk ? StatusLine.Ok(_keysText) : StatusLine.Warn(_keysText);
        }

        // ── setting the keys ──

        private Button _capturing;

        private void UpKey_Click(object sender, RoutedEventArgs e) => StartCapture(UpKey);
        private void DownKey_Click(object sender, RoutedEventArgs e) => StartCapture(DownKey);

        private void StartCapture(Button button)
        {
            EndCapture();
            _capturing = button;
            button.Content = "Press the key\u2026";
            button.Focus();
            button.LostKeyboardFocus += CaptureLostFocus;
        }

        private void CaptureLostFocus(object sender, KeyboardFocusChangedEventArgs e) { EndCapture(); Render(); }

        private void EndCapture()
        {
            if (_capturing == null) return;
            _capturing.LostKeyboardFocus -= CaptureLostFocus;
            _capturing = null;
        }

        /// <summary>The next key pressed while a button waits: its code, modifiers and name on this layout. Escape cancels.</summary>
        private void CaptureKey(object sender, KeyEventArgs e)
        {
            if (_capturing == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt) return;
            var target = _capturing;
            EndCapture();
            if (key != Key.Escape)
            {
                byte vk = (byte)KeyInterop.VirtualKeyFromKey(key);
                var mods = Keyboard.Modifiers;
                var binding = new KeyBinding
                {
                    Key = vk,
                    Shift = (mods & ModifierKeys.Shift) != 0,
                    Ctrl = (mods & ModifierKeys.Control) != 0,
                    Alt = (mods & ModifierKeys.Alt) != 0,
                    Label = KeyPresser.KeyName(vk) ?? key.ToString(),
                };
                if (target == UpKey) Plugin.Settings.FfbKeyUp = binding; else Plugin.Settings.FfbKeyDown = binding;
                Plugin.SaveSettings();
                Plugin.Ffb.KeysChanged();
            }
            _keysRead = default;
            Render();
        }

        private void ClearKeys_Click(object sender, RoutedEventArgs e)
        {
            Plugin.Settings.FfbKeyUp = null;
            Plugin.Settings.FfbKeyDown = null;
            Plugin.SaveSettings();
            _keysRead = default;
            Render();
        }

        private void PerCar_Click(object sender, RoutedEventArgs e)
        {
            bool on = PerCar.IsChecked == true;
            if (!on) Plugin.Ffb.Stop();   // save what's pending before it stops sampling
            Plugin.Settings.FfbPerCar = on;
            Plugin.SaveSettings();
            Render();
        }

        private void Forget_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CarRow row) Plugin.Ffb.Forget(row.Car);
        }
    }
}
