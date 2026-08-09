using System;
using System.Collections.Generic;

namespace DuckGame.MidiController
{
    /// <summary>
    /// The in-game settings UI.
    /// </summary>
    /// <remarks>
    /// WHY A STANDALONE MENU RATHER THAN AN OPTIONS PAGE: Options.CreateOptionsMenu and
    /// Options.AddMenus hard-code their children, and the in-match pause menu is rebuilt
    /// per level. A menu added at mod-init time would never be a child of the live pause
    /// group, so its Update would never be pumped and it would be dead on arrival.
    ///
    /// Driving MonoMain.pauseMenu directly is fully supported: the engine pumps it every
    /// frame and nulls it when it closes, and submenu navigation works because
    /// UIMenuActionOpenMenu reassigns MonoMain.pauseMenu when the menu it closes is the
    /// current one.
    /// </remarks>
    internal static class MidiSettingsMenu
    {
        private static UIMenu _root;
        private static UIMenu _deviceMenu;
        private static UIMenu _drumMapMenu;
        private static UIMenu _melodicMapMenu;
        private static UIMenu _optionsMenu;
        private static UIMenu _monitorMenu;
        private static UIMenu _wizardMenu;
        private static UIMenu _seqMenu;

        private static bool _openRequested;
        private static bool _wizardRequested;

        /// <summary>
        /// Each submenu's BACK item, paired with the submenu it belongs to.
        /// </summary>
        /// <remarks>
        /// A submenu's BACK cannot simply close itself: UIMenuActionOpenMenu moved
        /// MonoMain.pauseMenu onto the submenu when we navigated in, so closing it would
        /// drop the player out of the settings entirely instead of going up one level.
        /// BACK has to be "close me, open the root", which means it can only be wired
        /// once the root exists - hence this deferred fix-up.
        /// </remarks>
        private static readonly List<KeyValuePair<UIMenuItem, UIMenu>> _backItems =
            new List<KeyValuePair<UIMenuItem, UIMenu>>();

        private static UIMenuItem MakeBackItem(UIMenu owner)
        {
            UIMenuItem item = new UIMenuItem("BACK", new UIMenuActionCloseMenu(owner),
                UIAlign.Center, default(Color), true);
            _backItems.Add(new KeyValuePair<UIMenuItem, UIMenu>(item, owner));
            return item;
        }

        private static void WireBackItems()
        {
            for (int i = 0; i < _backItems.Count; i++)
                _backItems[i].Key.menuAction = new UIMenuActionOpenMenu(_backItems[i].Value, _root);
        }

        /// <summary>Bound to the device picker; applied when the selection changes.</summary>
        public static string selectedDeviceName = "";

        // --- entry points ---------------------------------------------------

        internal static void RequestOpen() { _openRequested = true; }
        internal static void RequestOpenWizard() { _wizardRequested = true; }

        /// <summary>
        /// Polls the hotkey. Called from PostUpdate, which still runs while paused.
        /// </summary>
        internal static void HandleHotkey()
        {
            bool pressed = false;
            try
            {
                // Keyboard.Pressed does not suppress itself while the console is open,
                // so the guard is explicit.
                pressed = Keyboard.Pressed(Keys.F9) && !DevConsole.open;
            }
            catch { }

            if (!pressed && !_openRequested && !_wizardRequested) return;

            bool wantWizard = _wizardRequested;
            _openRequested = false;
            _wizardRequested = false;

            if (Level.current == null) return;
            if (MonoMain.pauseMenu != null) return;   // don't fight the game's own menu

            try
            {
                EnsureBuilt();
                UIMenu target = wantWizard ? _wizardMenu : _root;
                if (target == null) return;
                MonoMain.pauseMenu = target;
                target.Open();
            }
            catch (Exception e)
            {
                Log.Error("could not open the settings menu: " + e.Message);
            }
        }

