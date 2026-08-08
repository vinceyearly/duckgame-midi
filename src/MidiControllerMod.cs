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
            base.OnPreInitialize();
        }

        protected override void OnPostInitialize()
        {
            // Mod init runs before SFX.Initialize() and before the graphics device is
            // fully up, so nothing here may touch SFX, Content, Layer or any Tex2D.
            // All UI construction is deferred to first open.
            try
            {
                MidiConfig.Load();
                MidiCommands.Register();

                engine = new MidiEngine();
                MonoMain.RegisterEngineUpdatable(engine);

                Log.Info("loaded. Press F9 in a match for settings, or type 'midi' in the console.");
            }
            catch (Exception e)
            {
                Log.Error("failed to initialize: " + e);
            }

            base.OnPostInitialize();
        }

        protected override void OnStart()
        {
            // Called once the first Level has been set - safe to open hardware now.
            try
            {
                if (engine != null)
                    engine.StartIfEnabled();
            }
            catch (Exception e)
            {
                Log.Error("failed to start: " + e);
            }

            base.OnStart();
        }
    }
}
