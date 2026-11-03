using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameReaderCommon;
using TechInspector.Core.Ffb;

namespace TechInspector.Plugin
{
    /// <summary>
    /// Keeps iRacing's wheel strength per car. Reads the player's car and the strength from the
    /// live telemetry, saves it in the plugin's preferences, and sets a car's saved strength once
    /// the player is in it by pressing the keys bound to iRacing's own strength up and down
    /// controls, as the user would. The strength is iRacing's max force in Nm, the figure its telemetry reports.
    /// iRacing's SDK isn't used for this: its force feedback command overrides the wheel's force
    /// and locks the sim's own force feedback settings.
    /// </summary>
    public sealed class FfbKeeper
    {
        private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);
        /// <summary>How long a set waits for the iRacing window to come to the front.</summary>
        private static readonly TimeSpan FrontWait = TimeSpan.FromSeconds(60);
        private const int MaxPresses = 150;

        private readonly TechInspectorPlugin _plugin;
        private readonly StrengthMemory _memory;
        private readonly object _gate = new object();
        private DateTime _next;
        private CancellationTokenSource _nudge;
        /// <summary>The strength in the latest sample, NaN when unknown; written on SimHub's data thread.</summary>
        private float _latest = float.NaN;

        public FfbKeeper(TechInspectorPlugin plugin)
        {
            _plugin = plugin;
            _memory = new StrengthMemory(car => { lock (_plugin.Settings) return _plugin.Settings.FfbMaxForceByCar.TryGetValue(car, out var s) ? s.Nm : (float?)null; });
        }

        /// <summary>The player's car path and name, while iRacing runs with a car loaded.</summary>
        public string CurrentCar => _memory.Car;
        public string CurrentCarName => _memory.CarName;
        /// <summary>The strength iRacing reports now, in Nm.</summary>
        public float? CurrentNm => _memory.Live;

        /// <summary>What the last or current set is doing, for the tab; null when there's nothing to say.</summary>
        public string Status { get; private set; }
        public bool StatusIsProblem { get; private set; }

        /// <summary>Raised off the UI thread when a strength was saved or set.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// On SimHub's critical path: samples four times a second, and every frame while a set is
        /// pressing keys, so each press is seen as soon as the sim reports it.
        /// </summary>
        public void Sample(GameData data)
        {
            var now = DateTime.UtcNow;
            if (now < _next && _nudge == null) return;
            _next = now + Every;
            string car = null, name = null;
            float? nm = null;
            bool onTrack = false;
            try { Read(data.NewData?.GetRawDataObject(), out car, out name, out nm, out onTrack); }
            catch (Exception) { /* not an iRacing sample yet: treat as no car */ }
            _latest = nm ?? float.NaN;
            lock (_gate)
            {
                Handle(_memory.Step(car, name, nm, onTrack, now));
                if (!_memory.Applying) CancelNudge();
            }
        }

