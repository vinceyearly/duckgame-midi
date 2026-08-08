using System;
using System.Collections.Generic;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Per-frame driver: drains the MIDI queue, routes it, and installs our profile on
    /// the local duck.
    /// </summary>
    /// <remarks>
    /// Registered with MonoMain.RegisterEngineUpdatable. PreUpdate runs before
    /// Level.UpdateCurrentLevel, so triggers emitted there are seen by the instruments
    /// in the same frame - no added latency.
    /// </remarks>
    public class MidiEngine : IEngineUpdatable
    {
        private readonly MidiInputProfile _profile = new MidiInputProfile();
        private readonly InstrumentRouter _router = new InstrumentRouter();

        private Duck _attachedDuck;
        private InstrumentKind _heldInstrument = InstrumentKind.None;
        private Level _lastLevel;
        private bool _startedHardware;
        /// <summary>Set in PreUpdate, consumed and cleared in PostUpdate.</summary>
        private bool _preUpdateRanThisFrame;

        // Diagnostics surfaced by `midi status`, the HUD and the monitor.
        private readonly Queue<string> _monitor = new Queue<string>();
        private const int MonitorCapacity = 12;
        private string _lastNoteDescription = "";
        private int _framesSinceActivity = int.MaxValue;

        /// <summary>Synthetic messages queued by `midi test`, keyed by frames remaining.</summary>
        private readonly List<PendingInject> _pendingInjects = new List<PendingInject>();

        private struct PendingInject
        {
            internal int raw;
            internal int framesLeft;
        }

        // MIDI-learn handoff: when armed, the next message is captured for the UI
        // instead of being played.
        private bool _learnArmed;
        private bool _learnHasResult;
        private MidiMessage _learnResult;

        internal InstrumentKind heldInstrument { get { return _heldInstrument; } }
        internal bool isAttached { get { return _attachedDuck != null; } }
        internal string lastNoteDescription { get { return _lastNoteDescription; } }
        internal int framesSinceActivity { get { return _framesSinceActivity; } }

        // --- lifecycle ------------------------------------------------------

        internal void StartIfEnabled()
        {
            if (_startedHardware) return;
            _startedHardware = true;

            if (!NAudioReflection.available)
            {
                Log.Warn("MIDI unavailable: " + NAudioReflection.failureReason);
                Log.Warn("The settings menu and 'midi spawn' still work.");
                return;
            }
            if (!DuckHook.available)
            {
                Log.Error(DuckHook.unavailableReason);
                return;
            }

            MidiListener.RefreshDeviceNames();
            MidiListener.StartHotplugWatch();

            if (!MidiConfig.enabled) return;

            // Zero-config: on a fresh install, just pick the obvious device and go.
            if (string.IsNullOrEmpty(MidiConfig.deviceName))
            {
                int idx = MidiListener.PickDefaultDevice();
                if (idx < 0)
                {
                    Log.Info("no MIDI input devices found. Plug one in - it'll connect automatically.");
                    return;
                }
                if (MidiListener.Open(idx))
                {
                    MidiConfig.deviceName = MidiListener.openDeviceName;
                    MidiConfig.Save();
                    Log.Good("connected to \"" + MidiListener.openDeviceName + "\" automatically.");
                }
                else
                {
                    Log.Warn("could not open \"" + MidiListener.GetDeviceNames()[idx] + "\": " + MidiListener.lastError);
                }
                return;
            }

            if (!MidiListener.OpenByName(MidiConfig.deviceName))
            {
                Log.Warn("MIDI device \"" + MidiConfig.deviceName + "\" not connected yet - watching for it.");
            }
            else
            {
                Log.Good("connected to \"" + MidiListener.openDeviceName + "\".");
            }
        }

        internal void Shutdown()
        {
            Detach();
            MidiListener.Shutdown();
        }

        // --- IEngineUpdatable -----------------------------------------------

        public void PreUpdate()
        {
            _preUpdateRanThisFrame = true;

            try
            {
                if (_framesSinceActivity < int.MaxValue) _framesSinceActivity++;
                TickPendingInjects();

                // A level change invalidates every Duck reference we hold.
                if (!object.ReferenceEquals(Level.current, _lastLevel))
                {
                    _lastLevel = Level.current;
                    Detach();
                }

                if (!MidiConfig.enabled || !MidiListener.isOpen)
                {
                    Detach();
                    MidiListener.DrainAndDiscard();
                    return;
                }

                Duck duck = DuckHook.LocalDuck();
                if (duck == null || MonoMain.pauseMenu != null)
                {
                    Detach();
                    DrainIntoMonitorOnly();
                    return;
                }

                // Re-resolve what is held EVERY frame, before emitting anything. This is
                // the guard that stops us injecting SHOOT into a real gun if the player
                // swaps weapons mid-note.
                _heldInstrument = Instruments.Detect(duck.holdObject);

                Attach(duck);
                _profile.BeginFrame(ResolveRealProfile(duck));

                DrainQueue(true);

                bool suppressShoot = _profile.justAttached;
                _profile.justAttached = false;
                _router.Emit(_profile, _heldInstrument, suppressShoot);
            }
            catch (Exception e)
            {
                Log.Throttled("preupdate", 30.0, "error in update: " + e.Message);
            }
        }

        public void Update() { }

        public void PostUpdate()
        {
            try
            {
                // While the pause menu is up, shouldPauseGameplay is true and PreUpdate
                // does not run - so the queue would back up and MIDI-learn would never
                // see a message. PostUpdate always runs, so drain here for UI purposes
                // only, never into an InputProfile.
                if (!_preUpdateRanThisFrame)
                    DrainIntoMonitorOnly();
                _preUpdateRanThisFrame = false;

                MidiSettingsMenu.HandleHotkey();
            }
            catch (Exception e)
            {
                Log.Throttled("postupdate", 30.0, "error in post-update: " + e.Message);
            }
        }

        public void OnDrawLayer(Layer layer)
        {
            try { MidiHud.Draw(layer, this); }
            catch { }
        }

        // --- queue draining -------------------------------------------------

        private void DrainQueue(bool allowPlay)
        {
            int raw;
            int guard = 0;
            while (MidiListener.TryDequeue(out raw))
            {
                MidiMessage m = MidiMessage.Decode(raw);
                if (m.kind == MidiKind.Unknown) continue;

                RecordForMonitor(m);

                if (_learnArmed)
                {
                    // Only note-ons and CCs are useful as bindable sources.
                    if (m.kind == MidiKind.NoteOn || m.kind == MidiKind.ControlChange)
                    {
                        _learnResult = m;
                        _learnHasResult = true;
                        _learnArmed = false;
                    }
                    continue;
                }

                if (allowPlay)
                    _router.HandleMessage(m, _heldInstrument);

                // Bound the work per frame so a stuck controller can't stall the game.
                if (++guard > 256) break;
            }
        }

        private void DrainIntoMonitorOnly()
        {
            DrainQueue(false);
        }

        private void RecordForMonitor(MidiMessage m)
        {
            _monitor.Enqueue(m.ToString());
            while (_monitor.Count > MonitorCapacity) _monitor.Dequeue();

            if (m.kind == MidiKind.NoteOn)
            {
                _lastNoteDescription = MidiMessage.NoteName(m.number) + " v" + m.value;
                _framesSinceActivity = 0;
            }
            else if (m.kind != MidiKind.NoteOff)
            {
                _framesSinceActivity = 0;
            }
        }

        internal string[] GetMonitorLines()
        {
            return _monitor.ToArray();
        }

        // --- synthetic injection (`midi test`) -------------------------------

        /// <summary>Queues a raw message to be injected in <paramref name="delayFrames"/> frames.</summary>
        internal void ScheduleInject(int raw, int delayFrames)
        {
            if (delayFrames <= 0)
            {
                MidiListener.InjectRaw(raw);
                return;
            }
            PendingInject p = new PendingInject();
            p.raw = raw;
            p.framesLeft = delayFrames;
            _pendingInjects.Add(p);
        }

        private void TickPendingInjects()
        {
            for (int i = _pendingInjects.Count - 1; i >= 0; i--)
            {
                PendingInject p = _pendingInjects[i];
                p.framesLeft--;
                if (p.framesLeft <= 0)
                {
                    MidiListener.InjectRaw(p.raw);
                    _pendingInjects.RemoveAt(i);
                }
                else
                {
                    _pendingInjects[i] = p;
                }
            }
        }

        internal void ClearPendingInjects()
        {
            _pendingInjects.Clear();
        }

        // --- MIDI learn -----------------------------------------------------

        internal void ArmLearn()
        {
            _learnArmed = true;
            _learnHasResult = false;
        }

        internal void CancelLearn()
        {
            _learnArmed = false;
            _learnHasResult = false;
        }

        internal bool TryTakeLearned(out MidiMessage m)
        {
            m = _learnResult;
            if (!_learnHasResult) return false;
            _learnHasResult = false;
            return true;
        }

        // --- attach / detach ------------------------------------------------

        private void Attach(Duck duck)
        {
            if (object.ReferenceEquals(_attachedDuck, duck)) return;

            Detach();

            InputProfile existing = DuckHook.GetVirtualInput(duck);
            if (existing != null && !(existing is MidiInputProfile))
            {
                // Something else already owns this slot - an AI-controlled duck, or
                // another mod. Leave it alone rather than fight over it.
                return;
            }

            if (!DuckHook.SetVirtualInput(duck, _profile))
                return;

            _attachedDuck = duck;
            _profile.justAttached = true;
        }

        internal void Detach()
        {
            if (_attachedDuck != null)
            {
                InputProfile current = DuckHook.GetVirtualInput(_attachedDuck);
                // Only clear the slot if it is still ours.
                if (object.ReferenceEquals(current, _profile))
                    DuckHook.SetVirtualInput(_attachedDuck, null);
                _attachedDuck = null;
            }
            _router.Panic(_profile);
            _profile.ClearAll();
            _heldInstrument = InstrumentKind.None;
        }

        internal void Panic()
        {
            _router.Panic(_profile);
            MidiListener.DrainAndDiscard();
            Log.Good("all notes stopped.");
        }

        /// <summary>The duck's genuine profile, bypassing our own wrapper.</summary>
        private static InputProfile ResolveRealProfile(Duck duck)
        {
            try
            {
                if (duck.profile != null && duck.profile.inputProfile != null)
                    return duck.profile.inputProfile;
            }
            catch { }
            return null;
        }
    }
}
