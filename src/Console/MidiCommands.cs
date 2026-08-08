using System;
using System.Collections.Generic;
using System.Text;

namespace DuckGame.MidiController
{
    /// <summary>
    /// The `midi` dev-console command.
    /// </summary>
    /// <remarks>
    /// Only CMD.String arguments are used. The richer argument types (CMD.Boolean,
    /// CMD.Float, CMD.Enum and friends) exist only in Duck Game Rebuilt, so using them
    /// would break the mod on the stock Steam build.
    /// </remarks>
    internal static class MidiCommands
    {
        private static readonly List<Thing> _spawned = new List<Thing>();
        private static Level _spawnLevel;

        internal static void Register()
        {
            try
            {
                CMD.Argument[] args = new CMD.Argument[]
                {
                    new CMD.String("action", true),
                    new CMD.String("value", true)
                };
                CMD cmd = new CMD("midi", args, RunCommand);
                cmd.description =
                    "MIDI Controller: spawn|devices|device|on|off|status|settings|wizard|panic|save|reload|reset|help";
                DevConsole.AddCommand(cmd);
            }
            catch (Exception e)
            {
                Log.Error("could not register the 'midi' command: " + e.Message);
            }
        }

        private static void RunCommand(CMD cmd)
        {
            string action = null;
            string value = null;
            try
            {
                action = cmd.Arg<string>("action");
                value = cmd.Arg<string>("value");
            }
            catch { }

            if (string.IsNullOrEmpty(action)) action = "status";
            action = action.Trim().ToLowerInvariant();

            switch (action)
            {
                case "help": PrintHelp(); break;
                case "status": PrintStatus(); break;
                case "devices": PrintDevices(); break;
                case "device": SelectDevice(value); break;
                case "on": SetEnabled(true); break;
                case "off": SetEnabled(false); break;
                case "spawn": Spawn(value); break;
                case "panic": DoPanic(); break;
                case "save": MidiConfig.Save(); Log.Good("settings saved."); break;
                case "reload": MidiConfig.Load(); Log.Good("settings reloaded."); break;
                case "reset": MidiConfig.ResetToDefaults(); MidiConfig.Save(); break;
                case "settings": MidiSettingsMenu.RequestOpen(); break;
                case "wizard": MidiSettingsMenu.RequestOpenWizard(); break;
                case "test": Test(value); break;
                default:
                    Log.Warn("unknown action \"" + action + "\". Try: midi help");
                    break;
            }
        }

        private static void PrintHelp()
        {
            Log.Info("midi status              - connection and routing state");
            Log.Info("midi devices             - list MIDI inputs");
            Log.Info("midi device <n|name>     - select an input by index or name");
            Log.Info("midi on | off            - enable or disable note injection");
            Log.Info("midi spawn <instrument>  - drop an instrument next to you");
            Log.Info("                           sax|trombone|trumpet|keytar|drums|clear");
            Log.Info("midi settings            - open the settings menu (or press F9)");
            Log.Info("midi wizard              - re-run the first-time setup");
            Log.Info("midi panic               - stop all notes");
            Log.Info("midi save|reload|reset   - settings file operations");
            Log.Info("midi test <what>         - play without a controller attached:");
            Log.Info("                           scale|drums|quack|<note number>");
        }

        private static void PrintStatus()
        {
            Log.Info("--- MIDI Controller ---");
            Log.Info("  enabled:    " + (MidiConfig.enabled ? "yes" : "no"));

            if (!NAudioReflection.available)
            {
                Log.Error("  MIDI:       UNAVAILABLE - " + NAudioReflection.failureReason);
            }
            else if (MidiListener.isOpen)
            {
                Log.Good("  device:     " + MidiListener.openDeviceName);
            }
            else
            {
                string want = string.IsNullOrEmpty(MidiConfig.deviceName) ? "(none selected)" : MidiConfig.deviceName;
                Log.Warn("  device:     not connected - want \"" + want + "\"");
                if (!string.IsNullOrEmpty(MidiListener.lastError))
                    Log.Warn("  last error: " + MidiListener.lastError);
            }

            if (!DuckHook.available)
                Log.Error("  input:      UNAVAILABLE - " + DuckHook.unavailableReason);

            MidiEngine e = MidiControllerMod.engine;
            if (e != null)
            {
                Log.Info("  attached:   " + (e.isAttached ? "yes" : "no"));
                Log.Info("  holding:    " + Instruments.DisplayName(e.heldInstrument));
                Log.Info("  last note:  " + (string.IsNullOrEmpty(e.lastNoteDescription) ? "-" : e.lastNoteDescription));
            }

            Log.Info("  messages:   " + MidiListener.messagesReceived +
                     " received, " + MidiListener.messagesDropped + " dropped, " +
                     MidiListener.queueDepth + " queued");
            // The whole point of lobby compat is that it is checkable, not silent.
            string hash = LobbyCompat.CurrentModHash();
            if (!MidiConfig.lobbyCompat)
                Log.Warn("  lobbies:    OFF by config - only identically-modded lobbies");
            else if (LobbyCompat.looksUnmodded())
                Log.Good("  lobbies:    public lobbies OK (mod hash \"" + hash + "\")");
            else if (LobbyCompat.applied)
                Log.Info("  lobbies:    hidden from hash; other mods still count (\"" + hash + "\")");
            else
                Log.Error("  lobbies:    NOT hidden - " + LobbyCompat.failureReason);

            Log.Info("  root note:  " + MidiConfig.rootNote + " (" + MidiMessage.NoteName(MidiConfig.rootNote) + ")");
            Log.Info("  quack ch:   " + (MidiConfig.quackChannel + 1));
            Log.Info("  config:     " + MidiConfig.configPath);
        }

