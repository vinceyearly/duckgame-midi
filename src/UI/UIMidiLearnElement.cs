using System;

namespace DuckGame.MidiController
{
    /// <summary>
    /// One remappable row: shows what a target is currently bound to, and captures the
    /// next MIDI message when activated.
    /// </summary>
    /// <remarks>
    /// The capture flow mirrors the game's own UIControlElement key-rebinding element:
    /// set UIMenu.globalUILock so menu navigation freezes, swallow the frame the SELECT
    /// was consumed on, then wait for input. Without globalUILock the pad you hit would
    /// also scroll the menu.
    /// </remarks>
    public class UIMidiLearnElement : UIMenuItem
    {
        private readonly BindTarget _target;
        private readonly int _step;

        private bool _learning;
        private bool _skipStep;
        private int _timeoutFrames;

        private const int LearnTimeoutFrames = 600;   // ~10 seconds

        internal UIMidiLearnElement(BindTarget target, int step)
            : base("", null, UIAlign.Left)
        {
            _target = target;
            _step = step;
            Refresh();
        }

        private void Refresh()
        {
            string label = MidiMapping.DescribeTarget(_target, _step);
            string value;

            if (_learning)
            {
                value = "PRESS A KEY...";
            }
            else
            {
                MidiBind b = MidiMapping.FindBindFor(_target, _step);
                if (b == null)
                {
                    value = DefaultHintFor(_target, _step);
                }
                else if (b.isControlChange)
                {
                    value = "CC" + b.number + (b.channel >= 0 ? (" ch" + (b.channel + 1)) : "");
                }
                else
                {
                    value = MidiMessage.NoteName(b.number) + " (" + b.number + ")" +
                            (b.channel >= 0 ? (" ch" + (b.channel + 1)) : "");
                }
            }

            text = PadTo(label, 18) + value;
        }

        /// <summary>What the automatic routing would do if there is no explicit bind.</summary>
        private static string DefaultHintFor(BindTarget target, int step)
        {
            if (MidiMapping.IsDrumTarget(target))
                return "auto (GM)";
            if (target == BindTarget.MelodicStep)
                return "auto " + MidiMessage.NoteName(MidiConfig.rootNote + step);
            if (target == BindTarget.Quack)
                return "auto ch" + (MidiConfig.quackChannel + 1);
            return "-";
        }

        private static string PadTo(string s, int width)
        {
            if (s == null) s = "";
            if (s.Length >= width) return s.Substring(0, width - 1) + " ";
            return s.PadRight(width);
        }

        public override void Activate(string trigger)
        {
            if (trigger != Triggers.Select)
            {
                base.Activate(trigger);
                return;
            }

            if (!MidiListener.isOpen)
            {
                try { SFX.Play("consoleError"); } catch { }
                return;
            }

            _learning = true;
            _skipStep = true;
            _timeoutFrames = LearnTimeoutFrames;
            UIMenu.globalUILock = true;

            MidiEngine e = MidiControllerMod.engine;
            if (e != null) e.ArmLearn();

            try { SFX.Play("consoleSelect"); } catch { }
            try
            {
                HUD.CloseAllCorners();
                HUD.AddCornerControl(HUDCorner.TopLeft, "@F1@CANCEL");
            }
            catch { }

            Refresh();
        }

        public override void Update()
        {
            if (_learning)
            {
                // The SELECT press that opened us is still live this frame.
                if (_skipStep)
                {
                    _skipStep = false;
                    base.Update();
                    return;
                }

                bool cancelled = false;
                try { cancelled = Keyboard.Pressed(Keys.F1); }
                catch { }

                if (--_timeoutFrames <= 0)
                {
                    cancelled = true;
                    Log.Warn("MIDI learn timed out.");
                }

                if (cancelled)
                {
                    EndLearn();
                }
                else
                {
                    MidiEngine e = MidiControllerMod.engine;
                    MidiMessage m;
                    if (e != null && e.TryTakeLearned(out m))
                    {
                        bool isCc = (m.kind == MidiKind.ControlChange);
                        // Bind to any channel by default - most controllers transmit on
                        // channel 1 and users are rarely thinking about channels.
                        MidiMapping.Bind(-1, m.number, isCc, _target, _step);
                        MidiConfig.Save();
                        try { SFX.Play("consoleSelect"); } catch { }
                        EndLearn();
                    }
                }
            }

            base.Update();
        }

        private void EndLearn()
        {
            _learning = false;
            _skipStep = false;
            UIMenu.globalUILock = false;

            MidiEngine e = MidiControllerMod.engine;
            if (e != null) e.CancelLearn();

            try { HUD.CloseAllCorners(); } catch { }
            Refresh();
        }

        public override void Draw()
        {
            // Cheap enough to keep the row live (shows learn state and auto-hints as
            // rootNote changes) without a dirty-tracking mechanism.
            Refresh();
            base.Draw();
        }
    }
}
