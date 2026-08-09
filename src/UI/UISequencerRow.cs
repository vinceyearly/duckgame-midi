using System;
using System.Text;

namespace DuckGame.MidiController
{
    /// <summary>
    /// One track of the step grid: a label followed by a cell per step.
    /// </summary>
    /// <remarks>
    /// The cursor column is shared across every row (a static), so moving up and down
    /// tracks keeps your place in the bar - which is how you actually program a beat.
    ///
    /// Cells are drawn as coloured text rather than sprites. The bitmap font is monospace,
    /// so a fixed-width label plus fixed-width cells lines the grid up for free, and the
    /// game's own colour tags give a playhead and cursor without any custom rendering.
    /// </remarks>
    public class UISequencerRow : UIMenuItem
    {
        /// <summary>Shared edit cursor, in steps.</summary>
        internal static int cursor;

        private readonly bool _isMelodic;
        private readonly DrumVoice _voice;

        private bool _centred;

        /// <summary>
        /// Kept deliberately tight. The HUD camera is only a few hundred units wide, and
        /// at 8px per character a label plus 32 cells has to stay under about 40 characters
        /// or the row overflows the dialog and the labels get clipped off-screen.
        /// </summary>
        private const int LabelWidth = 6;

        internal UISequencerRow(DrumVoice voice)
            : base("", null, UIAlign.Left)
        {
            _voice = voice;
            _isMelodic = false;
            // Font is chosen in Refresh, from the pattern length.
        }

        /// <summary>Marker constructor for the melodic line.</summary>
        internal UISequencerRow()
            : base("", null, UIAlign.Left)
        {
            _isMelodic = true;
            // Font is chosen in Refresh, from the pattern length.
        }

        /// <summary>
        /// Renders the shared cursor and the playhead as a caption line above the grid.
        /// </summary>
        /// <remarks>
        /// This exists because the rows themselves cannot use colour. Duck Game strips
        /// "|COLOUR|" tags when drawing but measures the raw string for layout, so a tag
        /// per cell made a 22-character row measure as ~180 and overflow the dialog no
        /// matter how wide it was. Plain text plus one indicator line gives the same
        /// information and actually fits.
        /// </remarks>
        internal static string IndicatorLine()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "";
            StepSequencer s = e.sequencer;
            StepPattern p = s.pattern;

            int playhead = s.playing ? s.currentStep : -1;
            StringBuilder sb = new StringBuilder();
            sb.Append(' ', LabelWidth);
            for (int i = 0; i < p.length; i++)
            {
                if (i == cursor && i == playhead) sb.Append('+');
                else if (i == cursor) sb.Append('v');
                else if (i == playhead) sb.Append('^');
                else if (i % 4 == 0) sb.Append(':');    // beat markers
                else sb.Append(' ');
            }
            return sb.ToString();
        }

        private static StepSequencer seq
        {
            get
            {
                MidiEngine e = MidiControllerMod.engine;
                return e == null ? null : e.sequencer;
            }
        }

        internal static void MoveCursor(int delta, StepPattern p)
        {
            if (p == null) return;
            cursor += delta;
            while (cursor < 0) cursor += p.length;
            while (cursor >= p.length) cursor -= p.length;
        }

        public override void Activate(string trigger)
        {
            StepSequencer s = seq;
            if (s == null) { base.Activate(trigger); return; }
            StepPattern p = s.pattern;

            if (trigger == Triggers.MenuLeft) { MoveCursor(-1, p); return; }
            if (trigger == Triggers.MenuRight) { MoveCursor(1, p); return; }

            if (trigger == Triggers.Menu1)
            {
                if (_isMelodic) s.melodicMute = !s.melodicMute;
                else s.ToggleMute(_voice);
                try { SFX.Play("consoleSelect"); } catch { }
                return;
            }

            if (trigger == Triggers.Select)
            {
                if (_isMelodic)
                {
                    // Toggle rest against a sensible default, so one press gives a note.
                    int cur = p.GetNote(cursor);
                    p.SetNote(cursor, cur == StepPattern.Rest ? 0 : StepPattern.Rest);
                }
                else
                {
                    p.ToggleDrum(_voice, cursor);
                }
                try { SFX.Play("consoleSelect"); } catch { }
                return;
            }

            // Nudge the note under the cursor. Only meaningful on the melodic line.
            if (_isMelodic && (trigger == Triggers.Ragdoll || trigger == Triggers.Strafe))
            {
                int cur = p.GetNote(cursor);
                if (cur == StepPattern.Rest) cur = 0;
                cur += (trigger == Triggers.Strafe) ? 1 : -1;
                if (cur < 0) cur = 0;
                if (cur > 12) cur = 12;
                p.SetNote(cursor, cur);
                try { SFX.Play("consoleSelect"); } catch { }
                return;
            }

            base.Activate(trigger);
        }

        public override void Update()
        {
            // Must happen in Update, not Draw: the menu measures its children to size and
            // centre itself, and that pass runs before Draw. Setting the text in Draw left
            // the menu laid out for empty rows and then drawing full-width ones, so the
            // dialog sat off-centre and the right-hand steps fell off the screen.
            Refresh();
            base.Update();
        }

        private void Refresh()
        {
            StepSequencer s = seq;
            if (s == null) return;
            StepPattern p = s.pattern;

            if (cursor >= p.length) cursor = p.length - 1;
            if (cursor < 0) cursor = 0;

            if (!_centred)
            {
                // Left-aligned text anchors to the dialog's left edge and runs off the
                // right of the frame; centring keeps it inside. Every row is the same
                // length, so the columns still line up.
                if (_textElement != null) _textElement.align = UIAlign.Center;
                _centred = true;
            }

            bool muted = _isMelodic ? s.melodicMute : s.GetMute(_voice);
            string label = _isMelodic ? "NOTE" : ShortName(_voice);
            if (label.Length > LabelWidth - 1) label = label.Substring(0, LabelWidth - 1);
            // A muted track is marked with a dash rather than a colour - see below.
            if (muted) label = "-" + label;
            label = label.PadRight(LabelWidth).Substring(0, LabelWidth);

            StringBuilder sb = new StringBuilder();
            sb.Append(label);
            for (int i = 0; i < p.length; i++) sb.Append(CellText(p, i));

            text = sb.ToString();
        }

        private string CellText(StepPattern p, int step)
        {
            if (_isMelodic)
            {
                int n = p.GetNote(step);
                if (n == StepPattern.Rest) return ".";
                // 0-9 then A,B,C for 10-12, so every step stays one character wide.
                return n < 10 ? n.ToString() : ((char)('A' + (n - 10))).ToString();
            }
            return p.GetDrum(_voice, step) ? "X" : ".";
        }

        /// <summary>Exactly LabelWidth characters, so the grid columns line up.</summary>
        private static string ShortName(DrumVoice v)
        {
            switch (v)
            {
                case DrumVoice.Kick: return "KICK";
                case DrumVoice.Snare: return "SNARE";
                case DrumVoice.HatClosed: return "HAT CL";
                case DrumVoice.HatOpen: return "HAT OP";
                case DrumVoice.LowTom: return "TOM LO";
                case DrumVoice.MedTom: return "TOM MD";
                case DrumVoice.HighTom: return "TOM HI";
                default: return "CRASH";
            }
        }
    }
}
