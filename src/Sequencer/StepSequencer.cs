using System;

namespace DuckGame.MidiController
{
    internal enum ClockSource
    {
        Internal = 0,
        External
    }

    /// <summary>
    /// A 16-step drum box with a monophonic melodic line, driven either by its own tempo
    /// or by MIDI clock from your hardware.
    /// </summary>
    /// <remarks>
    /// TIMING. Steps advance in PreUpdate, which fires once per game frame at 60fps, so
    /// step boundaries are quantised to ~16.7ms. At 120 BPM a sixteenth is 7.5 frames, i.e.
    /// roughly +/-8ms of jitter. That is not DAW-tight and swing is correspondingly coarse.
    /// It is still the right trade: a frame-based clock runs on game time, so it pauses
    /// when the game pauses and never drifts away from what is happening on screen. A
    /// wall-clock would stay in tempo while the game was frozen and then lurch.
    ///
    /// The transport keeps running even when you are not holding an instrument. Stopping it
    /// would be easier, but then picking the kit back up would drop you in at a random
    /// point in the bar; this way you rejoin in time.
    /// </remarks>
    internal class StepSequencer
    {
        internal const int SlotCount = 4;
        private const float FramesPerSecond = 60f;
        private const int StepsPerBeat = 4;          // sixteenths
        private const int PulsesPerQuarter = 24;     // MIDI standard
        private const int PulsesPerStep = PulsesPerQuarter / StepsPerBeat;   // 6

        /// <summary>How long we keep trusting external clock after the last pulse.</summary>
        private const int ExternalClockTimeoutFrames = 90;   // ~1.5s

        private readonly StepPattern[] _slots = new StepPattern[SlotCount];
        private int _slot;

        private bool _playing;
        private int _step;            // index within the pattern
        private float _phase;         // progress through the current step, in step units

        private int _pulses;          // external clock pulses since the last step
        private int _framesSinceClock = int.MaxValue;

        private int _bpm = 120;
        private float _swing;         // 0..0.45, fraction of a step

        private readonly bool[] _drumMute = new bool[(int)DrumVoice.Count];
        private bool _melodicMute;

        private bool _recording;
        private int _sequencedNote = -1;   // MIDI note currently sounding from the pattern

        internal StepSequencer()
        {
            for (int i = 0; i < SlotCount; i++) _slots[i] = new StepPattern();
        }

        // --- state ----------------------------------------------------------

        internal StepPattern pattern { get { return _slots[_slot]; } }
        internal int slot { get { return _slot; } }
        internal bool playing { get { return _playing; } }
        internal bool recording { get { return _recording; } }
        internal int currentStep { get { return _step; } }

        internal ClockSource clockSource
        {
            get
            {
                return _framesSinceClock <= ExternalClockTimeoutFrames
                    ? ClockSource.External : ClockSource.Internal;
            }
        }

        internal int bpm
        {
            get { return _bpm; }
            set
            {
                if (value < 40) value = 40;
                if (value > 300) value = 300;
                _bpm = value;
            }
        }

        internal float swing
        {
            get { return _swing; }
            set
            {
                if (value < 0f) value = 0f;
                if (value > 0.45f) value = 0.45f;
                _swing = value;
            }
        }

        internal bool GetMute(DrumVoice v) { return _drumMute[(int)v]; }
        internal void ToggleMute(DrumVoice v) { _drumMute[(int)v] = !_drumMute[(int)v]; }
        internal bool melodicMute
        {
            get { return _melodicMute; }
            set { _melodicMute = value; }
        }

        internal void SelectSlot(int i)
        {
            if (i < 0 || i >= SlotCount) return;
            _slot = i;
        }

        // --- transport ------------------------------------------------------

        internal void Play()
        {
            _playing = true;
        }

        internal void Stop(InstrumentRouter router)
        {
            _playing = false;
            _recording = false;
            ReleaseSequencedNote(router);
            _step = 0;
            _phase = 0f;
            _pulses = 0;
        }

        internal void TogglePlay(InstrumentRouter router)
        {
            if (_playing) Stop(router);
            else Play();
        }

        internal void ToggleRecord()
        {
            _recording = !_recording;
            if (_recording) _playing = true;
        }

        // --- clock ----------------------------------------------------------

        /// <summary>Feeds a system-realtime message from the hardware transport.</summary>
        internal void HandleTransport(MidiMessage m, InstrumentRouter router)
        {
            switch (m.kind)
            {
                case MidiKind.Clock:
                    _framesSinceClock = 0;
                    if (_playing) _pulses++;
                    break;
                case MidiKind.Start:
                    _step = 0;
                    _phase = 0f;
                    _pulses = 0;
                    _playing = true;
                    break;
                case MidiKind.Continue:
                    _playing = true;
                    break;
                case MidiKind.Stop:
                    Stop(router);
                    break;
            }
        }

