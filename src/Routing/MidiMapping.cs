using System;
using System.Collections.Generic;

namespace DuckGame.MidiController
{
    internal enum BindTarget
    {
        None = 0,

        DrumKick, DrumSnare, DrumHatClosed, DrumHatOpen,
        DrumLowTom, DrumMedTom, DrumHighTom, DrumCrash,

        /// <summary>A melodic scale step; the actual step is carried alongside.</summary>
        MelodicStep,

        Quack,

        KeytarPresetNext,
        Bend,
        Sustain
    }

    /// <summary>Result of routing one MIDI message.</summary>
    internal struct RouteResult
    {
        internal BindTarget target;
        internal int step;        // MelodicStep: 0..12 (0..3 for trumpet). Quack: unused.
        internal float pitch01;   // Quack: 0..1 leftTrigger value.
        internal DrumVoice voice;

        internal static RouteResult None()
        {
            RouteResult r = new RouteResult();
            r.target = BindTarget.None;
            return r;
        }
    }

    /// <summary>An explicit user binding, which always beats the automatic fallback.</summary>
    internal class MidiBind
    {
        internal int channel = -1;      // -1 = any channel
        internal int number;            // note or CC number
        internal bool isControlChange;
        internal BindTarget target;
        internal int step;

        internal bool Matches(MidiMessage m)
        {
            if (channel >= 0 && m.channel != channel) return false;
            if (isControlChange)
                return m.kind == MidiKind.ControlChange && m.number == number;
            return (m.kind == MidiKind.NoteOn || m.kind == MidiKind.NoteOff) && m.number == number;
        }
    }

    /// <summary>
    /// Turns a MIDI message into an instrument action.
    /// </summary>
    /// <remarks>
    /// Resolution order: explicit binds win; otherwise auto-route by the held instrument.
    /// This is what makes the mod work with no configuration at all - a General MIDI
    /// drum machine and a normal keyboard both do the right thing out of the box.
    /// </remarks>
    internal static class MidiMapping
    {
        internal static readonly List<MidiBind> binds = new List<MidiBind>();

        // --- General MIDI percussion key map --------------------------------
        // Duck Game's kit has 8 voices; GM has dozens. Everything sensible is folded
        // onto the nearest equivalent, and anything unlisted snaps to the closest
        // mapped note by pitch distance so no pad is ever silent.
        private static readonly Dictionary<int, DrumVoice> _gmDrums = BuildGmDrums();

        private static Dictionary<int, DrumVoice> BuildGmDrums()
        {
            Dictionary<int, DrumVoice> d = new Dictionary<int, DrumVoice>();
            Add(d, DrumVoice.Kick, 35, 36);
            Add(d, DrumVoice.Snare, 37, 38, 39, 40);
            Add(d, DrumVoice.HatClosed, 42, 44);
            Add(d, DrumVoice.HatOpen, 46);
            Add(d, DrumVoice.LowTom, 41, 43);
            Add(d, DrumVoice.MedTom, 45, 47);
            Add(d, DrumVoice.HighTom, 48, 50);
            Add(d, DrumVoice.Crash, 49, 51, 52, 53, 55, 57, 59);
            return d;
        }

        private static void Add(Dictionary<int, DrumVoice> d, DrumVoice v, params int[] notes)
        {
            for (int i = 0; i < notes.Length; i++) d[notes[i]] = v;
        }

        // --- routing --------------------------------------------------------

