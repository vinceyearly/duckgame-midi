using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DuckGame.MidiController
{
    public enum PolyphonyPolicy
    {
        LastNoteWins = 0,
        HighestNote,
        LowestNote
    }

    /// <summary>
    /// All user settings, plus load/save.
    /// </summary>
    /// <remarks>
    /// FORMAT: plain key=value text, hand-parsed. Deliberately not XML - System.Xml.Linq
    /// carries the same "might not be in the AppDomain when mods compile" hazard as
    /// NAudio does. It is also directly hand-editable, which is a feature when someone
    /// needs to share a working mapping.
    ///
    /// LOCATION: under the Duck Game save directory, not the mod folder, so a Workshop
    /// update (which replaces the mod folder wholesale) cannot wipe someone's mapping.
    /// </remarks>
    public static class MidiConfig
    {
        public const int FormatVersion = 1;

        /// <summary>General MIDI reserves channel 10 (index 9) for percussion.</summary>
        public const int DrumChannelIndex = 9;

        // --- settings -------------------------------------------------------
        public static bool enabled = true;
        public static string deviceName = "";

        /// <summary>MIDI note that maps to scale step 0. 60 is middle C.</summary>
        public static int rootNote = 60;
        public static bool octaveFold = true;
        public static PolyphonyPolicy polyphony = PolyphonyPolicy.LastNoteWins;

        /// <summary>0-based. Default 1 is what hardware labels "channel 2".</summary>
        public static int quackChannel = 1;
        public static int quackRootNote = 48;
        public static bool quackWhenEmptyHanded = true;

        /// <summary>Notes on GM channel 10 are ignored while a melodic instrument is held.</summary>
        public static bool drumChannelStrict = true;

        public static int velocityFloor = 1;
        public static float bendRange = 1f;
        public static bool legatoBend;
        public static bool showHud = true;

        /// <summary>Which local player to drive when playing splitscreen. -1 = first available.</summary>
        public static int playerSlot = -1;

        public static bool firstRunDone;

        /// <summary>
        /// Stay out of Duck Game's lobby mod hash so public lobbies remain joinable.
        /// </summary>
        /// <remarks>
        /// On by default: without it, installing any mod locks you out of every lobby
        /// whose mod set differs from yours. Turning it off makes this client strictly
        /// visible as modded, at the cost of only matching identically-modded lobbies.
        /// Read at startup only - changing it needs a restart. See LobbyCompat.
        /// </remarks>
        public static bool lobbyCompat = true;

        // --- persistence ----------------------------------------------------

        public static string configPath
        {
            get { return DuckFile.userDirectory + "MidiController/config.txt"; }
        }

        public static void Load()
        {
            try
            {
                string text = DuckFile.LoadString(configPath);
                if (text == null)
                {
                    Log.Info("no config yet - using General MIDI defaults.");
                    return;
                }
                Parse(text);
            }
            catch (Exception e)
            {
                Log.Error("could not read config: " + e.Message + " (using defaults)");
            }
        }

        private static void Parse(string text)
        {
            MidiMapping.ClearBinds();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int badLines = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) { badLines++; continue; }

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                try
                {
                    switch (key)
                    {
                        case "version": break;
                        case "enabled": enabled = ParseBool(val, enabled); break;
                        case "device": deviceName = val; break;
                        case "rootnote": rootNote = ClampInt(ParseInt(val, rootNote), 0, 127); break;
                        case "octavefold": octaveFold = ParseBool(val, octaveFold); break;
                        case "polyphony": polyphony = ParsePolyphony(val); break;
                        case "quackchannel": quackChannel = ClampInt(ParseInt(val, quackChannel), -1, 15); break;
                        case "quackrootnote": quackRootNote = ClampInt(ParseInt(val, quackRootNote), 0, 127); break;
                        case "quackwhenemptyhanded": quackWhenEmptyHanded = ParseBool(val, quackWhenEmptyHanded); break;
                        case "drumchannelstrict": drumChannelStrict = ParseBool(val, drumChannelStrict); break;
                        case "velocityfloor": velocityFloor = ClampInt(ParseInt(val, velocityFloor), 0, 127); break;
                        case "bendrange": bendRange = ParseFloat(val, bendRange); break;
                        case "legatobend": legatoBend = ParseBool(val, legatoBend); break;
                        case "showhud": showHud = ParseBool(val, showHud); break;
                        case "playerslot": playerSlot = ClampInt(ParseInt(val, playerSlot), -1, 3); break;
                        case "firstrundone": firstRunDone = ParseBool(val, firstRunDone); break;
                        case "lobbycompat": lobbyCompat = ParseBool(val, lobbyCompat); break;
                        case "bind":
                            if (!ParseBind(val)) badLines++;
                            break;
                        case "seqsettings":
                            {
                                StepSequencer s = ActiveSequencer();
                                if (s != null) s.DeserializeSettings(val);
                            }
                            break;
                        case "seq":
                            {
                                StepSequencer s = ActiveSequencer();
                                if (s == null) break;
                                // Peek the slot, then parse straight into that pattern.
                                string[] head = val.Split(':');
                                int slotIndex;
                                if (head.Length < 1 || !int.TryParse(head[0], out slotIndex)) { badLines++; break; }
                                StepPattern target = s.SlotAt(slotIndex);
                                if (target == null) { badLines++; break; }
                                if (StepPattern.Deserialize(val, target) < 0) badLines++;
                            }
                            break;
                        default:
                            badLines++;
                            break;
                    }
                }
                catch
                {
                    badLines++;
                }
            }

            if (badLines > 0)
                Log.Warn("config had " + badLines + " line(s) it could not read; they were skipped.");
        }

        // bind=note:<channel>:<number>:<target>[:<step>]
        // channel -1 means "any channel".
        private static bool ParseBind(string val)
        {
            string[] p = val.Split(':');
            if (p.Length < 4) return false;

            bool isCc;
            if (string.Equals(p[0], "note", StringComparison.OrdinalIgnoreCase)) isCc = false;
            else if (string.Equals(p[0], "cc", StringComparison.OrdinalIgnoreCase)) isCc = true;
            else return false;

            int channel = ParseInt(p[1], -1);
            int number = ParseInt(p[2], -1);
            if (number < 0 || number > 127) return false;

            BindTarget target;
            if (!TryParseTarget(p[3], out target)) return false;

            int step = 0;
            if (p.Length >= 5) step = ParseInt(p[4], 0);

            MidiMapping.Bind(channel, number, isCc, target, step);
            return true;
        }

        private static bool TryParseTarget(string s, out BindTarget target)
        {
            target = BindTarget.None;
            string[] names = Enum.GetNames(typeof(BindTarget));
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase))
                {
                    target = (BindTarget)Enum.Parse(typeof(BindTarget), names[i]);
                    return true;
                }
            }
            return false;
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Duck Game MIDI Controller settings");
                sb.AppendLine("# Safe to hand-edit. Reload in game with:  midi reload");
                sb.AppendLine("version=" + FormatVersion);
                sb.AppendLine("enabled=" + Bool(enabled));
                sb.AppendLine("device=" + (deviceName != null ? deviceName : ""));
                sb.AppendLine("rootNote=" + rootNote);
                sb.AppendLine("octaveFold=" + Bool(octaveFold));
                sb.AppendLine("polyphony=" + polyphony);
                sb.AppendLine("quackChannel=" + quackChannel);
                sb.AppendLine("quackRootNote=" + quackRootNote);
                sb.AppendLine("quackWhenEmptyHanded=" + Bool(quackWhenEmptyHanded));
                sb.AppendLine("drumChannelStrict=" + Bool(drumChannelStrict));
                sb.AppendLine("velocityFloor=" + velocityFloor);
                sb.AppendLine("bendRange=" + bendRange.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("legatoBend=" + Bool(legatoBend));
                sb.AppendLine("showHud=" + Bool(showHud));
                sb.AppendLine("playerSlot=" + playerSlot);
                sb.AppendLine("firstRunDone=" + Bool(firstRunDone));
                sb.AppendLine("# Keeps this mod out of the lobby mod hash so you can play with");
                sb.AppendLine("# people who don't have it. Restart the game to apply a change.");
                sb.AppendLine("lobbyCompat=" + Bool(lobbyCompat));

                StepSequencer seq = ActiveSequencer();
                if (seq != null)
                {
                    sb.AppendLine("seqSettings=" + seq.SerializeSettings());
                    for (int i = 0; i < StepSequencer.SlotCount; i++)
                    {
                        StepPattern p = seq.SlotAt(i);
                        if (p == null || p.isEmpty) continue;   // don't clutter with empties
                        sb.AppendLine("seq=" + p.Serialize(i));
                    }
                }

                List<MidiBind> binds = MidiMapping.binds;
                for (int i = 0; i < binds.Count; i++)
                {
                    MidiBind b = binds[i];
                    sb.AppendLine("bind=" + (b.isControlChange ? "cc" : "note") + ":" +
                                  b.channel + ":" + b.number + ":" + b.target + ":" + b.step);
                }

                DuckFile.SaveString(sb.ToString(), configPath);
            }
            catch (Exception e)
            {
                Log.Error("could not save config: " + e.Message);
            }
        }

        public static void ResetToDefaults()
        {
            enabled = true;
            rootNote = 60;
            octaveFold = true;
            polyphony = PolyphonyPolicy.LastNoteWins;
            quackChannel = 1;
            quackRootNote = 48;
            quackWhenEmptyHanded = true;
            drumChannelStrict = true;
            velocityFloor = 1;
            bendRange = 1f;
            legatoBend = false;
            showHud = true;
            playerSlot = -1;
            lobbyCompat = true;
            MidiMapping.ClearBinds();
            Log.Good("settings reset to defaults.");
        }

        // --- small parse helpers (no exceptions escape) ---------------------

        /// <summary>
        /// The live sequencer, or null if the engine hasn't been constructed yet.
        /// </summary>
        /// <remarks>
        /// Config is loaded during OnPreInitialize, and the engine is built in that same
        /// method - so on the very first load this can legitimately be null and pattern
        /// lines are skipped. MidiControllerMod therefore constructs the engine before
        /// calling Load.
        /// </remarks>
        private static StepSequencer ActiveSequencer()
        {
            MidiEngine e = MidiControllerMod.engine;
            return e == null ? null : e.sequencer;
        }

        private static string Bool(bool b) { return b ? "true" : "false"; }

        private static bool ParseBool(string s, bool fallback)
        {
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            return fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            float v;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static int ClampInt(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static PolyphonyPolicy ParsePolyphony(string s)
        {
            if (string.Equals(s, "HighestNote", StringComparison.OrdinalIgnoreCase)) return PolyphonyPolicy.HighestNote;
            if (string.Equals(s, "LowestNote", StringComparison.OrdinalIgnoreCase)) return PolyphonyPolicy.LowestNote;
            return PolyphonyPolicy.LastNoteWins;
        }
    }
}
