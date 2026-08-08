using System;

namespace DuckGame.MidiController
{
    /// <summary>
    /// A small corner readout so "is it actually working?" is answerable at a glance.
    /// </summary>
    /// <remarks>
    /// Fades out after a few seconds of silence so it does not clutter normal play, and
    /// comes back the moment a note arrives.
    /// </remarks>
    internal static class MidiHud
    {
        private const int IdleFramesBeforeHide = 180;   // ~3 seconds at 60Hz
        private const int FadeFrames = 30;

        internal static void Draw(Layer layer, MidiEngine engine)
        {
            if (!MidiConfig.showHud) return;
            if (engine == null) return;
            if (layer == null || !object.ReferenceEquals(layer, Layer.HUD)) return;
            if (Level.current == null) return;

            // Nothing to say when the feature is off and idle.
            if (!MidiConfig.enabled) return;

            int idle = engine.framesSinceActivity;
            bool problem = !MidiListener.isOpen;

            float alpha = 1f;
            if (!problem)
            {
                if (idle > IdleFramesBeforeHide + FadeFrames) return;
                if (idle > IdleFramesBeforeHide)
                    alpha = 1f - ((idle - IdleFramesBeforeHide) / (float)FadeFrames);
            }
            else
            {
                alpha = 0.75f;
            }

            string line;
            Color color;
            if (problem)
            {
                line = "MIDI: no device";
                color = Colors.DGRed;
            }
            else
            {
                string instrument = Instruments.DisplayName(engine.heldInstrument);
                string note = engine.lastNoteDescription;
                line = "MIDI " + instrument;
                if (!string.IsNullOrEmpty(note)) line += "  " + note;
                color = engine.isAttached ? Colors.DGGreen : Colors.DGBlue;
            }

            Camera cam = layer.camera;
            if (cam == null) return;

            // Bottom-left, inset a little; small scale so it never dominates.
            Vec2 pos = new Vec2(cam.x + 4f, cam.y + cam.height - 12f);
            Graphics.DrawString(line, pos, color * alpha, (Depth)0.95f, null, 0.5f);
        }
    }
}