        private static void PrintDevices()
        {
            if (!NAudioReflection.available)
            {
                Log.Error("MIDI unavailable: " + NAudioReflection.failureReason);
                return;
            }
            string[] names = MidiListener.RefreshDeviceNames();
            if (names.Length == 0)
            {
                Log.Warn("no MIDI input devices found.");
                Log.Info("Plug your controller in - it will connect automatically within a couple of seconds.");
                return;
            }
            Log.Info("MIDI inputs:");
            for (int i = 0; i < names.Length; i++)
            {
                bool open = MidiListener.isOpen &&
                            string.Equals(names[i], MidiListener.openDeviceName, StringComparison.Ordinal);
                if (open) Log.Good("  [" + i + "] " + names[i] + "   <- connected");
                else Log.Info("  [" + i + "] " + names[i]);
            }
        }

        private static void SelectDevice(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Log.Warn("usage: midi device <index|name>");
                PrintDevices();
                return;
            }

            int index;
            bool ok;
            if (int.TryParse(value, out index))
                ok = MidiListener.Open(index);
            else
                ok = MidiListener.OpenByName(value);

            if (!ok)
            {
                Log.Error("could not open that device: " + MidiListener.lastError);
                return;
            }

            MidiConfig.deviceName = MidiListener.openDeviceName;
            MidiConfig.Save();
            Log.Good("connected to \"" + MidiListener.openDeviceName + "\".");
        }

        private static void SetEnabled(bool on)
        {
            MidiConfig.enabled = on;
            MidiConfig.Save();
            if (on)
            {
                if (MidiControllerMod.engine != null) MidiControllerMod.engine.StartIfEnabled();
                if (!MidiListener.isOpen && !string.IsNullOrEmpty(MidiConfig.deviceName))
                    MidiListener.OpenByName(MidiConfig.deviceName);
                Log.Good("MIDI input enabled.");
            }
            else
            {
                if (MidiControllerMod.engine != null) MidiControllerMod.engine.Detach();
                Log.Good("MIDI input disabled.");
            }
        }

        private static void DoPanic()
        {
            if (MidiControllerMod.engine != null) MidiControllerMod.engine.Panic();
            else Log.Warn("engine not running.");
        }

        // --- synthetic playback ---------------------------------------------

        private const int NoteOn = 0x90;
        private const int NoteOff = 0x80;