        /// <summary>
        /// Routes a note or CC. <paramref name="held"/> is the instrument the local duck
        /// is currently holding, which selects the auto-route behaviour.
        /// </summary>
        internal static RouteResult Route(MidiMessage m, InstrumentKind held)
        {
            // 1. Explicit user bindings always win.
            for (int i = 0; i < binds.Count; i++)
            {
                if (!binds[i].Matches(m)) continue;
                RouteResult r = new RouteResult();
                r.target = binds[i].target;
                r.step = binds[i].step;
                if (IsDrumTarget(binds[i].target))
                    r.voice = VoiceOf(binds[i].target);
                if (binds[i].target == BindTarget.Quack)
                    r.pitch01 = QuackPitch(m.number);
                if (binds[i].target == BindTarget.MelodicStep)
                    r.step = ClampStep(binds[i].step, held);
                return r;
            }

            // 2. Control changes we understand.
            if (m.kind == MidiKind.ControlChange)
            {
                if (m.number == MidiMessage.CcSustainPedal)
                {
                    RouteResult r = new RouteResult();
                    r.target = BindTarget.Sustain;
                    r.step = m.value;
                    return r;
                }
                return RouteResult.None();
            }

            if (m.kind == MidiKind.PitchBend)
            {
                RouteResult r = new RouteResult();
                r.target = BindTarget.Bend;
                r.step = m.value;
                return r;
            }

            if (m.kind != MidiKind.NoteOn && m.kind != MidiKind.NoteOff)
                return RouteResult.None();

            // 3. The dedicated quack channel is always live, whatever is held -
            //    so you can quack a bassline under a saxophone solo.
            if (m.channel == MidiConfig.quackChannel)
            {
                RouteResult r = new RouteResult();
                r.target = BindTarget.Quack;
                r.pitch01 = QuackPitch(m.number);
                return r;
            }

            // 4. Drums: either the kit is held, or the note arrived on GM channel 10.
            bool drumChannel = (m.channel == MidiConfig.DrumChannelIndex);
            if (held == InstrumentKind.DrumSet || drumChannel)
            {
                if (held != InstrumentKind.DrumSet && MidiConfig.drumChannelStrict)
                    return RouteResult.None();   // channel-10 notes shouldn't leak into a sax solo

                RouteResult r = new RouteResult();
                r.target = TargetOf(ResolveDrumVoice(m.number));
                r.voice = ResolveDrumVoice(m.number);
                return r;
            }

            // 5. Melodic auto-route.
            if (Instruments.IsMelodic(held))
            {
                int step;
                if (!TryMelodicStep(m.number, held, out step))
                    return RouteResult.None();
                RouteResult r = new RouteResult();
                r.target = BindTarget.MelodicStep;
                r.step = step;
                return r;
            }

            // 6. Holding nothing playable - fall back to a pitched quack so the mod
            //    always does something audible rather than feeling broken.
            if (MidiConfig.quackWhenEmptyHanded)
            {
                RouteResult r = new RouteResult();
                r.target = BindTarget.Quack;
                r.pitch01 = QuackPitch(m.number);
                return r;
            }

            return RouteResult.None();
        }

        /// <summary>
        /// Maps a MIDI note to a scale step for the held melodic instrument.
        /// </summary>
        /// <remarks>
        /// The instruments have 13 samples (a full octave plus the upper tonic), so
        /// rootNote+12 must land on step 12 rather than folding back to 0.
        /// </remarks>
        private static bool TryMelodicStep(int note, InstrumentKind held, out int step)
        {
            step = 0;
            int raw = note - MidiConfig.rootNote;

            if (raw == 12)
            {
                step = 12;
            }
            else if (raw >= 0 && raw <= 12)
            {
                step = raw;
            }
            else if (MidiConfig.octaveFold)
            {
                step = ((raw % 12) + 12) % 12;
            }
            else
            {
                return false;   // out of range and folding is off
            }

            step = ClampStep(step, held);
            return true;
        }

        /// <summary>
        /// Trumpet only has four fingerings, so a 13-step scale is compressed onto them.
        /// </summary>
        private static int ClampStep(int step, InstrumentKind held)
        {
            if (held == InstrumentKind.Trumpet)
            {
                int p = (step * 4) / 13;          // 0,0,0,0,1,1,1,2,2,2,3,3,3
                if (p < 0) p = 0;
                if (p > 3) p = 3;
                return p;
            }
            if (step < 0) step = 0;
            if (step > 12) step = 12;
            return step;
        }