        /// <summary>Length of a step in step-units, applying swing as a long/short pair.</summary>
        /// <remarks>
        /// Shuffle is expressed by lengthening even steps and shortening odd ones by the
        /// same amount, so a pair always spans exactly two steps and the bar never drifts.
        /// External clock ignores swing - hardware applies its own, and fighting it would
        /// double the shuffle.
        /// </remarks>
        private float StepDuration(int step)
        {
            if (_swing <= 0f) return 1f;
            return (step % 2 == 0) ? (1f + _swing) : (1f - _swing);
        }

        // --- per-frame ------------------------------------------------------

        internal void Tick(InstrumentKind held, InstrumentRouter router)
        {
            if (_framesSinceClock < int.MaxValue) _framesSinceClock++;
            if (!_playing) return;

            if (clockSource == ClockSource.External)
            {
                // Hardware drives us: advance a step every six pulses.
                while (_pulses >= PulsesPerStep)
                {
                    _pulses -= PulsesPerStep;
                    AdvanceStep(held, router);
                }
                return;
            }

            float stepsPerFrame = (_bpm / 60f) * StepsPerBeat / FramesPerSecond;
            _phase += stepsPerFrame;

            int guard = 0;
            while (_phase >= StepDuration(_step))
            {
                _phase -= StepDuration(_step);
                AdvanceStep(held, router);
                // A silly BPM plus a long frame hitch could otherwise spin here.
                if (++guard > 8) { _phase = 0f; break; }
            }
        }

        private void AdvanceStep(InstrumentKind held, InstrumentRouter router)
        {
            StepPattern p = pattern;
            _step++;
            if (_step >= p.length) _step = 0;
            EmitStep(p, _step, held, router);
        }

        /// <summary>
        /// Plays one step. Tracks only sound when the matching instrument is actually held -
        /// the transport keeps running regardless so you rejoin in time.
        /// </summary>
        private void EmitStep(StepPattern p, int step, InstrumentKind held, InstrumentRouter router)
        {
            if (router == null) return;

            if (held == InstrumentKind.DrumSet)
            {
                int mask = p.DrumMask(step);
                if (mask != 0)
                {
                    for (int v = 0; v < (int)DrumVoice.Count; v++)
                    {
                        if ((mask & (1 << v)) == 0) continue;
                        if (_drumMute[v]) continue;
                        router.SequencerDrumHit((DrumVoice)v);
                    }
                }
                return;
            }

            if (Instruments.IsMelodic(held))
            {
                // Release first: the melodic state machine articulates a new note by
                // seeing the active note change, and sax/trombone need the old one gone.
                ReleaseSequencedNote(router);
                if (_melodicMute) return;

                int scaleStep = p.GetNote(step);
                if (scaleStep == StepPattern.Rest) return;

                _sequencedNote = MidiConfig.rootNote + scaleStep;
                router.SequencerNoteOn(_sequencedNote);
            }
        }

        private void ReleaseSequencedNote(InstrumentRouter router)
        {
            if (_sequencedNote < 0) return;
            if (router != null) router.SequencerNoteOff(_sequencedNote);
            _sequencedNote = -1;
        }

        /// <summary>Called when the instrument changes or we detach, so notes don't hang.</summary>
        internal void OnInstrumentLost(InstrumentRouter router)
        {
            ReleaseSequencedNote(router);
        }

        // --- recording ------------------------------------------------------

        /// <summary>
        /// The step a live hit should land on: whichever boundary it is nearest, so playing
        /// slightly ahead of the beat records on the beat rather than a step early.
        /// </summary>
        private int QuantizedStep()
        {
            StepPattern p = pattern;
            int s = _step;
            if (_phase > StepDuration(_step) * 0.5f)
            {
                s++;
                if (s >= p.length) s = 0;
            }
            return s;
        }

        internal void RecordDrum(DrumVoice v)
        {
            if (!_recording) return;
            pattern.SetDrum(v, QuantizedStep(), true);
        }

        internal void RecordNote(int scaleStep)
        {
            if (!_recording) return;
            pattern.SetNote(QuantizedStep(), scaleStep);
        }

        // --- persistence ----------------------------------------------------

        internal string SerializeSettings()
        {
            return _slot + ":" + _bpm + ":" + _swing.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        internal void DeserializeSettings(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string[] p = value.Split(':');
            int v;
            if (p.Length > 0 && int.TryParse(p[0], out v)) SelectSlot(v);
            if (p.Length > 1 && int.TryParse(p[1], out v)) bpm = v;
            float f;
            if (p.Length > 2 && float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out f)) swing = f;
        }

        internal StepPattern SlotAt(int i)
        {
            if (i < 0 || i >= SlotCount) return null;
            return _slots[i];
        }
    }
}