        /// <summary>
        /// Plays synthetic MIDI so the whole pipeline can be exercised with no hardware.
        /// </summary>
        /// <remarks>
        /// This is the fastest way to answer "is the mod broken, or is my controller
        /// not transmitting?" - if `midi test scale` sounds but your keyboard does not,
        /// the problem is upstream of this mod.
        /// </remarks>
        private static void Test(string what)
        {
            MidiEngine e = MidiControllerMod.engine;
            if (e == null) { Log.Error("engine not running."); return; }
            if (Level.current == null) { Log.Error("no level loaded."); return; }

            if (string.IsNullOrEmpty(what)) what = "scale";
            what = what.Trim().ToLowerInvariant();

            const int noteFrames = 12;   // ~200ms per note
            const int gapFrames = 4;

            if (what == "scale")
            {
                // A chromatic run over the instrument's full 13-note range.
                int at = 0;
                for (int step = 0; step <= 12; step++)
                {
                    int note = MidiConfig.rootNote + step;
                    e.ScheduleInject(MidiListener.BuildRaw(NoteOn, 0, note, 100), at);
                    e.ScheduleInject(MidiListener.BuildRaw(NoteOff, 0, note, 0), at + noteFrames);
                    at += noteFrames + gapFrames;
                }
                Log.Good("playing a 13-note chromatic run - hold a sax, trombone, keytar or trumpet.");
                return;
            }

            if (what == "drums" || what == "drum" || what == "beat")
            {
                // A bar of kick/snare/hat on GM channel 10.
                int[] pattern = { 36, 42, 38, 42, 36, 42, 38, 46 };
                int at = 0;
                for (int i = 0; i < pattern.Length; i++)
                {
                    e.ScheduleInject(MidiListener.BuildRaw(NoteOn, MidiConfig.DrumChannelIndex, pattern[i], 110), at);
                    at += 10;
                }
                Log.Good("playing a drum pattern - hold the drum kit.");
                return;
            }

            if (what == "quack")
            {
                int at = 0;
                for (int step = 0; step <= 12; step += 2)
                {
                    int note = MidiConfig.quackRootNote + step;
                    int ch = MidiConfig.quackChannel < 0 ? 0 : MidiConfig.quackChannel;
                    e.ScheduleInject(MidiListener.BuildRaw(NoteOn, ch, note, 100), at);
                    e.ScheduleInject(MidiListener.BuildRaw(NoteOff, ch, note, 0), at + 6);
                    at += 12;
                }
                Log.Good("playing an ascending quack run.");
                return;
            }

            int single;
            if (int.TryParse(what, out single) && single >= 0 && single <= 127)
            {
                e.ScheduleInject(MidiListener.BuildRaw(NoteOn, 0, single, 100), 0);
                e.ScheduleInject(MidiListener.BuildRaw(NoteOff, 0, single, 0), noteFrames);
                Log.Good("sent note " + single + " (" + MidiMessage.NoteName(single) + ").");
                return;
            }

            Log.Warn("usage: midi test <scale|drums|quack|0-127>");
        }

        // --- spawning -------------------------------------------------------

        private static void Spawn(string what)
        {
            if (string.IsNullOrEmpty(what))
            {
                Log.Warn("usage: midi spawn <sax|trombone|trumpet|keytar|drums|clear>");
                return;
            }
            what = what.Trim().ToLowerInvariant();

            if (what == "clear")
            {
                ClearSpawned();
                return;
            }

            if (Level.current == null)
            {
                Log.Error("no level loaded.");
                return;
            }

            Duck d = DuckHook.LocalDuck();
            if (d == null)
            {
                Log.Error("no local duck - start a match first.");
                return;
            }

            // A Thing added by a client exists only on that client and desyncs the
            // lobby's object list, so refuse rather than produce a confusing result.
            if (Network.isActive && !Network.isServer)
            {
                Log.Error("spawning is host-only. Ask the host, or use a level that already has instruments.");
                return;
            }

            Thing t = Create(what, d.x, d.y - 10f);
            if (t == null)
            {
                Log.Warn("unknown instrument \"" + what + "\". Try: sax, trombone, trumpet, keytar, drums");
                return;
            }

            PruneIfLevelChanged();
            Level.Add(t);
            if (Network.isActive)
            {
                try { Thing.Fondle(t, DuckNetwork.localConnection); }
                catch { }
            }
            _spawned.Add(t);
            _spawnLevel = Level.current;
            Log.Good("spawned " + what + ". Press GRAB to pick it up.");
        }

        private static Thing Create(string what, float x, float y)
        {
            switch (what)
            {
                case "sax":
                case "saxophone":
                case "saxaphone":
                    return new Saxaphone(x, y);
                case "trombone":
                case "bone":
                    return new Trombone(x, y);
                case "trumpet":
                case "horn":
                    return new Trumpet(x, y);
                case "keytar":
                case "keys":
                    return new Keytar(x, y);
                case "drums":
                case "drum":
                case "drumset":
                case "kit":
                    return new DrumSet(x, y);
                default:
                    return null;
            }
        }

        private static void PruneIfLevelChanged()
        {
            if (object.ReferenceEquals(_spawnLevel, Level.current)) return;
            _spawned.Clear();
            _spawnLevel = Level.current;
        }

        private static void ClearSpawned()
        {
            PruneIfLevelChanged();
            int removed = 0;
            for (int i = 0; i < _spawned.Count; i++)
            {
                try
                {
                    if (_spawned[i] != null && !_spawned[i].removeFromLevel)
                    {
                        Level.Remove(_spawned[i]);
                        removed++;
                    }
                }
                catch { }
            }
            _spawned.Clear();
            Log.Good("removed " + removed + " spawned instrument(s).");
        }
    }
}
