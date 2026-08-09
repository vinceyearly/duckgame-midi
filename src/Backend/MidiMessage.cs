using System;

namespace DuckGame.MidiController
{
    internal enum MidiKind
    {
        Unknown = 0,
        NoteOn,
        NoteOff,
        ControlChange,
        PitchBend,

        // System realtime. These carry no channel and can arrive between the bytes of
        // any other message. Hardware with a transport sends them; the MPK Mini does not.
        Clock,      // 0xF8, 24 per quarter note
        Start,      // 0xFA, restart from the top
        Continue,   // 0xFB, resume from where we stopped
        Stop        // 0xFC
    }

    /// <summary>
    /// A decoded MIDI short message.
    /// </summary>
    /// <remarks>
    /// We decode the raw 32-bit word ourselves rather than using NAudio's MidiEvent
    /// class hierarchy. That keeps the reflection surface down to a single Int32
    /// property and makes us immune to NAudio version drift. See NAudioReflection.
    /// </remarks>
    internal struct MidiMessage
    {
        internal MidiKind kind;
        /// <summary>0-based. What hardware calls "channel 10" is index 9.</summary>
        internal int channel;
        /// <summary>Note number for Note*, controller number for ControlChange.</summary>
        internal int number;
        /// <summary>Velocity for Note*, value for ControlChange, 0..16383 for PitchBend.</summary>
        internal int value;
        internal int raw;

        internal const int CcSustainPedal = 64;
        internal const int CcAllNotesOff = 123;

        internal static MidiMessage Decode(int raw)
        {
            MidiMessage m = new MidiMessage();
            m.raw = raw;

            int status = raw & 0xFF;
            int d1 = (raw >> 8) & 0x7F;
            int d2 = (raw >> 16) & 0x7F;

            // 0xF0..0xFF are system messages and carry no channel.
            if (status >= 0xF0)
            {
                switch (status)
                {
                    case 0xF8: m.kind = MidiKind.Clock; break;
                    case 0xFA: m.kind = MidiKind.Start; break;
                    case 0xFB: m.kind = MidiKind.Continue; break;
                    case 0xFC: m.kind = MidiKind.Stop; break;
                    default: m.kind = MidiKind.Unknown; break;   // sysex, MTC, tune request
                }
                return m;
            }

            m.channel = status & 0x0F;
            int cmd = status & 0xF0;

            switch (cmd)
            {
                case 0x90:
                    // Note-on with velocity 0 is a note-off. This is the running-status
                    // convention and most controllers use it instead of real 0x80.
                    m.kind = (d2 == 0) ? MidiKind.NoteOff : MidiKind.NoteOn;
                    m.number = d1;
                    m.value = d2;
                    break;
                case 0x80:
                    m.kind = MidiKind.NoteOff;
                    m.number = d1;
                    m.value = d2;
                    break;
                case 0xB0:
                    m.kind = MidiKind.ControlChange;
                    m.number = d1;
                    m.value = d2;
                    break;
                case 0xE0:
                    m.kind = MidiKind.PitchBend;
                    m.number = 0;
                    m.value = (d2 << 7) | d1;   // 0..16383, centre 8192
                    break;
                default:
                    m.kind = MidiKind.Unknown;
                    break;
            }
            return m;
        }

        /// <summary>Human-readable form for the MIDI monitor, e.g. "ch1 NoteOn C4 (60) v100".</summary>
        public override string ToString()
        {
            switch (kind)
            {
                case MidiKind.NoteOn:
                    return "ch" + (channel + 1) + " ON  " + NoteName(number) + " (" + number + ") v" + value;
                case MidiKind.NoteOff:
                    return "ch" + (channel + 1) + " OFF " + NoteName(number) + " (" + number + ")";
                case MidiKind.ControlChange:
                    return "ch" + (channel + 1) + " CC" + number + " = " + value;
                case MidiKind.PitchBend:
                    return "ch" + (channel + 1) + " BEND " + value;
                case MidiKind.Clock:
                    return "clock";
                case MidiKind.Start:
                    return "START";
                case MidiKind.Continue:
                    return "CONTINUE";
                case MidiKind.Stop:
                    return "STOP";
                default:
                    return "raw 0x" + raw.ToString("X6");
            }
        }

        private static readonly string[] _noteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>MIDI note number to scientific pitch notation. 60 is C4 (middle C).</summary>
        internal static string NoteName(int note)
        {
            if (note < 0 || note > 127)
                return "?";
            return _noteNames[note % 12] + ((note / 12) - 1).ToString();
        }
    }
}
