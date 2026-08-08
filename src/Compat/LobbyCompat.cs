using System;
using System.Collections.Generic;
using System.Reflection;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Keeps this mod out of Duck Game's lobby compatibility hash, so you can play in
    /// public lobbies with people who do not have it.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS
    ///
    /// Duck Game refuses to join a lobby unless your mod hash matches the host's
    /// (NCSteam lobby join, the NMConnect handshake, and the matchmaking filter all
    /// compare ModLoader.modHash). The hash is a CONTENT COMPATIBILITY check: its job is
    /// to stop mods that add Things or NetMessages from desyncing a lobby. Without this,
    /// installing this mod would lock you out of every public lobby.
    ///
    /// The game's own developers maintain a hardcoded exemption list in ModLoader
    /// ("brokenclientsidemods") for client-side mods that do not affect compatibility.
    /// This mod is exactly that category:
    ///
    ///   * it declares no Thing, AmmoType, DestroyType or NetMessage subclasses, so
    ///     Network.gameDataHash (messageTypeHash + Editor.thingTypesHash) is untouched;
    ///   * it only synthesizes local input for the local player's own duck, and only for
    ///     instrument and quack triggers;
    ///   * notes reach other players through the game's own StateBinding and
    ///     NetSoundEffect replication - byte-identical to a human playing the same notes
    ///     on a keyboard.
    ///
    /// That exemption list is baked into the game binary, so a third-party mod cannot add
    /// itself to it. The equivalent, and the only route available, is to drop out of
    /// ModLoader's accessible-mod list before the hash is computed.
    ///
    /// HOW THE TIMING WORKS
    ///
    /// ModLoader.LoadMods runs, in order:
    ///   1. build _sortedAccessibleMods
    ///   2. call OnPreInitialize on every accessible mod      <-- we remove ourselves here
    ///   3. modHash = GetModHash()                            <-- computed without us
    ///   4. PostLoadMods -> OnPostInitialize
    ///   5. Start -> OnStart
    ///
    /// Because the hash is computed at step 3 from that list, removing ourselves at step 2
    /// means the hash is identical to what the same machine would produce without this mod
    /// installed - "nomods" if it is the only mod.
    ///
    /// The cost: steps 4 and 5 skip us, so OnPostInitialize and OnStart never fire. All of
    /// this mod's setup therefore happens in OnPreInitialize and lazily on the first frame.
    ///
    /// What this does NOT affect: the Mods menu reads ModLoader.allMods, not
    /// accessibleMods, so the mod still appears there, can still be enabled/disabled, and
    /// can still be published to the Workshop.
    ///
    /// Honest caveat: because the mod is excluded from the hash, it is also not visible in
    /// a lobby's mod list, so a host enforcing a strict "no mods" policy cannot see it.
    /// Anyone who would rather be strictly visible can turn this off with
    /// `lobbyCompat=false` in the config, at the cost of only being able to join lobbies
    /// whose mod set matches theirs exactly.
    /// </remarks>
    internal static class LobbyCompat
    {
        private static bool _applied;
        private static string _failureReason;

        internal static bool applied { get { return _applied; } }
        internal static string failureReason { get { return _failureReason; } }

        /// <summary>
        /// Removes this mod from ModLoader's accessible list. Must be called from
        /// OnPreInitialize - later is too late, the hash will already be computed.
        /// </summary>
        internal static void HideFromModHash(Mod self)
        {
            if (_applied) return;
            if (self == null) { _failureReason = "no mod instance"; return; }

            try
            {
                // accessibleMods is a get-only property over a private static field, so
                // the field is the only way to mutate the list.
                FieldInfo field = typeof(ModLoader).GetField("_sortedAccessibleMods",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    _failureReason = "ModLoader._sortedAccessibleMods not found";
                    return;
                }

                IList<Mod> list = field.GetValue(null) as IList<Mod>;
                if (list == null)
                {
                    _failureReason = "accessible mod list was not an IList<Mod>";
                    return;
                }

                if (!list.Remove(self))
                {
                    // Already absent - either a build that orders things differently, or
                    // we were called twice. Either way there is nothing to undo.
                    _failureReason = "this mod was not in the accessible list";
                    return;
                }

                _applied = true;
                _failureReason = null;
            }
            catch (Exception e)
            {
                _failureReason = e.Message;
            }
        }

        /// <summary>
        /// The lobby hash this game will advertise and compare against, for diagnostics.
        /// </summary>
        /// <remarks>
        /// ModLoader.modHash is internal, so a mod in its own assembly cannot read it
        /// directly - hence reflection. "nomods" means we are indistinguishable from an
        /// unmodded client.
        /// </remarks>
        internal static string CurrentModHash()
        {
            try
            {
                PropertyInfo p = typeof(ModLoader).GetProperty("modHash",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                if (p == null) return "(unknown)";
                object v = p.GetValue(null, null);
                return v == null ? "(null)" : v.ToString();
            }
            catch { return "(unreadable)"; }
        }

        /// <summary>True when this client looks unmodded to lobbies.</summary>
        internal static bool looksUnmodded()
        {
            return CurrentModHash() == "nomods";
        }
    }
}
