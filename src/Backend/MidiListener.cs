using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

// DuckGame has its own Timer type, which wins over System.Threading.Timer inside the
// DuckGame namespace. Alias so the intent is unambiguous.
using ThreadingTimer = System.Threading.Timer;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Owns the hardware MIDI input: device discovery, open/close, hot-plug recovery,
    /// and the thread-safe handoff of messages to the game's main thread.
    /// </summary>
    /// <remarks>
    /// THREADING CONTRACT: NAudio raises MessageReceived on a Windows multimedia
    /// callback thread. The handler does exactly one thing - enqueue an int. It never
    /// touches a Duck, a Level, SFX, Steam, or any game state. Everything downstream
    /// happens on the main thread when MidiEngine drains the queue. This matches the
    /// pattern the game's own code uses for its background gamepad-enumeration thread.
    /// </remarks>
    internal static class MidiListener
    {
        private const int QueueCap = 512;
        private const double HotplugPollSeconds = 2.0;

        private static readonly ConcurrentQueue<int> _queue = new ConcurrentQueue<int>();
        private static readonly object _deviceLock = new object();

        private static object _midiIn;              // opaque NAudio.Midi.MidiIn
        private static Delegate _handler;
        private static ThreadingTimer _hotplugTimer;

        private static string _openDeviceName;
        private static int _openDeviceIndex = -1;
        private static string _lastError;
        private static long _messagesReceived;
        private static long _messagesDropped;

        // Device-name snapshot, refreshed by the hot-plug poll. The UI reads this
        // instead of calling into MMSYSTEM so opening a menu can never block.
        private static string[] _deviceSnapshot = new string[0];

        internal static bool isOpen { get { return _midiIn != null; } }
        internal static string openDeviceName { get { return _openDeviceName; } }
        internal static string lastError { get { return _lastError; } }
        internal static long messagesReceived { get { return _messagesReceived; } }
        internal static long messagesDropped { get { return _messagesDropped; } }
        internal static int queueDepth { get { return _queue.Count; } }

        // --- discovery ------------------------------------------------------

        /// <summary>Cached device names. Never blocks; refreshed by the hot-plug poll.</summary>
        internal static string[] GetDeviceNames()
        {
            return _deviceSnapshot;
        }

        /// <summary>Queries MMSYSTEM directly. Call sparingly - this can block briefly.</summary>
        internal static string[] RefreshDeviceNames()
        {
            int count = NAudioReflection.GetDeviceCount();
            List<string> names = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string n = NAudioReflection.GetDeviceName(i);
                names.Add(n != null ? n : ("device " + i));
            }
            _deviceSnapshot = names.ToArray();
            return _deviceSnapshot;
        }

        /// <summary>
        /// Resolves a device by case-insensitive name substring.
        /// </summary>
        /// <remarks>
        /// Deliberately name-based, not index-based: indices renumber whenever any MIDI
        /// device is plugged or unplugged, so a stored index silently points at the wrong
        /// hardware. Note MMSYSTEM truncates product names to 31 characters, so a stored
        /// name is matched against the truncated form.
        /// </remarks>
        internal static int FindDeviceIndex(string nameFragment)
        {
            if (string.IsNullOrEmpty(nameFragment)) return -1;
            string[] names = RefreshDeviceNames();

            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], nameFragment, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] != null &&
                    names[i].IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            // Stored name may be the 31-char truncation of a longer one, or vice versa.
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] != null && names[i].Length > 0 &&
                    nameFragment.IndexOf(names[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Picks a sensible device for zero-config first run: the first input that is not
        /// an obvious virtual loopback or MIDI-through port.
        /// </summary>
        internal static int PickDefaultDevice()
        {
            string[] names = RefreshDeviceNames();
            if (names.Length == 0) return -1;

            for (int i = 0; i < names.Length; i++)
            {
                if (!LooksLikeLoopback(names[i]))
                    return i;
            }
            return 0;   // everything looks virtual; the user's setup may genuinely be virtual
        }

        private static readonly string[] _loopbackHints =
            { "loopmidi", "loopbe", "midi through", "through port", "virtual midi", "rtpmidi", "iac driver" };

        private static bool LooksLikeLoopback(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < _loopbackHints.Length; i++)
            {
                if (lower.Contains(_loopbackHints[i])) return true;
            }
            return false;
        }

        // --- open / close ---------------------------------------------------

        internal static bool Open(int index)
        {
            lock (_deviceLock)
            {
                CloseLocked();

                if (!NAudioReflection.available)
                {
                    _lastError = NAudioReflection.failureReason;
                    return false;
                }

                string[] names = RefreshDeviceNames();
                if (index < 0 || index >= names.Length)
                {
                    _lastError = "no MIDI input at index " + index;
                    return false;
                }

                Delegate handler = CreateHandler();
                if (handler == null)
                {
                    _lastError = "could not bind the MessageReceived handler";
                    return false;
                }

                string error;
                object midiIn = NAudioReflection.OpenDevice(index, handler, out error);
                if (midiIn == null)
                {
                    // Most common cause by far: another application already holds the port.
                    _lastError = error != null ? error : "unknown error opening device";
                    return false;
                }

                _midiIn = midiIn;
                _handler = handler;
                _openDeviceIndex = index;
                _openDeviceName = names[index];
                _lastError = null;
                return true;
            }
        }

        internal static bool OpenByName(string nameFragment)
        {
            int idx = FindDeviceIndex(nameFragment);
            if (idx < 0)
            {
                _lastError = "no MIDI input matching \"" + nameFragment + "\"";
                return false;
            }
            return Open(idx);
        }

        internal static void Close()
        {
            lock (_deviceLock) { CloseLocked(); }
        }

        private static void CloseLocked()
        {
            if (_midiIn != null)
            {
                NAudioReflection.CloseDevice(_midiIn, _handler);
                _midiIn = null;
                _handler = null;
            }
            _openDeviceIndex = -1;
            _openDeviceName = null;
            DrainAndDiscard();
        }

        // --- the callback ---------------------------------------------------

        private static Delegate CreateHandler()
        {
            Type handlerType = NAudioReflection.messageEventHandlerType;
            if (handlerType == null) return null;
            try
            {
                MethodInfo mi = typeof(MidiListener).GetMethod("OnMidiMessage",
                    BindingFlags.NonPublic | BindingFlags.Static);
                // Relaxed delegate binding: the delegate's parameter is
                // MidiInMessageEventArgs, ours is its base EventArgs. Contravariance
                // makes this legal and means we never name the NAudio type.
                return Delegate.CreateDelegate(handlerType, mi);
            }
            catch { return null; }
        }

        private static void OnMidiMessage(object sender, EventArgs args)
        {
            // MULTIMEDIA CALLBACK THREAD. Enqueue and return - nothing else.
            try
            {
                int raw;
                if (!NAudioReflection.TryGetRawMessage(args, out raw))
                    return;

                _queue.Enqueue(raw);
                Interlocked.Increment(ref _messagesReceived);

                // Bound the queue so a minimised or loading game can't grow it forever.
                while (_queue.Count > QueueCap)
                {
                    int discard;
                    if (!_queue.TryDequeue(out discard)) break;
                    Interlocked.Increment(ref _messagesDropped);
                }
            }
            catch { /* never let an exception cross back into native code */ }
        }

        internal static bool TryDequeue(out int raw)
        {
            return _queue.TryDequeue(out raw);
        }

        /// <summary>
        /// Pushes a synthetic message into the queue as though it came from hardware.
        /// </summary>
        /// <remarks>
        /// Used by `midi test`. It exercises decode, routing, voice allocation and
        /// trigger injection end to end, so a user can prove the mod works before
        /// working out why their controller is silent - and so the whole pipeline is
        /// testable without a MIDI device attached.
        /// </remarks>
        internal static void InjectRaw(int raw)
        {
            _queue.Enqueue(raw);
            Interlocked.Increment(ref _messagesReceived);
        }

        /// <summary>Builds a raw short message. <paramref name="channel"/> is 0-based.</summary>
        internal static int BuildRaw(int status, int channel, int data1, int data2)
        {
            return ((status & 0xF0) | (channel & 0x0F))
                 | ((data1 & 0x7F) << 8)
                 | ((data2 & 0x7F) << 16);
        }

        internal static void DrainAndDiscard()
        {
            int discard;
            while (_queue.TryDequeue(out discard)) { }
        }

        // --- hot-plug -------------------------------------------------------

        internal static void StartHotplugWatch()
        {
            if (_hotplugTimer != null) return;
            _hotplugTimer = new ThreadingTimer(HotplugTick, null,
                TimeSpan.FromSeconds(HotplugPollSeconds),
                TimeSpan.FromSeconds(HotplugPollSeconds));
        }

        internal static void StopHotplugWatch()
        {
            if (_hotplugTimer == null) return;
            _hotplugTimer.Dispose();
            _hotplugTimer = null;
        }

        /// <summary>
        /// Reconciles the open device against reality every couple of seconds.
        /// NAudio gives no device-removal notification, so polling is the only option.
        /// </summary>
        private static void HotplugTick(object state)
        {
            try
            {
                if (!MidiConfig.enabled) return;

                lock (_deviceLock)
                {
                    string[] names = RefreshDeviceNames();
                    string wanted = MidiConfig.deviceName;

                    if (_midiIn != null)
                    {
                        // Still pointing at the device we think we are?
                        bool stillThere = _openDeviceIndex >= 0
                            && _openDeviceIndex < names.Length
                            && string.Equals(names[_openDeviceIndex], _openDeviceName, StringComparison.OrdinalIgnoreCase);
                        if (!stillThere)
                        {
                            Log.Warn("MIDI device \"" + _openDeviceName + "\" went away.");
                            CloseLocked();
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (string.IsNullOrEmpty(wanted)) return;

                    int idx = -1;
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i] != null &&
                            (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase) ||
                             names[i].IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0))
                        { idx = i; break; }
                    }
                    if (idx < 0) return;

                    Delegate handler = CreateHandler();
                    if (handler == null) return;

                    string error;
                    object midiIn = NAudioReflection.OpenDevice(idx, handler, out error);
                    if (midiIn == null)
                    {
                        Log.Throttled("hotplug-open", 60.0,
                            "could not reopen \"" + wanted + "\": " + (error != null ? error : "unknown"));
                        return;
                    }

                    _midiIn = midiIn;
                    _handler = handler;
                    _openDeviceIndex = idx;
                    _openDeviceName = names[idx];
                    _lastError = null;
                    Log.Good("reconnected to \"" + _openDeviceName + "\".");
                }
            }
            catch (Exception e)
            {
                Log.Throttled("hotplug", 60.0, "hot-plug poll failed: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            StopHotplugWatch();
            Close();
        }
    }
}
