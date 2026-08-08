using System;

namespace DuckGame.MidiController
{
    internal enum InstrumentKind
    {
        None = 0,
        Saxaphone,      // spelled as the game spells it
        Trombone,
        Keytar,
        Trumpet,
        DrumSet
    }

    internal enum DrumVoice
    {
        Kick = 0,
        Snare,
        HatClosed,
        HatOpen,
        LowTom,
        MedTom,
        HighTom,
        Crash,
        Count
    }

    internal static class Instruments
    {
        /// <summary>
        /// Identifies the held instrument by exact type.
        /// </summary>
        /// <remarks>
        /// Exact-type matching, not `is Gun`, on purpose: a TapedGun combining an
        /// instrument with a real weapon must NOT be treated as an instrument, or we
        /// could inject SHOOT into something that fires.
        /// </remarks>
        internal static InstrumentKind Detect(Thing held)
        {
            if (held == null) return InstrumentKind.None;
            Type t = held.GetType();
            if (t == typeof(Saxaphone)) return InstrumentKind.Saxaphone;
            if (t == typeof(Trombone)) return InstrumentKind.Trombone;
            if (t == typeof(Keytar)) return InstrumentKind.Keytar;
            if (t == typeof(Trumpet)) return InstrumentKind.Trumpet;
            if (t == typeof(DrumSet)) return InstrumentKind.DrumSet;
            return InstrumentKind.None;
        }

        internal static bool IsMelodic(InstrumentKind k)
        {
            return k == InstrumentKind.Saxaphone
                || k == InstrumentKind.Trombone
                || k == InstrumentKind.Keytar
                || k == InstrumentKind.Trumpet;
        }

        internal static string DisplayName(InstrumentKind k)
        {
            switch (k)
            {
                case InstrumentKind.Saxaphone: return "SAXOPHONE";
                case InstrumentKind.Trombone: return "TROMBONE";
                case InstrumentKind.Keytar: return "KEYTAR";
                case InstrumentKind.Trumpet: return "TRUMPET";
                case InstrumentKind.DrumSet: return "DRUM KIT";
                default: return "NONE";
            }
        }

        internal static string DisplayName(DrumVoice v)
        {
            switch (v)
            {
                case DrumVoice.Kick: return "KICK";
                case DrumVoice.Snare: return "SNARE";
                case DrumVoice.HatClosed: return "HI-HAT (CLOSED)";
                case DrumVoice.HatOpen: return "HI-HAT (OPEN)";
                case DrumVoice.LowTom: return "LOW TOM";
                case DrumVoice.MedTom: return "MID TOM";
                case DrumVoice.HighTom: return "HIGH TOM";
                case DrumVoice.Crash: return "CRASH";
                default: return "?";
            }
        }

        /// <summary>
        /// The trigger each drum voice is bolted to in DrumSet.Update.
        /// </summary>
        /// <remarks>
        /// Kick is driven by STRAFE rather than RAGDOLL - the game accepts either, but
        /// STRAFE is a harmless aim-lock whereas RAGDOLL would be a real hazard if the
        /// held-object guard ever regressed.
        ///
        /// Injecting the directional triggers is safe here specifically because DrumSet
        /// zeroes the duck's hSpeed/vSpeed every frame while held and Duck pins its
        /// position to the kit, so none of these can produce movement.
        /// </remarks>
        internal static string TriggerFor(DrumVoice v)
        {
            switch (v)
            {
                case DrumVoice.Kick: return Triggers.Strafe;
                case DrumVoice.Snare: return Triggers.Shoot;
                case DrumVoice.HatClosed: return Triggers.Jump;
                case DrumVoice.HatOpen: return Triggers.LeftTrigger;
                case DrumVoice.LowTom: return Triggers.Left;
                case DrumVoice.MedTom: return Triggers.Down;
                case DrumVoice.HighTom: return Triggers.Right;
                case DrumVoice.Crash: return Triggers.Up;
                default: return null;
            }
        }

        /// <summary>How many distinct scale steps an instrument actually has.</summary>
        internal static int StepCount(InstrumentKind k)
        {
            if (k == InstrumentKind.Trumpet) return 4;    // trumpet01..trumpet04
            if (IsMelodic(k)) return 13;                  // sax0..sax12 etc.
            return 0;
        }

        /// <summary>
        /// Converts a scale step to the leftTrigger value the game's own note math expects.
        /// </summary>
        /// <remarks>
        /// Sax/Trombone: handPitch = leftTrigger, notePitch = handPitch + 0.01,
        ///   note = clamp(round(notePitch * 12), 0, 12).
        ///   round((n/12 + 0.01) * 12) = round(n + 0.12) = n. Exact for n = 0..12.
        /// Keytar: currentNote = clamp(round(handPitch * 13), 0, 12), so the divisor is 13.
        ///
        /// These produce byte-identical notePitch values to a vanilla player using the
        /// game's own jam keys, which is what makes the network replication safe.
        /// </remarks>
        internal static float StepToLeftTrigger(InstrumentKind k, int step)
        {
            if (k == InstrumentKind.Keytar)
                return step / 13f;
            return step / 12f;
        }
    }
}
