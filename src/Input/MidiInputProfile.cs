using System;
using System.Collections.Generic;

namespace DuckGame.MidiController
{
    /// <summary>
    /// An InputProfile that passes the player's real input straight through, while
    /// letting us synthesize the specific triggers the instruments read.
    /// </summary>
    /// <remarks>
    /// WHY A WRAPPER AND NOT A REPLACEMENT: Duck.inputProfile returns _virtualInput for
    /// everything, not just instrument code. If we returned only synthetic input the
    /// player could not walk, jump, grab or aim. So every query falls through to the
    /// real profile unless we are actively driving it.
    ///
    /// WHY virtualDevice IS INSTALLED ON OURSELVES: leftStick, rightStick, rightTrigger,
    /// hasMotionAxis and motionAxis are NOT virtual, so we cannot override them. They do
    /// however short-circuit on InputProfile's own _virtualInput device. Installing a
    /// VirtualInput on THIS instance makes those getters read fields we control, which we
    /// mirror from the real profile each frame. Without it, aiming and analog run speed
    /// would read zero the moment an instrument was picked up.
    ///
    /// This is safe because network input replication reads Profile.inputProfile, never
    /// Duck._virtualInput - so nothing we do here is visible to the netcode.
    /// </remarks>
    public sealed class MidiInputProfile : InputProfile
    {
        private InputProfile _real;

        private readonly HashSet<string> _down = new HashSet<string>();
        private readonly HashSet<string> _pressed = new HashSet<string>();
        private readonly HashSet<string> _released = new HashSet<string>();

        private float _leftTrigger;
        private bool _driveLeftTrigger;

        /// <summary>Set on the frame we attach, to suppress a spurious first-frame SHOOT.</summary>
        internal bool justAttached;

        public MidiInputProfile()
            : base("")
        {
            virtualDevice = new VirtualInput(0);
        }

        internal InputProfile realProfile { get { return _real; } }

        /// <summary>
        /// Refreshes pass-through state and expires last frame's edges.
        /// </summary>
        /// <remarks>
        /// Edge lifetime, and why it must be exactly here: we clear at the start of
        /// PreUpdate (frame N), the router fills them during the same PreUpdate, the
        /// level consumes them in Level.UpdateCurrentLevel later in frame N, and they are
        /// gone by PreUpdate of frame N+1. That is precisely one frame of visibility,
        /// which is what a real key press produces.
        ///
        /// Do NOT move this to PostUpdate: PostUpdate still runs while the game is
        /// paused but PreUpdate does not, so the edge would outlive its frame.
        /// </remarks>
        internal void BeginFrame(InputProfile real)
        {
            _real = real;
            _pressed.Clear();
            _released.Clear();
            _down.Clear();
            _driveLeftTrigger = false;

            VirtualInput vd = virtualDevice;
            if (vd != null && real != null)
            {
                vd.leftStick = real.leftStick;
                vd.rightStick = real.rightStick;
                vd.rightTrigger = real.rightTrigger;   // routers may overwrite below
            }
        }

        // --- emission (called by the routers) -------------------------------

        internal void EmitPressed(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return;
            _pressed.Add(trigger);
            _down.Add(trigger);
        }

        internal void EmitDown(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return;
            _down.Add(trigger);
        }

        internal void EmitReleased(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return;
            _released.Add(trigger);
            _down.Remove(trigger);
        }

        internal void DriveLeftTrigger(float value)
        {
            _leftTrigger = value;
            _driveLeftTrigger = true;
        }

        internal void DriveRightTrigger(float value)
        {
            VirtualInput vd = virtualDevice;
            if (vd != null) vd.rightTrigger = value;
        }

        internal void ClearAll()
        {
            _pressed.Clear();
            _released.Clear();
            _down.Clear();
            _driveLeftTrigger = false;
        }

        // --- overrides ------------------------------------------------------
        // Falling through to the real profile is not just politeness: InputProfile.Down
        // maintains _lastActiveDevice and Input.lastActiveProfile, and the rest of the
        // game expects that bookkeeping to happen on the real object.

        public override bool Pressed(string trigger, bool any = false)
        {
            if (_pressed.Contains(trigger)) return true;
            return _real != null && _real.Pressed(trigger, any);
        }

        public override bool Released(string trigger)
        {
            if (_released.Contains(trigger)) return true;
            return _real != null && _real.Released(trigger);
        }

        public override bool Down(string trigger)
        {
            if (_down.Contains(trigger)) return true;
            return _real != null && _real.Down(trigger);
        }

        public override float leftTrigger
        {
            get
            {
                if (_driveLeftTrigger) return _leftTrigger;
                if (_real == null) return 0f;
                float v = _real.leftTrigger;
                // hasMotionAxis reads false on us (it requires a VirtualInput in
                // _mappings, and ours is empty), so fold the real profile's gyro in here
                // to keep motion-control quack pitch working.
                if (_real.hasMotionAxis) v += _real.motionAxis;
                return v;
            }
        }
    }
}
