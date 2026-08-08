using System;
using System.Collections.Generic;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Mono voice allocation.
    /// </summary>
    /// <remarks>
    /// Every Duck Game instrument is monophonic - one Sound object, one notePitch. A
    /// MIDI keyboard is not, so something has to decide which held key is sounding.
    /// LastNoteWins is the classic mono-synth behaviour and the default: the newest key
    /// takes over, and releasing it falls back to whatever is still held.
    /// </remarks>
    internal class NoteStack
    {
        private readonly List<int> _held = new List<int>();      // MIDI note numbers, oldest first
        private readonly List<int> _sustained = new List<int>(); // released while the pedal is down
        private bool _sustainDown;

        internal int count { get { return _held.Count; } }

        internal void NoteOn(int note)
        {
            _held.Remove(note);
            _held.Add(note);
            _sustained.Remove(note);
        }

        internal void NoteOff(int note)
        {
            if (_sustainDown)
            {
                if (_held.Contains(note) && !_sustained.Contains(note))
                    _sustained.Add(note);
                return;
            }
            _held.Remove(note);
        }

        internal void SetSustain(bool down)
        {
            _sustainDown = down;
            if (down) return;
            // Pedal released: everything that was let go while it was down now stops.
            for (int i = 0; i < _sustained.Count; i++)
                _held.Remove(_sustained[i]);
            _sustained.Clear();
        }

        internal void Clear()
        {
            _held.Clear();
            _sustained.Clear();
            _sustainDown = false;
        }

        /// <summary>The MIDI note that should currently be sounding, or -1 for silence.</summary>
        internal int Active()
        {
            if (_held.Count == 0) return -1;

            switch (MidiConfig.polyphony)
            {
                case PolyphonyPolicy.HighestNote:
                {
                    int best = _held[0];
                    for (int i = 1; i < _held.Count; i++)
                        if (_held[i] > best) best = _held[i];
                    return best;
                }
                case PolyphonyPolicy.LowestNote:
                {
                    int best = _held[0];
                    for (int i = 1; i < _held.Count; i++)
                        if (_held[i] < best) best = _held[i];
                    return best;
                }
                default:
                    return _held[_held.Count - 1];   // most recently pressed
            }
        }
    }
}
