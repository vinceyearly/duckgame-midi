using System;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Turns the current note state into trigger emissions, once per frame.
    /// </summary>
    /// <remarks>
    /// Each instrument family needs different handling because the game retriggers them
    /// differently. The awkward one is Saxaphone/Trombone: they only start a new sample
    /// when their internal noteSound is null, and it is only nulled when notePitch
    /// reaches 0. So moving from one note to another with SHOOT continuously held just
    /// bends the existing sample instead of playing the new one. A one-frame release is
    /// the only way to make consecutive notes actually articulate.
    /// </remarks>
    internal class InstrumentRouter
    {
        private readonly NoteStack _notes = new NoteStack();

        // Melodic state
        private int _currentStep = -1;
        private int _pendingStep = -1;
        private bool _shootHeld;

        // Trumpet state
        private int _trumpetPitch = -1;

        // Drum state: queued hits per voice, so a fast flam is spread across frames
        // rather than collapsing into one hit.
        private readonly int[] _pendingHits = new int[(int)DrumVoice.Count];
        private const int MaxQueuedHits = 3;

        // Quack is edge-triggered and fire-and-forget.
        private int _pendingQuacks;
        private float _quackPitch;

        private float _bend;
        private bool _presetNextQueued;

        internal int activeStep { get { return _currentStep; } }
        internal int heldNoteCount { get { return _notes.count; } }

        // --- message intake (main thread, before Emit) -----------------------

        internal void HandleMessage(MidiMessage m, InstrumentKind held)
        {
            RouteResult r = MidiMapping.Route(m, held);
            if (r.target == BindTarget.None) return;

            switch (r.target)
            {
                case BindTarget.Sustain:
                    _notes.SetSustain(r.step >= 64);
                    return;

                case BindTarget.Bend:
                    // 0..16383, centre 8192. The game only bends upward, so the lower
                    // half of the wheel is inert.
                    {
                        float norm = (r.step - 8192) / 8192f;
                        if (norm < 0f) norm = 0f;
                        norm *= MidiConfig.bendRange;
                        if (norm > 1f) norm = 1f;
                        _bend = norm;
                    }
                    return;

                case BindTarget.KeytarPresetNext:
                    if (m.kind == MidiKind.NoteOn) _presetNextQueued = true;
                    return;

                case BindTarget.Quack:
                    if (m.kind == MidiKind.NoteOn && m.value >= MidiConfig.velocityFloor)
                    {
                        _pendingQuacks++;
                        if (_pendingQuacks > MaxQueuedHits) _pendingQuacks = MaxQueuedHits;
                        _quackPitch = r.pitch01;
                    }
                    return;
            }

            if (MidiMapping.IsDrumTarget(r.target))
            {
                // Drums are percussive: note-offs are meaningless.
                if (m.kind != MidiKind.NoteOn) return;
                if (m.value < MidiConfig.velocityFloor) return;
                int idx = (int)r.voice;
                if (idx < 0 || idx >= _pendingHits.Length) return;
                _pendingHits[idx]++;
                if (_pendingHits[idx] > MaxQueuedHits) _pendingHits[idx] = MaxQueuedHits;
                return;
            }

            if (r.target == BindTarget.MelodicStep)
            {
                if (m.kind == MidiKind.NoteOn)
                {
                    if (m.value < MidiConfig.velocityFloor) return;
                    _notes.NoteOn(m.number);
                }
                else
                {
                    _notes.NoteOff(m.number);
                }
            }
        }

        // --- sequencer entry points -----------------------------------------
        // The sequencer stores voices and scale steps, not MIDI note numbers, so it feeds
        // the router directly rather than synthesizing notes and routing them back through
        // MidiMapping. That avoids a lossy round-trip through the GM drum map, and means a
        // sequenced hit lands in exactly the same per-voice queue and gap machine that live
        // playing uses - so playback is indistinguishable from someone playing it by hand.

        internal void SequencerDrumHit(DrumVoice v)
        {
            int idx = (int)v;
            if (idx < 0 || idx >= _pendingHits.Length) return;
            _pendingHits[idx]++;
            if (_pendingHits[idx] > MaxQueuedHits) _pendingHits[idx] = MaxQueuedHits;
        }

        /// <summary>
        /// Sounds a sequenced melodic note. The note number is <c>rootNote + step</c> so it
        /// resolves back through the normal note maths, and so an explicit user binding on
        /// that note still wins - which is what someone who remapped it would expect.
        /// </summary>
        internal void SequencerNoteOn(int noteNumber)
        {
            _notes.NoteOn(noteNumber);
        }

        internal void SequencerNoteOff(int noteNumber)
        {
            _notes.NoteOff(noteNumber);
        }

        // --- emission (once per frame, into the profile) ---------------------

        internal void Emit(MidiInputProfile profile, InstrumentKind held, bool suppressShoot)
        {
            EmitQuack(profile);

            switch (held)
            {
                case InstrumentKind.DrumSet:
                    EmitDrums(profile);
                    break;
                case InstrumentKind.Trumpet:
                    EmitTrumpet(profile, suppressShoot);
                    break;
                case InstrumentKind.Keytar:
                    EmitKeytar(profile, suppressShoot);
                    break;
                case InstrumentKind.Saxaphone:
                case InstrumentKind.Trombone:
                    EmitWind(profile, held, suppressShoot);
                    break;
                default:
                    // Nothing playable held - quack still works, but never emit an
                    // instrument trigger. SHOOT on a real gun would fire it.
                    ResetInstrumentState();
                    break;
            }
        }

        private void EmitQuack(MidiInputProfile profile)
        {
            if (_pendingQuacks <= 0) return;
            _pendingQuacks--;
            // Duck reads leftTrigger on the same frame it sees Pressed(QUACK) and turns
            // it into quackPitch, which replicates to other players on its own.
            profile.DriveLeftTrigger(_quackPitch);
            profile.EmitPressed(Triggers.Quack);
        }

        private void EmitDrums(MidiInputProfile profile)
        {
            for (int i = 0; i < _pendingHits.Length; i++)
            {
                if (_pendingHits[i] <= 0) continue;
                _pendingHits[i]--;
                string trigger = Instruments.TriggerFor((DrumVoice)i);
                profile.EmitPressed(trigger);
            }
        }

        private void EmitTrumpet(MidiInputProfile profile, bool suppressShoot)
        {
            int desiredNote = _notes.Active();
            int desired = -1;
            if (desiredNote >= 0)
            {
                RouteResult r = MidiMapping.Route(MakeNote(desiredNote), InstrumentKind.Trumpet);
                if (r.target == BindTarget.MelodicStep) desired = r.step;
            }

            if (desired == _trumpetPitch)
            {
                if (_trumpetPitch >= 0) HoldTrumpet(profile, _trumpetPitch, suppressShoot);
                return;
            }

            // Trumpet stops its previous sound before starting a new one, so a release
            // and a press can share a frame. The game's release branch is guarded on the
            // pitch matching, so ordering is safe.
            if (_trumpetPitch >= 0)
                ReleaseTrumpet(profile, _trumpetPitch);

            _trumpetPitch = desired;
            if (desired >= 0)
                PressTrumpet(profile, desired, suppressShoot);
        }

        private void PressTrumpet(MidiInputProfile profile, int pitch, bool suppressShoot)
        {
            switch (pitch)
            {
                case 0: profile.EmitPressed(Triggers.Strafe); break;
                case 1: profile.EmitPressed(Triggers.Ragdoll); break;
                case 2: if (!suppressShoot) profile.EmitPressed(Triggers.Shoot); break;
                default: profile.DriveRightTrigger(1f); break;   // the 4th valve
            }
        }

        private void HoldTrumpet(MidiInputProfile profile, int pitch, bool suppressShoot)
        {
            switch (pitch)
            {
                case 0: profile.EmitDown(Triggers.Strafe); break;
                case 1: profile.EmitDown(Triggers.Ragdoll); break;
                case 2: if (!suppressShoot) profile.EmitDown(Triggers.Shoot); break;
                default: profile.DriveRightTrigger(1f); break;
            }
        }

        private void ReleaseTrumpet(MidiInputProfile profile, int pitch)
        {
            switch (pitch)
            {
                case 0: profile.EmitReleased(Triggers.Strafe); break;
                case 1: profile.EmitReleased(Triggers.Ragdoll); break;
                case 2: profile.EmitReleased(Triggers.Shoot); break;
                default: profile.DriveRightTrigger(0f); break;
            }
        }

        /// <summary>
        /// Keytar retriggers whenever its note index changes, so notes can be slurred
        /// with SHOOT held. Only a repeated identical note needs a gap.
        /// </summary>
        private void EmitKeytar(MidiInputProfile profile, bool suppressShoot)
        {
            if (_presetNextQueued)
            {
                _presetNextQueued = false;
                profile.EmitPressed(Triggers.Strafe);
            }

            profile.DriveRightTrigger(_bend);

            int desired = DesiredStep(InstrumentKind.Keytar);

            if (_pendingStep >= 0)
            {
                _currentStep = _pendingStep;
                _pendingStep = -1;
                DriveMelodic(profile, InstrumentKind.Keytar, _currentStep, true, suppressShoot);
                return;
            }
            if (desired < 0)
            {
                StopMelodic(profile);
                return;
            }
            DriveMelodic(profile, InstrumentKind.Keytar, desired, _currentStep < 0, suppressShoot);
            _currentStep = desired;
        }

        /// <summary>
        /// Saxophone and trombone: the gap machine.
        /// </summary>
        private void EmitWind(MidiInputProfile profile, InstrumentKind kind, bool suppressShoot)
        {
            int desired = DesiredStep(kind);

            // Coming out of a gap frame: start the queued note.
            if (_pendingStep >= 0)
            {
                _currentStep = _pendingStep;
                _pendingStep = -1;
                DriveMelodic(profile, kind, _currentStep, true, suppressShoot);
                return;
            }

            if (desired < 0)
            {
                StopMelodic(profile);
                return;
            }

            if (_currentStep < 0)
            {
                DriveMelodic(profile, kind, desired, true, suppressShoot);
                _currentStep = desired;
                return;
            }

            if (desired != _currentStep)
            {
                // Adjacent steps can be slurred by just moving leftTrigger - the game
                // bends the sounding sample, which is the authentic trombone slide.
                if (MidiConfig.legatoBend && Math.Abs(desired - _currentStep) <= 1)
                {
                    DriveMelodic(profile, kind, desired, false, suppressShoot);
                    _currentStep = desired;
                    return;
                }

                // GAP FRAME: release SHOOT so the game nulls its noteSound, then start
                // the new note next frame. Costs ~17ms of silence; unavoidable without
                // patching the game.
                profile.EmitReleased(Triggers.Shoot);
                _shootHeld = false;
                _currentStep = -1;
                _pendingStep = desired;
                return;
            }

            DriveMelodic(profile, kind, _currentStep, false, suppressShoot);
        }

        private int DesiredStep(InstrumentKind kind)
        {
            int note = _notes.Active();
            if (note < 0) return -1;
            RouteResult r = MidiMapping.Route(MakeNote(note), kind);
            if (r.target != BindTarget.MelodicStep) return -1;
            return r.step;
        }

        private void DriveMelodic(MidiInputProfile profile, InstrumentKind kind, int step,
                                  bool attack, bool suppressShoot)
        {
            profile.DriveLeftTrigger(Instruments.StepToLeftTrigger(kind, step));
            if (suppressShoot) return;
            if (attack || !_shootHeld)
            {
                profile.EmitPressed(Triggers.Shoot);
                _shootHeld = true;
            }
            else
            {
                profile.EmitDown(Triggers.Shoot);
            }
        }

        private void StopMelodic(MidiInputProfile profile)
        {
            if (_shootHeld)
            {
                profile.EmitReleased(Triggers.Shoot);
                _shootHeld = false;
            }
            _currentStep = -1;
            _pendingStep = -1;
        }

        private static MidiMessage MakeNote(int note)
        {
            MidiMessage m = new MidiMessage();
            m.kind = MidiKind.NoteOn;
            m.number = note;
            m.value = 100;
            m.channel = 0;
            return m;
        }

        private void ResetInstrumentState()
        {
            _currentStep = -1;
            _pendingStep = -1;
            _shootHeld = false;
            _trumpetPitch = -1;
            for (int i = 0; i < _pendingHits.Length; i++) _pendingHits[i] = 0;
        }

        /// <summary>
        /// Full reset. Called on detach, level change, device loss and `midi panic`.
        /// </summary>
        internal void Panic(MidiInputProfile profile)
        {
            _notes.Clear();
            ResetInstrumentState();
            _pendingQuacks = 0;
            _presetNextQueued = false;
            _bend = 0f;
            if (profile != null)
            {
                profile.EmitReleased(Triggers.Shoot);
                profile.ClearAll();
            }
        }
    }
}
