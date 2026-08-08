using System;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Console logging with a consistent prefix, so users can tell our messages apart
    /// from the game's and from other mods'.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[MIDI] ";

        internal static void Info(string message)
        {
            try { DevConsole.Log(Prefix + message, Colors.DGBlue); }
            catch { }
        }

        internal static void Good(string message)
        {
            try { DevConsole.Log(Prefix + message, Colors.DGGreen); }
            catch { }
        }

        internal static void Warn(string message)
        {
            try { DevConsole.Log(Prefix + message, Colors.DGYellow); }
            catch { }
        }

        internal static void Error(string message)
        {
            try { DevConsole.Log(Prefix + message, Colors.DGRed); }
            catch { }
        }

        /// <summary>Logs once per <paramref name="key"/> per cooldown window, to stop error spam.</summary>
        internal static void Throttled(string key, double cooldownSeconds, string message)
        {
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (_throttle.TryGetValue(key, out last) && (now - last).TotalSeconds < cooldownSeconds)
                return;
            _throttle[key] = now;
            Warn(message);
        }

        private static readonly System.Collections.Generic.Dictionary<string, DateTime> _throttle =
            new System.Collections.Generic.Dictionary<string, DateTime>();
    }
}
