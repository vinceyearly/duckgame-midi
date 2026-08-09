using System;
using System.Text;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Renders the step grid, and owns the edit mode that drives it.
    /// </summary>
    /// <remarks>
    /// WHY THE GRID IS NOT MADE OF MENU ITEMS.
    ///
    /// The obvious build is one UIMenuItem per track. It does not fit. UIMenuItem is a
    /// UIDivider: the arrow goes in the left section and the text in the right, so the
    /// text starts around the middle of the dialog. Measured in game, the HUD camera is
    /// 320x180 units and menu text is drawn about 1.4x the width it reports, so a row of
    /// 20 characters needs ~224 units but only has ~150 available. No combination of
    /// dialog width or label length fixes that - narrowing the box just pushes the text
    /// further off the right edge.
    ///
    /// A centred UIText spanning the full dialog does fit - the MIDI Monitor page renders
    /// 24-character rows cleanly that way. So the grid is drawn as plain UIText lines and
    /// input is handled by a single menu item that takes over the controls, using the same
    /// UIMenu.globalUILock approach as MIDI-learn. That also reads better: the arrow keys
    /// move the cursor instead of fighting menu navigation.
    /// </remarks>
    internal static class SequencerGrid
    {
        internal const int TrackCount = (int)DrumVoice.Count + 1;   // drums + melodic line
        internal const int MelodicTrack = (int)DrumVoice.Count;

        private const int LabelWidth = 4;

        /// <summary>Step under the edit cursor, shared by every row.</summary>
        internal static int cursor;
        /// <summary>Which track row the cursor is on.</summary>
        internal static int track;
        /// <summary>True while the grid has taken over the controls.</summary>
        internal static bool editing;

        private static StepSequencer seq
        {
            get
            {
                MidiEngine e = MidiControllerMod.engine;
                return e == null ? null : e.sequencer;
            }
        }

        // --- rendering ------------------------------------------------------

        /// <summary>One track row: "&gt;KICK X..X..X..X..X..".</summary>
        internal static string RowText(int trackIndex)
        {
            StepSequencer s = seq;
            if (s == null) return "";
            StepPattern p = s.pattern;
            ClampCursor(p);

            bool melodic = (trackIndex == MelodicTrack);
            bool muted = melodic ? s.melodicMute : s.GetMute((DrumVoice)trackIndex);

            StringBuilder sb = new StringBuilder();
            // Selection marker lives in the string so every row stays the same width and
            // the columns line up under centring.
            sb.Append(editing && track == trackIndex ? '>' : ' ');

            string label = melodic ? "NOTE" : ShortName((DrumVoice)trackIndex);
            if (label.Length > LabelWidth) label = label.Substring(0, LabelWidth);
            sb.Append(label.PadRight(LabelWidth));
            sb.Append(muted ? '-' : ' ');

            for (int i = 0; i < p.length; i++)
                sb.Append(CellChar(p, i, melodic, trackIndex));

            return sb.ToString();
        }

        private static char CellChar(StepPattern p, int step, bool melodic, int trackIndex)
        {
            if (melodic)
            {
                int n = p.GetNote(step);
                if (n == StepPattern.Rest) return '.';
                // 0-9 then A-C for 10-12, so a cell is always one character.
                return n < 10 ? (char)('0' + n) : (char)('A' + (n - 10));
            }
            return p.GetDrum((DrumVoice)trackIndex, step) ? 'X' : '.';
        }

        /// <summary>Caption line showing the cursor column and the playhead.</summary>
        internal static string IndicatorLine()
        {
            StepSequencer s = seq;
            if (s == null) return "";
            StepPattern p = s.pattern;
            ClampCursor(p);

            int playhead = s.playing ? s.currentStep : -1;
            StringBuilder sb = new StringBuilder();
            sb.Append(' ', 1 + LabelWidth + 1);   // line up with the rows
            for (int i = 0; i < p.length; i++)
            {
                if (i == cursor && i == playhead) sb.Append('+');
                else if (i == cursor) sb.Append('v');
                else if (i == playhead) sb.Append('^');
                else if (i % 4 == 0) sb.Append(':');   // beat markers
                else sb.Append(' ');
            }
            return sb.ToString();
        }

        // --- editing --------------------------------------------------------

        private static void ClampCursor(StepPattern p)
        {
            if (cursor < 0) cursor = 0;
            if (cursor >= p.length) cursor = p.length - 1;
            if (track < 0) track = 0;
            if (track >= TrackCount) track = TrackCount - 1;
        }

        internal static void MoveCursor(int delta)
        {
            StepSequencer s = seq;
            if (s == null) return;
            StepPattern p = s.pattern;
            cursor += delta;
            while (cursor < 0) cursor += p.length;
            while (cursor >= p.length) cursor -= p.length;
        }

        internal static void MoveTrack(int delta)
        {
            track += delta;
            while (track < 0) track += TrackCount;
            while (track >= TrackCount) track -= TrackCount;
        }

        internal static void ToggleCell()
        {
            StepSequencer s = seq;
            if (s == null) return;
            StepPattern p = s.pattern;
            if (track == MelodicTrack)
            {
                int cur = p.GetNote(cursor);
                p.SetNote(cursor, cur == StepPattern.Rest ? 0 : StepPattern.Rest);
            }
            else
            {
                p.ToggleDrum((DrumVoice)track, cursor);
            }
        }

        internal static void NudgeNote(int delta)
        {
            StepSequencer s = seq;
            if (s == null || track != MelodicTrack) return;
            StepPattern p = s.pattern;
            int cur = p.GetNote(cursor);
            if (cur == StepPattern.Rest) cur = 0;
            cur += delta;
            if (cur < 0) cur = 0;
            if (cur > 12) cur = 12;
            p.SetNote(cursor, cur);
        }

        internal static void ToggleMute()
        {
            StepSequencer s = seq;
            if (s == null) return;
            if (track == MelodicTrack) s.melodicMute = !s.melodicMute;
            else s.ToggleMute((DrumVoice)track);
        }

        internal static string ShortName(DrumVoice v)
        {
            switch (v)
            {
                case DrumVoice.Kick: return "KICK";
                case DrumVoice.Snare: return "SNAR";
                case DrumVoice.HatClosed: return "HATC";
                case DrumVoice.HatOpen: return "HATO";
                case DrumVoice.LowTom: return "TOML";
                case DrumVoice.MedTom: return "TOMM";
                case DrumVoice.HighTom: return "TOMH";
                default: return "CRSH";
            }
        }
    }

    /// <summary>
    /// The menu item that hands the controls over to the grid.
    /// </summary>
    /// <remarks>
    /// Mirrors the MIDI-learn element: setting UIMenu.globalUILock freezes menu
    /// navigation so W/A/S/D drive the cursor rather than moving the selection, and
    /// _skipStep swallows the frame the activating press was consumed on.
    /// </remarks>
    public class UISequencerEditItem : UIMenuItem
    {
        private bool _skipStep;

        internal UISequencerEditItem()
            : base(new Func<string>(Caption), null, UIAlign.Center)
        {
        }

        private static string Caption()
        {
            return SequencerGrid.editing ? "EDITING - PRESS E TO STOP" : "EDIT PATTERN";
        }

        public override void Activate(string trigger)
        {
            if (trigger != Triggers.Select) { base.Activate(trigger); return; }

            SequencerGrid.editing = true;
            _skipStep = true;
            UIMenu.globalUILock = true;
            try { SFX.Play("consoleSelect"); } catch { }
        }

        public override void Update()
        {
            if (SequencerGrid.editing)
            {
                if (_skipStep) { _skipStep = false; base.Update(); return; }

                InputProfile p = InputProfile.DefaultPlayer1;
                if (p != null)
                {
                    if (p.Pressed(Triggers.Left)) SequencerGrid.MoveCursor(-1);
                    if (p.Pressed(Triggers.Right)) SequencerGrid.MoveCursor(1);
                    if (p.Pressed(Triggers.Up)) SequencerGrid.MoveTrack(-1);
                    if (p.Pressed(Triggers.Down)) SequencerGrid.MoveTrack(1);
                    if (p.Pressed(Triggers.Jump) || p.Pressed(Triggers.Select))
                    {
                        SequencerGrid.ToggleCell();
                        try { SFX.Play("consoleSelect"); } catch { }
                    }
                    if (p.Pressed(Triggers.Grab)) SequencerGrid.ToggleMute();
                    if (p.Pressed(Triggers.Shoot)) SequencerGrid.NudgeNote(1);
                    if (p.Pressed(Triggers.Ragdoll)) SequencerGrid.NudgeNote(-1);

                    if (p.Pressed(Triggers.Quack) || p.Pressed(Triggers.Cancel))
                    {
                        SequencerGrid.editing = false;
                        UIMenu.globalUILock = false;
                        MidiConfig.Save();
                        try { SFX.Play("consoleSelect"); } catch { }
                    }
                }
            }
            base.Update();
        }
    }
}
