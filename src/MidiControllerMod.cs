using System;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Mod entry point. ModLoader requires exactly one public non-abstract Mod subclass
    /// in the assembly, or it throws ModTypeMissingException.
    /// </summary>
    /// <remarks>
    /// NOTE ON LANGUAGE LEVEL: every file in this mod must be C# 5. Duck Game compiles
    /// mod source in-process with Microsoft.CSharp.CSharpCodeProvider (the in-box .NET
    /// Framework csc), and neither the vanilla nor the Rebuilt install ships a
    /// system.codedom redirect or a roslyn/ folder. So: no string interpolation, no ?.,
    /// no expression-bodied members, no nameof, no tuples, no pattern matching, no
    /// auto-property initializers. Run tools\check-compile.ps1 to verify.
    ///
    /// NOTE ON INITIALIZATION ORDER: everything happens in OnPreInitialize, because
    /// LobbyCompat removes this mod from ModLoader's accessible list there - which means
    /// OnPostInitialize and OnStart are never called for us. See LobbyCompat for why.
    /// Anything that needs Steam, SFX, Content or a graphics device is deferred to the
    /// first frame instead (MidiEngine.EnsureStarted).
    /// </remarks>
    public class MidiControllerMod : Mod
    {
        public static MidiControllerMod instance;
        public static MidiEngine engine;

        /// <summary>Where this mod's own files live, for logging and diagnostics.</summary>
        public static string modDirectory
        {
            get
            {
                if (instance == null || instance.configuration == null)
                    return "";
                return instance.configuration.directory;
            }
        }

        protected override void OnPreInitialize()
        {
            instance = this;

            try
            {
                // Safe this early: ModLoader itself resolves DuckFile.modsDirectory (which
                // goes through Steam.user) to find us in the first place, so the save path
                // is already valid. Opening MIDI hardware is still deferred to frame one.
                MidiConfig.Load();
                MidiCommands.Register();

                engine = new MidiEngine();
                MonoMain.RegisterEngineUpdatable(engine);

                // MUST happen here: the lobby hash is computed immediately after every
                // mod's OnPreInitialize has run.
                if (MidiConfig.lobbyCompat)
                {
                    LobbyCompat.HideFromModHash(this);
                    if (LobbyCompat.applied)
                        Log.Info("loaded. Public lobbies stay joinable - see 'midi status'.");
                    else
                        Log.Warn("loaded, but could not hide from the lobby mod hash: " +
                                 LobbyCompat.failureReason);
                }
                else
                {
                    Log.Warn("loaded with lobbyCompat off - you will only be able to join " +
                             "lobbies whose mod set matches yours exactly.");
                }
            }
            catch (Exception e)
            {
                Log.Error("failed to initialize: " + e);
            }

            base.OnPreInitialize();
        }

        // OnPostInitialize and OnStart are intentionally not overridden - ModLoader will
        // not call them, because LobbyCompat has removed us from the accessible list by
        // the time it walks it. MidiEngine starts itself on the first frame instead.
    }
}