        /// <summary>Quack pitch, clamped to 0..1.</summary>
        /// <remarks>
        /// The clamp is mandatory, not cosmetic: Duck writes
        /// quackPitch = (byte)(leftTrigger * 255) as an unchecked cast, so a negative
        /// leftTrigger would wrap to a huge value. Consequence: quack pitch only bends
        /// upward, roughly one octave.
        /// </remarks>
        private static float QuackPitch(int note)
        {
            float v = (note - MidiConfig.quackRootNote) / 12f;
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            return v;
        }

        /// <summary>GM note to drum voice, snapping unmapped notes to the nearest mapped one.</summary>
        private static DrumVoice ResolveDrumVoice(int note)
        {
            DrumVoice v;
            if (_gmDrums.TryGetValue(note, out v)) return v;

            int bestDistance = int.MaxValue;
            DrumVoice best = DrumVoice.Snare;
            foreach (KeyValuePair<int, DrumVoice> kv in _gmDrums)
            {
                int dist = Math.Abs(kv.Key - note);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = kv.Value;
                }
            }
            return best;
        }

        internal static bool IsDrumTarget(BindTarget t)
        {
            return t >= BindTarget.DrumKick && t <= BindTarget.DrumCrash;
        }

        internal static DrumVoice VoiceOf(BindTarget t)
        {
            switch (t)
            {
                case BindTarget.DrumKick: return DrumVoice.Kick;
                case BindTarget.DrumSnare: return DrumVoice.Snare;
                case BindTarget.DrumHatClosed: return DrumVoice.HatClosed;
                case BindTarget.DrumHatOpen: return DrumVoice.HatOpen;
                case BindTarget.DrumLowTom: return DrumVoice.LowTom;
                case BindTarget.DrumMedTom: return DrumVoice.MedTom;
                case BindTarget.DrumHighTom: return DrumVoice.HighTom;
                default: return DrumVoice.Crash;
            }
        }

        internal static BindTarget TargetOf(DrumVoice v)
        {
            switch (v)
            {
                case DrumVoice.Kick: return BindTarget.DrumKick;
                case DrumVoice.Snare: return BindTarget.DrumSnare;
                case DrumVoice.HatClosed: return BindTarget.DrumHatClosed;
                case DrumVoice.HatOpen: return BindTarget.DrumHatOpen;
                case DrumVoice.LowTom: return BindTarget.DrumLowTom;
                case DrumVoice.MedTom: return BindTarget.DrumMedTom;
                case DrumVoice.HighTom: return BindTarget.DrumHighTom;
                default: return BindTarget.DrumCrash;
            }
        }

        // --- bind management (used by MIDI-learn and the config file) --------

        internal static void Bind(int channel, int number, bool isCc, BindTarget target, int step)
        {
            // One source binds to one target: drop any existing bind on this source.
            for (int i = binds.Count - 1; i >= 0; i--)
            {
                if (binds[i].channel == channel && binds[i].number == number &&
                    binds[i].isControlChange == isCc)
                    binds.RemoveAt(i);
            }
            if (target == BindTarget.None) return;

            MidiBind b = new MidiBind();
            b.channel = channel;
            b.number = number;
            b.isControlChange = isCc;
            b.target = target;
            b.step = step;
            binds.Add(b);
        }

        /// <summary>Finds the source currently bound to a target, for display in the UI.</summary>
        internal static MidiBind FindBindFor(BindTarget target, int step)
        {
            for (int i = 0; i < binds.Count; i++)
            {
                if (binds[i].target != target) continue;
                if (target == BindTarget.MelodicStep && binds[i].step != step) continue;
                return binds[i];
            }
            return null;
        }

        internal static void ClearBinds()
        {
            binds.Clear();
        }

        internal static string DescribeTarget(BindTarget t, int step)
        {
            switch (t)
            {
                case BindTarget.MelodicStep: return "NOTE " + step;
                case BindTarget.Quack: return "QUACK";
                case BindTarget.KeytarPresetNext: return "KEYTAR PRESET";
                case BindTarget.Bend: return "PITCH BEND";
                case BindTarget.Sustain: return "SUSTAIN";
                case BindTarget.None: return "-";
                default: return Instruments.DisplayName(VoiceOf(t));
            }
        }
    }
}