        // --- construction ---------------------------------------------------
        // Deferred until first open: the menu classes build SpriteMaps and BitmapFonts,
        // which need content loaded and a live graphics device. Mod init is too early.

        private static void EnsureBuilt()
        {
            if (_root != null) return;

            float cx = Layer.HUD.camera.width / 2f;
            float cy = Layer.HUD.camera.height / 2f;

            BuildDeviceMenu(cx, cy);
            BuildDrumMapMenu(cx, cy);
            BuildMelodicMapMenu(cx, cy);
            BuildOptionsMenu(cx, cy);
            BuildMonitorMenu(cx, cy);
            BuildWizardMenu(cx, cy);
            BuildSequencerMenu(cx, cy);
            BuildRoot(cx, cy);
            WireBackItems();
        }

        private static void BuildRoot(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@MIDI CONTROLLER", cx, cy, 230f,
                conString: "@CANCEL@BACK @SELECT@SELECT");

            m.Add(new UIMenuItemToggle("ENABLED", null,
                new FieldBinding(typeof(MidiConfig), "enabled")), true);

            m.Add(new UIText(new Func<string>(StatusLine), Color.White, UIAlign.Center), true);

            m.Add(new UIMenuItem("MIDI DEVICE", new UIMenuActionOpenMenu(m, _deviceMenu)), true);
            m.Add(new UIMenuItem("DRUM MAPPING", new UIMenuActionOpenMenu(m, _drumMapMenu)), true);
            m.Add(new UIMenuItem("NOTE MAPPING", new UIMenuActionOpenMenu(m, _melodicMapMenu)), true);
            m.Add(new UIMenuItem("PLAY OPTIONS", new UIMenuActionOpenMenu(m, _optionsMenu)), true);
            m.Add(new UIMenuItem("STEP SEQUENCER", new UIMenuActionOpenMenu(m, _seqMenu)), true);
            m.Add(new UIMenuItem("MIDI MONITOR", new UIMenuActionOpenMenu(m, _monitorMenu)), true);
            m.Add(new UIMenuItem("SETUP WIZARD", new UIMenuActionOpenMenu(m, _wizardMenu)), true);

