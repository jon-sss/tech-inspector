using System;
using System.Collections.Generic;

namespace TechInspector.Core.Ffb
{
    public enum StrengthActionKind { Apply, Store }

    /// <summary>What the caller should do: set the sim's strength, or save a car's strength.</summary>
    public sealed class StrengthAction
    {
        public StrengthActionKind Kind { get; private set; }
        public string Car { get; private set; }
        public string Name { get; private set; }
        public float Nm { get; private set; }

        public static StrengthAction Apply(string car, string name, float nm) => new StrengthAction { Kind = StrengthActionKind.Apply, Car = car, Name = name, Nm = nm };
        public static StrengthAction Store(string car, string name, float nm) => new StrengthAction { Kind = StrengthActionKind.Store, Car = car, Name = name, Nm = nm };

        public override string ToString() => $"{Kind} {Car} {Nm:0.00}";
    }

    /// <summary>
    /// Remembers iRacing's wheel strength per car. Fed with live samples (the player's car, the
    /// strength the sim reports, whether the car is on track), it says when to put a car's saved
    /// strength back and when to save one: after a change settles, when the car is exited, when
    /// another car is loaded and when the sim stops. Knows nothing of SimHub; the clock comes in.
    /// </summary>
    public sealed class StrengthMemory
    {
        /// <summary>Closer than this, the sim already has the saved strength.</summary>
        public const float Tolerance = 0.05f;
        /// <summary>A change must hold this long before it's saved, so a held key saves once.</summary>
        public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
        /// <summary>The longest a set may take before changes count as the user's again.</summary>
        public static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(90);

        private readonly Func<string, float?> _stored;

        private string _car, _name;
        private float? _live;
        private bool _onTrack, _dirty, _needsCheck, _retry, _waitForExit;
        private DateTime _changedAt;
        private float? _target;
        private DateTime _sentAt;

        /// <param name="stored">The saved strength for a car path, or null when there is none.</param>
        public StrengthMemory(Func<string, float?> stored) => _stored = stored;

        /// <summary>The player's car path, while the sim reports one.</summary>
        public string Car => _car;
        public string CarName => _name;
        /// <summary>The strength the sim reports now.</summary>
        public float? Live => _live;
        /// <summary>True while a saved strength is being set; the caller ends it with <see cref="EndApply"/>.</summary>
        public bool Applying => _target.HasValue;
        /// <summary>The strength being set.</summary>
        public float? Target => _target;

        /// <summary>One sample. A null car means no car loaded or the sim stopped.</summary>
        public IReadOnlyList<StrengthAction> Step(string car, string name, float? nm, bool onTrack, DateTime now)
        {
            var actions = new List<StrengthAction>();
            if (!string.Equals(car, _car, StringComparison.OrdinalIgnoreCase))
            {
                Flush(actions);
                _car = car; _name = name; _live = nm; _onTrack = onTrack;
                _dirty = false; _target = null; _needsCheck = car != null; _retry = false; _waitForExit = false;
            }
            if (_car == null) return actions;
            if (!string.IsNullOrEmpty(name)) _name = name;

            if (_needsCheck)
            {
                // The car loaded: put its saved strength back once the player is in it, since iRacing
                // takes the strength controls only then. Until that, changes are iRacing's own as it
                // loads the car, not the user's. After a failed set, wait for the next time in the car;
                // a change by hand meanwhile is the user's choice and ends the wait.
                bool changed = nm.HasValue && _live.HasValue && Math.Abs(nm.Value - _live.Value) > 0.01f;
                if (nm.HasValue) _live = nm;
                if (_retry && changed) { _needsCheck = false; _retry = false; _dirty = true; _changedAt = now; }
                else
                {
                    if (!onTrack) _waitForExit = false;
                    if (onTrack && !_waitForExit && nm.HasValue)
                    {
                        _needsCheck = false; _retry = false;
                        var saved = _stored(_car);
                        if (saved.HasValue && Math.Abs(saved.Value - nm.Value) > Tolerance)
                        {
                            _target = saved; _sentAt = now;
                            actions.Add(StrengthAction.Apply(_car, _name, saved.Value));
                        }
                    }
                }
            }
            else if (_target.HasValue)
            {
                // The set moves the strength step by step; none of that is a change by the user.
                if (nm.HasValue) _live = nm;
                if (now - _sentAt >= ApplyTimeout) EndApply();
            }
            else if (nm.HasValue && (!_live.HasValue || Math.Abs(nm.Value - _live.Value) > 0.01f))
            {
                _live = nm; _dirty = true; _changedAt = now;
            }
            else if (_dirty && now - _changedAt >= Settle)
            {
                actions.Add(StrengthAction.Store(_car, _name, _live.Value));
                _dirty = false;
            }

            if (_onTrack && !onTrack && !_target.HasValue) Flush(actions);
            _onTrack = onTrack;
            return actions;
        }

        /// <summary>
        /// The set finished: what the sim has now is the baseline again, not a change. When it
        /// failed, it's tried again the next time the player gets into the car.
        /// </summary>
        public void EndApply(bool retry = false)
        {
            _target = null;
            _dirty = false;
            if (!retry || _car == null) return;
            _needsCheck = true; _retry = true; _waitForExit = _onTrack;
        }

        /// <summary>The sim stopped: save what's pending.</summary>
        public IReadOnlyList<StrengthAction> Stop(DateTime now) => Step(null, null, null, false, now);

        /// <summary>Saves the current car's strength when it changed, or when the car has none saved yet.</summary>
        private void Flush(List<StrengthAction> actions)
        {
            // Nothing the user chose yet: the car wasn't driven, or the saved strength is still to be set.
            if (_car == null || !_live.HasValue || _target.HasValue || _needsCheck) return;
            if (_dirty || !_stored(_car).HasValue) actions.Add(StrengthAction.Store(_car, _name, _live.Value));
            _dirty = false;
        }
    }
}
