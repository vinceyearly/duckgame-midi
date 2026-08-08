using System;
using System.Reflection;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Installs and removes our InputProfile on a Duck, and finds the local duck.
    /// </summary>
    /// <remarks>
    /// WHY REFLECTION: Duck exposes a public VirtualInput property in Duck Game Rebuilt,
    /// but NOT in the stock Steam build - there, only the private field _virtualInput
    /// exists. Since the mod is compiled against whatever build the player runs, using
    /// the property would break the mod for everyone on vanilla. The private field is
    /// present, identically named and typed, in both, and nothing in the vanilla game
    /// ever writes it, so the slot is ours to use.
    /// </remarks>
    internal static class DuckHook
    {
        private static FieldInfo _fiVirtualInput;
        private static bool _resolved;

        internal static bool available
        {
            get
            {
                Resolve();
                return _fiVirtualInput != null;
            }
        }

        internal static string unavailableReason
        {
            get
            {
                Resolve();
                return _fiVirtualInput != null
                    ? null
                    : "Duck._virtualInput not found - this build of Duck Game is not supported";
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _fiVirtualInput = typeof(Duck).GetField("_virtualInput",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (_fiVirtualInput != null && _fiVirtualInput.FieldType != typeof(InputProfile))
                    _fiVirtualInput = null;
            }
            catch
            {
                _fiVirtualInput = null;
            }
        }

        internal static InputProfile GetVirtualInput(Duck d)
        {
            Resolve();
            if (d == null || _fiVirtualInput == null) return null;
            try { return _fiVirtualInput.GetValue(d) as InputProfile; }
            catch { return null; }
        }

        internal static bool SetVirtualInput(Duck d, InputProfile profile)
        {
            Resolve();
            if (d == null || _fiVirtualInput == null) return false;
            try { _fiVirtualInput.SetValue(d, profile); return true; }
            catch { return false; }
        }

        /// <summary>
        /// The duck this player is controlling, or null.
        /// </summary>
        /// <remarks>
        /// Online we take the local profile's duck. Offline or splitscreen we take the
        /// configured player slot, defaulting to the first live local duck - so a single
        /// player never has to configure anything, but a splitscreen host can pick.
        /// </remarks>
        internal static Duck LocalDuck()
        {
            try
            {
                if (Level.current == null) return null;

                if (Network.isActive)
                {
                    Profile p = DuckNetwork.localProfile;
                    if (p == null) return null;
                    Duck d = p.duck;
                    return IsUsable(d) ? d : null;
                }

                int slot = MidiConfig.playerSlot;
                int seen = 0;
                foreach (Profile p in Profiles.active)
                {
                    if (p == null || !p.localPlayer) continue;
                    Duck d = p.duck;
                    if (!IsUsable(d)) { seen++; continue; }
                    if (slot < 0 || seen == slot) return d;
                    seen++;
                }
            }
            catch { }
            return null;
        }

        private static bool IsUsable(Duck d)
        {
            if (d == null) return false;
            try { return !d.removeFromLevel && !d.dead; }
            catch { return false; }
        }
    }
}
