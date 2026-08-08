using System;
using System.IO;
using System.Reflection;

namespace DuckGame.MidiController
{
    /// <summary>
    /// Reflection shim over NAudio.Midi.
    /// </summary>
    /// <remarks>
    /// WHY REFLECTION AND NOT A DIRECT REFERENCE:
    ///
    /// ModLoader.AttemptCompile builds the reference list from
    /// AppDomain.CurrentDomain.GetAssemblies() and caches it in a static
    /// CompilerParameters the first time ANY dynamic mod compiles. Mods compile at
    /// MonoMain startup, before SFX.Initialize() pulls NAudio in. Whether NAudio
    /// happens to be loaded at that moment depends on which OTHER mods the user has
    /// installed and in what order - so a direct `using NAudio.Midi;` would compile
    /// for some users and fail with an opaque CS0246 for others.
    ///
    /// Binding late sidesteps that completely. We touch only 7 members and read only
    /// MidiInMessageEventArgs.RawMessage (an Int32), decoding it ourselves, which also
    /// makes us immune to NAudio version drift.
    /// </remarks>
    internal static class NAudioReflection
    {
        private static bool _tried;
        private static bool _ok;
        private static string _failureReason = "not initialized";

        private static Type _tMidiIn;
        private static PropertyInfo _pNumberOfDevices;   // static int MidiIn.NumberOfDevices
        private static MethodInfo _mDeviceInfo;          // static MidiInCapabilities MidiIn.DeviceInfo(int)
        private static PropertyInfo _pProductName;       // MidiInCapabilities.ProductName
        private static MethodInfo _mStart;
        private static MethodInfo _mStop;
        private static MethodInfo _mDispose;
        private static EventInfo _evMessageReceived;
        private static PropertyInfo _pRawMessage;        // MidiInMessageEventArgs.RawMessage

        internal static bool available { get { EnsureInit(); return _ok; } }
        internal static string failureReason { get { EnsureInit(); return _failureReason; } }
        internal static Type messageEventHandlerType
        {
            get { EnsureInit(); return _ok ? _evMessageReceived.EventHandlerType : null; }
        }

        private static void EnsureInit()
        {
            if (_tried) return;
            _tried = true;

            // MidiIn is winmm-based; there is no MIDI input at all off Windows.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                _failureReason = "MIDI input requires Windows (NAudio's MidiIn is winmm-based)";
                return;
            }

            try
            {
                Assembly naudio = LoadNAudio();
                if (naudio == null)
                {
                    _failureReason = "NAudio.dll could not be loaded";
                    return;
                }

                _tMidiIn = naudio.GetType("NAudio.Midi.MidiIn");
                Type tCaps = naudio.GetType("NAudio.Midi.MidiInCapabilities");
                Type tArgs = naudio.GetType("NAudio.Midi.MidiInMessageEventArgs");
                if (_tMidiIn == null || tCaps == null || tArgs == null)
                {
                    _failureReason = "NAudio loaded but NAudio.Midi types are missing";
                    return;
                }

                _pNumberOfDevices = _tMidiIn.GetProperty("NumberOfDevices", BindingFlags.Public | BindingFlags.Static);
                _mDeviceInfo = _tMidiIn.GetMethod("DeviceInfo", BindingFlags.Public | BindingFlags.Static);
                _pProductName = tCaps.GetProperty("ProductName");
                _mStart = _tMidiIn.GetMethod("Start", Type.EmptyTypes);
                _mStop = _tMidiIn.GetMethod("Stop", Type.EmptyTypes);
                _mDispose = _tMidiIn.GetMethod("Dispose", Type.EmptyTypes);
                _evMessageReceived = _tMidiIn.GetEvent("MessageReceived");
                _pRawMessage = tArgs.GetProperty("RawMessage");

                if (_pNumberOfDevices == null || _mDeviceInfo == null || _pProductName == null ||
                    _mStart == null || _mStop == null || _mDispose == null ||
                    _evMessageReceived == null || _pRawMessage == null)
                {
                    _failureReason = "NAudio.Midi API shape is not what we expect (version mismatch?)";
                    return;
                }

                _ok = true;
                _failureReason = null;
            }
            catch (Exception e)
            {
                _failureReason = "exception binding NAudio: " + e.Message;
            }
        }

        private static Assembly LoadNAudio()
        {
            // Cheapest first: it may already be loaded (the game's audio backend uses it).
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    AssemblyName n = loaded[i].GetName();
                    if (string.Equals(n.Name, "NAudio", StringComparison.OrdinalIgnoreCase))
                        return loaded[i];
                }
            }
            catch { }

            try { return Assembly.Load(new AssemblyName("NAudio")); }
            catch { }

            // Last resort: it sits next to DuckGame.exe.
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NAudio.dll");
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            catch { }

            return null;
        }

        internal static int GetDeviceCount()
        {
            EnsureInit();
            if (!_ok) return 0;
            try { return (int)_pNumberOfDevices.GetValue(null, null); }
            catch { return 0; }
        }

        internal static string GetDeviceName(int index)
        {
            EnsureInit();
            if (!_ok) return null;
            try
            {
                object caps = _mDeviceInfo.Invoke(null, new object[] { index });
                if (caps == null) return null;
                return (string)_pProductName.GetValue(caps, null);
            }
            catch { return null; }
        }

        /// <summary>Constructs and starts a MidiIn, wiring <paramref name="handler"/> to MessageReceived.</summary>
        /// <returns>The opaque MidiIn instance, or null on failure (reason written to <paramref name="error"/>).</returns>
        internal static object OpenDevice(int index, Delegate handler, out string error)
        {
            error = null;
            EnsureInit();
            if (!_ok) { error = _failureReason; return null; }

            object midiIn = null;
            try
            {
                midiIn = Activator.CreateInstance(_tMidiIn, new object[] { index });
                _evMessageReceived.AddEventHandler(midiIn, handler);
                _mStart.Invoke(midiIn, null);
                return midiIn;
            }
            catch (TargetInvocationException tie)
            {
                error = tie.InnerException != null ? tie.InnerException.Message : tie.Message;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            // Opening failed partway; don't leak the native handle.
            if (midiIn != null)
            {
                try { _evMessageReceived.RemoveEventHandler(midiIn, handler); } catch { }
                try { _mDispose.Invoke(midiIn, null); } catch { }
            }
            return null;
        }

        internal static void CloseDevice(object midiIn, Delegate handler)
        {
            if (midiIn == null) return;
            EnsureInit();
            if (!_ok) return;
            try { _mStop.Invoke(midiIn, null); } catch { }
            if (handler != null)
            {
                try { _evMessageReceived.RemoveEventHandler(midiIn, handler); } catch { }
            }
            try { _mDispose.Invoke(midiIn, null); } catch { }
        }

        /// <summary>Pulls the raw 32-bit message out of a MidiInMessageEventArgs without naming the type.</summary>
        internal static bool TryGetRawMessage(object eventArgs, out int raw)
        {
            raw = 0;
            if (eventArgs == null || !_ok) return false;
            try
            {
                object v = _pRawMessage.GetValue(eventArgs, null);
                if (v == null) return false;
                raw = (int)v;
                return true;
            }
            catch { return false; }
        }
    }
}