        /// <summary>iRacing stopped, or the feature was turned off: save what's pending.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                CancelNudge();
                Handle(_memory.Stop(DateTime.UtcNow));
            }
            _latest = float.NaN;
        }

        /// <summary>The player's car and the strength, from SimHub's raw iRacing sample.</summary>
        private static void Read(object raw, out string car, out string name, out float? nm, out bool onTrack)
        {
            car = null; name = null; nm = null; onTrack = false;
            if (raw == null) return;
            dynamic d = raw;
            var telemetry = d.Telemetry as IDictionary<string, object>;
            if (telemetry != null)
            {
                if (telemetry.TryGetValue("SteeringWheelMaxForceNm", out var f) && f != null) nm = Convert.ToSingle(f);
                if (telemetry.TryGetValue("IsOnTrack", out var t) && t != null) onTrack = Convert.ToBoolean(t);
            }
            if (nm.HasValue && (float.IsNaN(nm.Value) || nm.Value <= 0)) nm = null;
            var info = d.SessionData?.DriverInfo;
            if (info == null) return;
            long idx = Convert.ToInt64(info.DriverCarIdx);
            foreach (var driver in info.Drivers)
            {
                if (driver == null || Convert.ToInt64(driver.CarIdx) != idx) continue;
                car = driver.CarPath as string;
                name = driver.CarScreenName as string;
                break;
            }
            if (string.IsNullOrWhiteSpace(car)) car = null;
        }

        /// <summary>Runs under the gate.</summary>
        private void Handle(IReadOnlyList<StrengthAction> actions)
        {
            if (actions.Count == 0) return;
            foreach (var a in actions)
            {
                switch (a.Kind)
                {
                    case StrengthActionKind.Apply:
                        CancelNudge();
                        var cts = _nudge = new CancellationTokenSource();
                        Task.Run(() => Nudge(a.Car, a.Name ?? a.Car, a.Nm, cts.Token));
                        break;
                    case StrengthActionKind.Store:
                        Save(a.Car, a.Name, a.Nm);
                        break;
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void CancelNudge()
        {
            _nudge?.Cancel();
            _nudge = null;
        }

        /// <summary>
        /// Walks the strength to the target one key press at a time, reading the result of each.
        /// Which of the two controls raises the max force isn't assumed: the first press shows it.
        /// Stops on the closest step it can reach.
        /// </summary>
        private async Task Nudge(string car, string name, float target, CancellationToken ct)
        {
            try
            {
                var keys = Keys(_plugin);
                KeyBinding up = keys.Up, down = keys.Down;
                if (!keys.Ready)
                {
                    Finish(ct, $"Couldn't set {target:0.0} Nm for {name}: the keys for iRacing's strength controls aren't set.", true, aboutKeys: true);
                    return;
                }
                SetStatus($"Setting {target:0.0} Nm for {name}…", false);
                SimHub.Logging.Current.Info($"Tech Inspector: setting the wheel max force to {target:0.0} Nm for {name} with {up} / {down}");
                bool learned = false;
                int stuck = 0;
                for (int presses = 0; presses < MaxPresses; presses++)
                {
                    if (!await WaitForFront(name, target, ct)) return;
                    float before = _latest;
                    if (float.IsNaN(before)) { await Task.Delay(100, ct); continue; }
                    float diff = target - before;
                    if (Math.Abs(diff) <= StrengthMemory.Tolerance) { Finish(ct, $"Set {before:0.0} Nm for {name}.", false); return; }

                    KeyPresser.Press(diff > 0 ? up : down);
                    float after = await WaitForChange(before, ct);
                    if (Math.Abs(after - before) < 0.001f)
                    {
                        if (++stuck >= 3) { Finish(ct, $"Couldn't set {target:0.0} Nm for {name}: iRacing didn't react to {up} / {down}.", true); return; }
                        continue;
                    }
                    stuck = 0;
                    if (!learned)
                    {
                        learned = true;
                        // Went the wrong way: the controls are the other way round for max force.
                        if (after > before != diff > 0) { var t = up; up = down; down = t; continue; }
                    }
                    if (Math.Abs(target - after) > StrengthMemory.Tolerance && Math.Sign(target - after) != Math.Sign(diff))
                    {
                        // Stepped past it: keep whichever side is closer.
                        if (Math.Abs(target - after) > Math.Abs(diff))
                        {
                            KeyPresser.Press(diff > 0 ? down : up);
                            after = await WaitForChange(after, ct);
                        }
                        Finish(ct, $"Set {after:0.0} Nm for {name}, the closest step to {target:0.0}.", false);
                        return;
                    }
                }
                Finish(ct, $"Couldn't set {target:0.0} Nm for {name}: too many key presses.", true);
            }
            catch (OperationCanceledException) { /* the car changed or iRacing stopped */ }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("Tech Inspector: setting the wheel strength failed", ex);
                Finish(ct, $"Couldn't set the strength for {name}.", true);
            }
        }

        /// <summary>The keys for iRacing's strength controls, as bound in controls.cfg and as they'll be pressed.</summary>
        public sealed class StrengthKeys
        {
            /// <summary>The bindings in controls.cfg; null when unbound.</summary>
            public KeyBinding BoundUp, BoundDown;
            /// <summary>What gets pressed: the binding, or the key set on the page when the binding can't be read.</summary>
            public KeyBinding Up, Down;

            public bool Ready => Up?.CanPress == true && Down?.CanPress == true;
            public bool Unbound => BoundUp == null || BoundDown == null;
            /// <summary>Bound to keys whose codes can't be read (F-keys, the numpad, + or # on a German keyboard).</summary>
            public bool Unreadable => BoundUp?.CanPress == false || BoundDown?.CanPress == false;
        }

        /// <summary>
        /// The bindings in controls.cfg come first. Where one is a key whose code can't be read, the
        /// same key pressed on the page stands in for it.
        /// </summary>
        public static StrengthKeys Keys(TechInspectorPlugin plugin)
        {
            var cfg = plugin.IracingFolder == null ? null : ControlBindings.Read(ControlBindings.ActivePath(plugin.IracingFolder));
            var k = new StrengthKeys
            {
                BoundUp = ControlBindings.Find(cfg, ControlBindings.IncreaseFfb),
                BoundDown = ControlBindings.Find(cfg, ControlBindings.DecreaseFfb),
            };
            k.Up = k.BoundUp == null ? null : k.BoundUp.CanPress ? k.BoundUp : plugin.Settings.FfbKeyUp;
            k.Down = k.BoundDown == null ? null : k.BoundDown.CanPress ? k.BoundDown : plugin.Settings.FfbKeyDown;
            return k;
        }

        /// <summary>Keys only reach the window in front: wait for the sim to be there.</summary>
        private async Task<bool> WaitForFront(string name, float target, CancellationToken ct)
        {
            var until = DateTime.UtcNow + FrontWait;
            bool said = false;
            while (!KeyPresser.SimInFront())
            {
                if (DateTime.UtcNow > until) { Finish(ct, $"Couldn't set {target:0.0} Nm for {name}: the iRacing window wasn't in front.", true); return false; }
                if (!said) { SetStatus($"Bring the iRacing window to the front to set {target:0.0} Nm for {name}.", false); said = true; }
                await Task.Delay(250, ct);
            }
            return true;
        }

        /// <summary>The strength after a press, once the sim reports a new one (or the old one after a while).</summary>
        private async Task<float> WaitForChange(float before, CancellationToken ct)
        {
            var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(700);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(30, ct);
                float now = _latest;
                if (!float.IsNaN(now) && Math.Abs(now - before) >= 0.001f) { await Task.Delay(60, ct); return _latest; }
            }
            return _latest;
        }

        private void Finish(CancellationToken ct, string message, bool problem, bool aboutKeys = false)
        {
            if (ct.IsCancellationRequested) return;
            lock (_gate)
            {
                _memory.EndApply(retry: problem);
                _nudge = null;
            }
            if (problem) message += " It tries again next time you're in the car.";
            SetStatus(message, problem, aboutKeys);
            if (problem) SimHub.Logging.Current.Warn("Tech Inspector: " + message);
            else SimHub.Logging.Current.Info("Tech Inspector: " + message);
        }

        private bool _statusAboutKeys;

        private void SetStatus(string message, bool problem, bool aboutKeys = false)
        {
            Status = message;
            StatusIsProblem = problem;
            _statusAboutKeys = aboutKeys;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The keys were set or changed: a "keys aren't set" message no longer holds.</summary>
        public void KeysChanged()
        {
            if (!_statusAboutKeys) return;
            Status = null;
            _statusAboutKeys = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forgets a car's saved strength.</summary>
        public void Forget(string car)
        {
            lock (_plugin.Settings) _plugin.Settings.FfbMaxForceByCar.Remove(car);
            _plugin.SaveSettings();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Saves a car's strength in the preferences, written to disk off SimHub's critical path.</summary>
        public void Save(string car, string name, float nm)
        {
            lock (_plugin.Settings)
                _plugin.Settings.FfbMaxForceByCar[car] = new FfbCarStrength { Name = string.IsNullOrEmpty(name) ? car : name, Nm = nm, When = DateTime.Now };
            SimHub.Logging.Current.Info($"Tech Inspector: saved the wheel max force {nm:0.0} Nm for {name ?? car}");
            Task.Run(() =>
            {
                try { _plugin.SaveSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: could not save the preferences", ex); }
            });
        }
    }
}
