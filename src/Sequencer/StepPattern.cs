using System;
using System.Text;

namespace DuckGame.MidiController
{
    /// <summary>
    /// One pattern: eight drum tracks plus a single melodic line.
    /// </summary>
    /// <remarks>
    /// The melodic track is deliberately monophonic. Duck Game's instruments each have one
    /// Sound and one notePitch, so a chord is not representable - a second simultaneous
    /// note would simply replace the first. One line per pattern matches what the game can
    /// actually play.
    ///
    /// Drum steps are stored as a bitmask per step rather than eight parallel arrays: it
    /// keeps a step's whole chord in one value, makes clearing and copying trivial, and
    /// serialises to a single number per step.
    /// </remarks>
    internal class StepPattern
    {
        /// <summary>
        /// Capped at 16 because that is what the grid can actually show. The HUD camera is
        /// 320x180 units and menu text renders at roughly 11 units per character, so a
        /// label plus 16 cells already fills the dialog. A 32-step pattern would have to be
        /// scrolled or shrunk past legibility, and "16-step box" is the design anyway.
        /// Shorter patterns still work, for odd time signatures.
        /// </summary>
        internal const int MaxSteps = 16;
        internal const int DefaultSteps = 16;
        internal const int Rest = -1;

        /// <summary>Bit i set means DrumVoice i fires on that step.</summary>
        private readonly int[] _drum = new int[MaxSteps];
        /// <summary>Scale step 0..12 per step, or Rest.</summary>
        private readonly int[] _melodic = new int[MaxSteps];

        private int _length = DefaultSteps;

        internal StepPattern()
        {
            Clear();
        }

        internal int length
        {
            get { return _length; }
            set
            {
                if (value < 1) value = 1;
                if (value > MaxSteps) value = MaxSteps;
                _length = value;
            }
        }

        // --- drums ----------------------------------------------------------

        internal bool GetDrum(DrumVoice v, int step)
        {
            if (step < 0 || step >= MaxSteps) return false;
            return (_drum[step] & (1 << (int)v)) != 0;
        }

        internal void SetDrum(DrumVoice v, int step, bool on)
        {
            if (step < 0 || step >= MaxSteps) return;
            if (on) _drum[step] |= (1 << (int)v);
            else _drum[step] &= ~(1 << (int)v);
        }

        internal void ToggleDrum(DrumVoice v, int step)
        {
            SetDrum(v, step, !GetDrum(v, step));
        }

        internal int DrumMask(int step)
        {
            if (step < 0 || step >= MaxSteps) return 0;
            return _drum[step];
        }

        // --- melodic --------------------------------------------------------

        internal int GetNote(int step)
        {
            if (step < 0 || step >= MaxSteps) return Rest;
            return _melodic[step];
        }

        internal void SetNote(int step, int scaleStep)
        {
            if (step < 0 || step >= MaxSteps) return;
            if (scaleStep != Rest)
            {
                if (scaleStep < 0) scaleStep = 0;
                if (scaleStep > 12) scaleStep = 12;
            }
            _melodic[step] = scaleStep;
        }

        // --- bulk -----------------------------------------------------------

        internal void Clear()
        {
            for (int i = 0; i < MaxSteps; i++)
            {
                _drum[i] = 0;
                _melodic[i] = Rest;
            }
        }

        internal void ClearDrumTrack(DrumVoice v)
        {
            for (int i = 0; i < MaxSteps; i++) SetDrum(v, i, false);
        }

        internal void ClearMelodicTrack()
        {
            for (int i = 0; i < MaxSteps; i++) _melodic[i] = Rest;
        }

        internal bool isEmpty
        {
            get
            {
                for (int i = 0; i < _length; i++)
                {
                    if (_drum[i] != 0) return false;
                    if (_melodic[i] != Rest) return false;
                }
                return true;
            }
        }

        // --- persistence ----------------------------------------------------
        // Serialised as one compact line per pattern so config.txt stays hand-editable
        // and a pattern can be pasted to someone else:
        //   seq=<slot>:<length>:<drumMask,drumMask,...>:<note,note,...>

        internal string Serialize(int slot)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(slot).Append(':').Append(_length).Append(':');
            for (int i = 0; i < _length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_drum[i]);
            }
            sb.Append(':');
            for (int i = 0; i < _length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_melodic[i]);
            }
            return sb.ToString();
        }

        /// <summary>Parses a serialised pattern. Returns the slot, or -1 if unreadable.</summary>
        internal static int Deserialize(string value, StepPattern into)
        {
            if (into == null || string.IsNullOrEmpty(value)) return -1;
            string[] parts = value.Split(':');
            if (parts.Length < 4) return -1;

            int slot, len;
            if (!int.TryParse(parts[0], out slot)) return -1;
            if (!int.TryParse(parts[1], out len)) return -1;

            into.Clear();
            into.length = len;

            string[] drums = parts[2].Split(',');
            for (int i = 0; i < drums.Length && i < MaxSteps; i++)
            {
                int mask;
                if (int.TryParse(drums[i], out mask)) into._drum[i] = mask;
            }

            string[] notes = parts[3].Split(',');
            for (int i = 0; i < notes.Length && i < MaxSteps; i++)
            {
                int n;
                if (int.TryParse(notes[i], out n)) into._melodic[i] = (n < 0 ? Rest : (n > 12 ? 12 : n));
            }

            return slot;
        }
    }
}