            m.Add(new UIMenuItem("SAVE SETTINGS",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(SaveAndNotify))), true);
            m.Add(new UIMenuItem("RESET TO DEFAULTS",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(ResetAndNotify))), true);
            // The root's BACK genuinely closes the settings - only submenus go up a level.
            m.Add(new UIMenuItem("BACK", new UIMenuActionCloseMenu(m), UIAlign.Center,
                default(Color), true), true);

            m.Close();
            _root = m;
        }

        private static string StatusLine()
        {
            if (!NAudioReflection.available)
                return "|DGRED|MIDI UNAVAILABLE";
            if (!MidiListener.isOpen)
                return "|DGYELLOW|NO DEVICE CONNECTED";

            string name = MidiListener.openDeviceName;
            if (name != null && name.Length > 22) name = name.Substring(0, 22);
            return "|DGGREEN|" + name;
        }

        // --- device ---------------------------------------------------------

        private static void BuildDeviceMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@MIDI DEVICE", cx, cy, 250f,
                conString: "@CANCEL@BACK @SELECT@SELECT");

            m.Add(new UIText(new Func<string>(DeviceListLine), Color.White, UIAlign.Center), true);

            // Rebuilding the item list as devices come and go would mean rebuilding the
            // menu; instead, one row per slot with a condition that hides unused rows.
            for (int i = 0; i < 8; i++)
            {
                int index = i;   // capture per iteration
                UIMenuItem item = new UIMenuItem("",
                    new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(
                        delegate { SelectDevice(index); })));
                item.condition = delegate { return index < MidiListener.GetDeviceNames().Length; };
                _deviceRows.Add(item);
                m.Add(item, true);
            }

            m.Add(new UIMenuItem("RESCAN",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(Rescan))), true);
            m.Add(MakeBackItem(m), true);

            m.Close();
            _deviceMenu = m;
        }

        private static readonly List<UIMenuItem> _deviceRows = new List<UIMenuItem>();

        private static string DeviceListLine()
        {
            // Refresh the row captions here - this runs every frame the menu is drawn.
            string[] names = MidiListener.GetDeviceNames();
            for (int i = 0; i < _deviceRows.Count; i++)
            {
                if (i >= names.Length) continue;
                string n = names[i];
                if (n != null && n.Length > 24) n = n.Substring(0, 24);
                bool isOpen = MidiListener.isOpen &&
                              string.Equals(names[i], MidiListener.openDeviceName, StringComparison.Ordinal);
                _deviceRows[i].text = (isOpen ? "> " : "  ") + n;
            }

            if (names.Length == 0)
                return "|DGYELLOW|NOTHING DETECTED - PLUG ONE IN";
            return "SELECT AN INPUT";
        }

        private static void SelectDevice(int index)
        {
            if (!MidiListener.Open(index))
            {
                Log.Error("could not open that device: " + MidiListener.lastError);
                try { SFX.Play("consoleError"); } catch { }
                return;
            }
            MidiConfig.deviceName = MidiListener.openDeviceName;
            MidiConfig.Save();
            try { SFX.Play("consoleSelect"); } catch { }
            Log.Good("connected to \"" + MidiListener.openDeviceName + "\".");
        }

        private static void Rescan()
        {
            MidiListener.RefreshDeviceNames();
            try { SFX.Play("consoleSelect"); } catch { }
        }

        // --- mapping --------------------------------------------------------

        private static void BuildDrumMapMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@DRUM MAPPING", cx, cy, 260f,
                conString: "@CANCEL@BACK @SELECT@LEARN");

            m.Add(new UIText("SELECT A ROW, THEN HIT A PAD", Colors.DGYellow, UIAlign.Center), true);

            for (int i = 0; i < (int)DrumVoice.Count; i++)
                m.Add(new UIMidiLearnElement(MidiMapping.TargetOf((DrumVoice)i), 0), true);

            m.Add(new UIMenuItem("CLEAR ALL BINDINGS",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(ClearBinds))), true);
            m.Add(MakeBackItem(m), true);

            m.Close();
            _drumMapMenu = m;
        }

        private static void BuildMelodicMapMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@NOTE MAPPING", cx, cy, 260f,
                conString: "@CANCEL@BACK @SELECT@LEARN");

            m.Add(new UIText("13 SCALE STEPS - AUTO BY DEFAULT", Colors.DGYellow, UIAlign.Center), true);

            for (int step = 0; step <= 12; step++)
                m.Add(new UIMidiLearnElement(BindTarget.MelodicStep, step), true);

            m.Add(new UIMidiLearnElement(BindTarget.KeytarPresetNext, 0), true);
            m.Add(MakeBackItem(m), true);

            m.Close();
            _melodicMapMenu = m;
        }

        private static void ClearBinds()
        {
            MidiMapping.ClearBinds();
            MidiConfig.Save();
            Log.Good("all custom bindings cleared - back to automatic routing.");
            try { SFX.Play("consoleSelect"); } catch { }
        }

        // --- options --------------------------------------------------------

        private static void BuildOptionsMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@PLAY OPTIONS", cx, cy, 260f,
                conString: "@CANCEL@BACK @SELECT@SELECT");

            m.Add(new UIMenuItemNumber("ROOT NOTE", null,
                new FieldBinding(typeof(MidiConfig), "rootNote", 0f, 127f, 1f), 1), true);
            m.Add(new UIMenuItemToggle("FOLD OCTAVES", null,
                new FieldBinding(typeof(MidiConfig), "octaveFold")), true);

            List<string> polyNames = new List<string>();
            polyNames.Add("NEWEST");
            polyNames.Add("HIGHEST");
            polyNames.Add("LOWEST");
            m.Add(new UIMenuItemNumber("PRIORITY", null,
                new FieldBinding(typeof(MidiConfig), "polyphony", 0f, 2f, 1f), 1,
                default(Color), null, null, "", null, polyNames), true);

            m.Add(new UIMenuItemNumber("QUACK CHANNEL", null,
                new FieldBinding(typeof(MidiConfig), "quackChannel", -1f, 15f, 1f), 1), true);
            m.Add(new UIMenuItemNumber("QUACK ROOT", null,
                new FieldBinding(typeof(MidiConfig), "quackRootNote", 0f, 127f, 1f), 1), true);
            m.Add(new UIMenuItemToggle("QUACK EMPTY-HANDED", null,
                new FieldBinding(typeof(MidiConfig), "quackWhenEmptyHanded")), true);

            m.Add(new UIMenuItemToggle("CH10 IS DRUMS ONLY", null,
                new FieldBinding(typeof(MidiConfig), "drumChannelStrict")), true);
            m.Add(new UIMenuItemNumber("VELOCITY FLOOR", null,
                new FieldBinding(typeof(MidiConfig), "velocityFloor", 0f, 127f, 1f), 1), true);
            m.Add(new UIMenuItemToggle("SLUR NEARBY NOTES", null,
                new FieldBinding(typeof(MidiConfig), "legatoBend")), true);
            m.Add(new UIMenuItemToggle("SHOW HUD", null,
                new FieldBinding(typeof(MidiConfig), "showHud")), true);
            m.Add(new UIMenuItemNumber("PLAYER SLOT", null,
                new FieldBinding(typeof(MidiConfig), "playerSlot", -1f, 3f, 1f), 1), true);

            m.Add(new UIText("JAM BUTTON OVERRIDES MIDI", Colors.DGYellow, UIAlign.Center), true);

            m.Add(MakeBackItem(m), true);

            m.Close();
            _optionsMenu = m;
        }

        // --- monitor --------------------------------------------------------

        private static void BuildMonitorMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@MIDI MONITOR", cx, cy, 300f,
                conString: "@CANCEL@BACK");

            m.Add(new UIText("PLAY SOMETHING - MESSAGES APPEAR BELOW", Colors.DGYellow, UIAlign.Center), true);
            for (int i = 0; i < 12; i++)
            {
                int line = i;
                // Centred, not left-aligned: a left-aligned UIText added straight to a
                // UIMenu anchors outside the dialog border and the rows spill over the
                // frame. The bitmap font is monospace, so padding every row to the same
                // width (see MonitorLine) makes centred rows line up as columns anyway.
                m.Add(new UIText(new Func<string>(delegate { return MonitorLine(line); }),
                    Color.White, UIAlign.Center), true);
            }
            m.Add(MakeBackItem(m), true);

            m.Close();
            _monitorMenu = m;
        }

        /// <summary>One monitor row, newest first, padded so the columns line up.</summary>
        private const int MonitorLineWidth = 24;

        private static string MonitorLine(int index)
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "";
            string[] lines = e.GetMonitorLines();
            // Newest at the top.
            int i = lines.Length - 1 - index;
            if (i < 0 || i >= lines.Length) return "";

            string s = lines[i];
            if (s.Length > MonitorLineWidth) s = s.Substring(0, MonitorLineWidth);
            return s.PadRight(MonitorLineWidth);
        }

        // --- step sequencer -------------------------------------------------

        private static void BuildSequencerMenu(float cx, float cy)
        {
            // The HUD camera is only 320x180 units. A UIMenu grows to fit its widest
            // child, so both the title and every caption have to stay short or the whole
            // dialog is pushed off the right of the screen.
            UIMenu m = new UIMenu("@QUACK@SEQUENCER", cx, cy, 220f,
                conString: "@CANCEL@BACK @SELECT@TOGGLE");

            m.Add(new UIText(new Func<string>(SeqStatusLine), Color.White, UIAlign.Center), true);
            m.Add(new UIText(new Func<string>(UISequencerRow.IndicatorLine),
                Colors.DGGreen, UIAlign.Center), true);

            for (int v = 0; v < (int)DrumVoice.Count; v++)
                m.Add(new UISequencerRow((DrumVoice)v), true);
            m.Add(new UISequencerRow(), true);   // melodic line

            m.Add(new UIText(new Func<string>(SeqHintLine), Colors.DGYellow, UIAlign.Center), true);

            m.Add(new UIMenuItem(new Func<string>(PlayLabel),
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(SeqTogglePlay))), true);
            m.Add(new UIMenuItem(new Func<string>(RecordLabel),
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(SeqToggleRecord))), true);

            m.Add(new UIMenuItemNumber("TEMPO", null,
                new FieldBinding(new SeqBpmProxy(), "value", 40f, 300f, 1f), 5), true);
            m.Add(new UIMenuItemNumber("SWING %", null,
                new FieldBinding(new SeqSwingProxy(), "value", 0f, 45f, 1f), 5), true);
            m.Add(new UIMenuItemNumber("STEPS", null,
                new FieldBinding(new SeqLengthProxy(), "value", 1f, StepPattern.MaxSteps, 1f), 1), true);
            m.Add(new UIMenuItemNumber("PATTERN", null,
                new FieldBinding(new SeqSlotProxy(), "value", 1f, StepSequencer.SlotCount, 1f), 1), true);

            m.Add(new UIMenuItem("CLEAR PATTERN",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(SeqClear))), true);
            m.Add(MakeBackItem(m), true);

            m.Close();
            _seqMenu = m;
        }

        private static string SeqStatusLine()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "";
            StepSequencer s = e.sequencer;

            string transport = s.recording ? "|DGRED|REC"
                             : (s.playing ? "|DGGREEN|PLAY" : "|GRAY|STOP");
            string clock = s.clockSource == ClockSource.External
                ? "|DGGREEN|EXT" : ("|WHITE|" + s.bpm);

            return transport + "|WHITE| P" + (s.slot + 1) + " " + clock;
        }

        /// <summary>Tells you why nothing is playing, which is nearly always the reason.</summary>
        private static string SeqHintLine()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "";
            // Keep every caption at or under the grid's width (label + 16 cells = 22
            // characters). A UIMenu grows to fit its widest child, so one long line here
            // would push the whole dialog off screen.
            if (e.heldInstrument == InstrumentKind.DrumSet) return "DRUM TRACKS LIVE";
            if (Instruments.IsMelodic(e.heldInstrument)) return "NOTE TRACK LIVE";
            return "HOLD AN INSTRUMENT";
        }

        private static string PlayLabel()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "PLAY";
            return e.sequencer.playing ? "STOP" : "PLAY";
        }

        private static string RecordLabel()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return "RECORD";
            return e.sequencer.recording ? "RECORDING - STOP" : "RECORD";
        }

        private static void SeqTogglePlay()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return;
            e.sequencer.TogglePlay(e.router);
            try { SFX.Play("consoleSelect"); } catch { }
        }

        private static void SeqToggleRecord()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return;
            e.sequencer.ToggleRecord();
            try { SFX.Play("consoleSelect"); } catch { }
        }

        private static void SeqClear()
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) return;
            e.sequencer.pattern.Clear();
            MidiConfig.Save();
            Log.Good("pattern cleared.");
            try { SFX.Play("consoleSelect"); } catch { }
        }

        // FieldBinding reflects over a public instance field or property, so the
        // sequencer's clamped values are exposed through these tiny adapters rather than
        // duplicating the clamping in the UI.

        public class SeqBpmProxy
        {
            public int value
            {
                get { MidiEngine e = MidiControllerMod.engine; return e == null ? 120 : e.sequencer.bpm; }
                set { MidiEngine e = MidiControllerMod.engine; if (e != null) e.sequencer.bpm = value; }
            }
        }

        public class SeqSwingProxy
        {
            public int value
            {
                get
                {
                    MidiEngine e = MidiControllerMod.engine;
                    return e == null ? 0 : (int)Math.Round(e.sequencer.swing * 100f);
                }
                set
                {
                    MidiEngine e = MidiControllerMod.engine;
                    if (e != null) e.sequencer.swing = value / 100f;
                }
            }
        }

        public class SeqLengthProxy
        {
            public int value
            {
                get { MidiEngine e = MidiControllerMod.engine; return e == null ? 16 : e.sequencer.pattern.length; }
                set { MidiEngine e = MidiControllerMod.engine; if (e != null) e.sequencer.pattern.length = value; }
            }
        }

        public class SeqSlotProxy
        {
            public int value
            {
                get { MidiEngine e = MidiControllerMod.engine; return e == null ? 1 : e.sequencer.slot + 1; }
                set { MidiEngine e = MidiControllerMod.engine; if (e != null) e.sequencer.SelectSlot(value - 1); }
            }
        }

        // --- first-run wizard -----------------------------------------------

        private static void BuildWizardMenu(float cx, float cy)
        {
            UIMenu m = new UIMenu("@QUACK@SETUP", cx, cy, 260f,
                conString: "@CANCEL@BACK @SELECT@SELECT");

            m.Add(new UIText(new Func<string>(WizardStepText), Color.White, UIAlign.Center), true);
            m.Add(new UIMenuItem("CHOOSE MIDI DEVICE",
                new UIMenuActionOpenMenu(m, _deviceMenu)), true);
            m.Add(new UIMenuItem("TEST: SPAWN A DRUM KIT",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(WizardSpawnDrums))), true);
            m.Add(new UIMenuItem("TEST: SPAWN A KEYTAR",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(WizardSpawnKeytar))), true);
            m.Add(new UIMenuItem("OPEN MIDI MONITOR",
                new UIMenuActionOpenMenu(m, _monitorMenu)), true);
            m.Add(new UIMenuItem("DONE",
                new UIMenuActionCallFunction(new UIMenuActionCallFunction.Function(WizardDone))), true);
            m.Add(MakeBackItem(m), true);

            m.Close();
            _wizardMenu = m;
        }

        private static string WizardStepText()
        {
            if (!NAudioReflection.available)
                return "|DGRED|MIDI IS NOT AVAILABLE ON THIS SYSTEM";
            if (!MidiListener.isOpen)
                return "|DGYELLOW|STEP 1: CONNECT YOUR CONTROLLER";
            MidiEngine e = MidiControllerMod.engine;
            if (e != null && e.heldInstrument == InstrumentKind.None)
                return "|DGYELLOW|STEP 2: SPAWN AND HOLD AN INSTRUMENT";
            return "|DGGREEN|READY - PLAY SOMETHING!";
        }

        private static void WizardSpawnDrums() { RunConsole("midi spawn drums"); }
        private static void WizardSpawnKeytar() { RunConsole("midi spawn keytar"); }

        private static void RunConsole(string command)
        {
            try { DevConsole.RunCommand(command); }
            catch (Exception e) { Log.Error("could not run \"" + command + "\": " + e.Message); }
        }

        private static void WizardDone()
        {
            MidiConfig.firstRunDone = true;
            MidiConfig.Save();
            try { SFX.Play("consoleSelect"); } catch { }
            if (_wizardMenu != null) _wizardMenu.Close();
        }

        // --- shared actions -------------------------------------------------

        private static void SaveAndNotify()
        {
            MidiConfig.Save();
            Log.Good("settings saved.");
            try { SFX.Play("consoleSelect"); } catch { }
        }

        private static void ResetAndNotify()
        {
            MidiConfig.ResetToDefaults();
            MidiConfig.Save();
            try { SFX.Play("consoleSelect"); } catch { }
        }
    }
}
