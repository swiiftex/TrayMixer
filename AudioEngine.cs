using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TrayMixer
{
    enum RowKind { App, SystemSounds, DiscordSounds }

    struct Level : IEquatable<Level>
    {
        public static readonly Level Default = new Level(100, false);

        public readonly int Volume;
        public readonly bool Muted;

        public Level(int volume, bool muted)
        {
            Volume = Math.Max(0, Math.Min(100, volume));
            Muted = muted;
        }

        public bool IsDefault => Volume == 100 && !Muted;
        public float Gain => Muted ? 0f : Volume / 100f;
        public bool Equals(Level other) => Volume == other.Volume && Muted == other.Muted;
    }

    sealed class AppState
    {
        public string Key;
        public string Name;
        public string ImagePath;
        public RowKind Kind;
        public Level Level;
        public float Peak;
    }

    sealed class DeviceState
    {
        public string Id;
        public string Name;
        public Level Level;
        public float Peak;
        public bool IsDefault;
    }

    sealed class MixerSnapshot
    {
        public readonly List<DeviceState> Devices = new List<DeviceState>();
        public readonly List<AppState> Apps = new List<AppState>();
        public DeviceState DefaultDevice => Devices.Find(d => d.IsDefault);
    }

    // Owns all Core Audio objects on a dedicated MTA thread (session notifications are only delivered to MTA clients),
    // publishes snapshots of every output device and every app's sessions, and restores remembered app volumes when an
    // app's audio session is recreated (many apps, Chromium-based ones especially, start at 100% every time).
    sealed class AudioEngine : IDisposable
    {
        public const string SystemSoundsKey = "#system";
        public const string DiscordSoundsKey = "#discord-sounds";

        const int IdlePollMilliseconds = 1500;
        const int LivePollMilliseconds = 33;
        const int LiveRescanMilliseconds = 1000;
        static Guid EventContext = new Guid("5B0E4A52-7C8D-4C61-9E8B-2D7F3A1C6E44");

        readonly SynchronizationContext _ui;
        readonly Action<MixerSnapshot> _onSnapshot;
        readonly Action<Dictionary<string, Level>> _onRememberedChanged;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly ConcurrentQueue<Action> _commands = new ConcurrentQueue<Action>();
        Thread _thread;
        volatile bool _stopping, _live, _sessionsChanged;

        // Everything below is only touched on the audio thread.
        readonly Dictionary<string, Level> _remembered;
        readonly Dictionary<uint, ProcessDetails> _processes = new Dictionary<uint, ProcessDetails>();
        readonly Dictionary<string, string> _appNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Device> _devices = new Dictionary<string, Device>();
        List<Session> _sessions = new List<Session>();
        IMMDeviceEnumerator _enumerator;
        SessionNotifier _sessionNotifier;
        VolumeNotifier _volumeNotifier;
        string _defaultId;
        string _lastSignature;

        sealed class Device
        {
            public string Name;
            public IAudioSessionManager2 Sessions;
            public IAudioEndpointVolume Volume;
            public IAudioMeterInformation Meter;
        }

        sealed class Session
        {
            public IAudioSessionControl2 Control;
            public string DeviceId;
            public string AppKey;
            public string ImagePath;
            public string DisplayName;
            public RowKind Kind;
            public bool IsDiscordSounds;
        }

        public AudioEngine(IDictionary<string, Level> remembered, Action<MixerSnapshot> onSnapshot, Action<Dictionary<string, Level>> onRememberedChanged)
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _remembered = new Dictionary<string, Level>(remembered, StringComparer.OrdinalIgnoreCase);
            _onSnapshot = onSnapshot;
            _onRememberedChanged = onRememberedChanged;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Audio sessions" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        // While the mixer is open, refresh levels and peak meters at ~30 fps instead of every 1.5 s.
        public bool Live
        {
            set
            {
                _live = value;
                if (value)
                    _sessionsChanged = true;
                _wake.Set();
            }
        }

        public void SetDevice(string deviceId, Level level) => Post(() => ApplyDevice(deviceId, level));

        public void SetApp(string key, Level level) => Post(() => ApplyApp(key, level));

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _thread?.Join(2000);
        }

        void Post(Action command)
        {
            _commands.Enqueue(command);
            _wake.Set();
        }

        void Run()
        {
            _enumerator = CoreAudio.CreateEnumerator();
            _sessionNotifier = new SessionNotifier(() => { _sessionsChanged = true; _wake.Set(); });
            _volumeNotifier = new VolumeNotifier(() => _wake.Set());
            int lastScan = 0;
            bool scanned = false;
            try
            {
                while (!_stopping)
                {
                    bool live = _live;
                    while (_commands.TryDequeue(out Action command))
                        Guarded(command);

                    if (!scanned || _sessionsChanged || unchecked(Environment.TickCount - lastScan) >= (live ? LiveRescanMilliseconds : IdlePollMilliseconds))
                    {
                        _sessionsChanged = false;
                        Guarded(Rescan);
                        lastScan = Environment.TickCount;
                        scanned = true;
                    }

                    MixerSnapshot snapshot = null;
                    Guarded(() => snapshot = BuildSnapshot(live));
                    if (snapshot != null)
                        Publish(snapshot, live);
                    _wake.WaitOne(live ? LivePollMilliseconds : IdlePollMilliseconds);
                }
            }
            finally
            {
                Shutdown();
            }
        }

        static void Guarded(Action action)
        {
            try { action(); }
            catch (COMException) { }
            catch (InvalidCastException) { }
        }

        // ---- Commands ----

        void ApplyDevice(string deviceId, Level level)
        {
            if (!_devices.TryGetValue(deviceId, out Device device) || device.Volume == null)
                return;
            device.Volume.SetMasterVolumeLevelScalar(level.Volume / 100f, ref EventContext);
            device.Volume.SetMute(level.Muted, ref EventContext);
        }

        void ApplyApp(string key, Level level)
        {
            foreach (var session in _sessions)
            {
                if (key == DiscordSoundsKey ? session.IsDiscordSounds : session.AppKey == key)
                    ApplyLevel(session, key, level);
            }
            if (Remember(key, level))
                PublishRemembered();
        }

        Level DiscordSoundsLevel => _remembered.TryGetValue(DiscordSoundsKey, out Level level) ? level : Level.Default;

        bool Remember(string key, Level level)
        {
            bool known = _remembered.TryGetValue(key, out Level previous);
            if (level.IsDefault)
                return known && _remembered.Remove(key);
            if (known && previous.Equals(level))
                return false;
            _remembered[key] = level;
            return true;
        }

        static void ApplyLevel(Session session, string key, Level level)
        {
            if (key == DiscordSoundsKey)
            {
                // Discord voice shares the session master volume, so its sound effects are set per channel instead.
                var channels = (IChannelAudioVolume)session.Control;
                if (channels.GetChannelCount(out int count) != 0)
                    return;
                for (int i = 0; i < count; i++)
                {
                    if (channels.GetChannelVolume(i, out float current) == 0 && Math.Abs(current - level.Gain) > 0.001f)
                        channels.SetChannelVolume(i, level.Gain, ref EventContext);
                }
                return;
            }
            var simple = (ISimpleAudioVolume)session.Control;
            if (simple.GetMasterVolume(out float volume) == 0 && Math.Abs(volume - level.Volume / 100f) > 0.001f)
                simple.SetMasterVolume(level.Volume / 100f, ref EventContext);
            if (simple.GetMute(out bool muted) == 0 && muted != level.Muted)
                simple.SetMute(level.Muted, ref EventContext);
        }

        // ---- Scanning ----

        void Rescan()
        {
            SyncDevices();
            SyncDefaultDevice();

            var previous = new HashSet<object>();
            foreach (var session in _sessions)
                previous.Add(session.Control);

            var found = new List<Session>();
            var livePids = new HashSet<uint>();
            foreach (var device in new List<KeyValuePair<string, Device>>(_devices))
            {
                try
                {
                    CollectSessions(device.Key, device.Value.Sessions, found, livePids);
                }
                catch (COMException)
                {
                    DropDevice(device.Key);
                }
            }
            foreach (uint pid in new List<uint>(_processes.Keys))
            {
                if (!livePids.Contains(pid))
                    _processes.Remove(pid);
            }

            // Learn volumes changed elsewhere (e.g. the Windows Volume Mixer) from sessions we already knew about,
            // then give new sessions the volume their app had last time.
            var observed = new Dictionary<string, Level>();
            var fromDefaultDevice = new HashSet<string>();
            foreach (var session in found)
            {
                if (!previous.Contains(session.Control) || fromDefaultDevice.Contains(session.AppKey))
                    continue;
                bool onDefault = session.DeviceId == _defaultId;
                if ((onDefault || !observed.ContainsKey(session.AppKey)) && TryReadLevel(session, out Level level))
                {
                    observed[session.AppKey] = level;
                    if (onDefault)
                        fromDefaultDevice.Add(session.AppKey);
                }
            }
            bool rememberedChanged = false;
            foreach (var app in observed)
                rememberedChanged |= Remember(app.Key, app.Value);
            foreach (var session in found)
            {
                if (!previous.Contains(session.Control) && _remembered.TryGetValue(session.AppKey, out Level level))
                    Guarded(() => ApplyLevel(session, session.AppKey, level));
                if (session.IsDiscordSounds)
                    Guarded(() => ApplyLevel(session, DiscordSoundsKey, DiscordSoundsLevel));
            }

            ReleaseSessions(_sessions);
            _sessions = found;
            if (rememberedChanged)
                PublishRemembered();
        }

        void SyncDevices()
        {
            if (_enumerator.EnumAudioEndpoints(CoreAudio.ERender, CoreAudio.DeviceStateActive, out IMMDeviceCollection collection) != 0)
                return;
            var present = new HashSet<string>();
            try
            {
                collection.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out IMMDevice device) != 0)
                        continue;
                    try
                    {
                        if (device.GetId(out string id) != 0)
                            continue;
                        present.Add(id);
                        if (_devices.ContainsKey(id))
                            continue;
                        var sessions = CoreAudio.Activate<IAudioSessionManager2>(device);
                        if (sessions == null)
                            continue;
                        // The session manager only sends notifications after the session list has been enumerated once.
                        if (sessions.GetSessionEnumerator(out IAudioSessionEnumerator initial) == 0)
                            CoreAudio.Release(initial);
                        sessions.RegisterSessionNotification(_sessionNotifier);
                        var volume = CoreAudio.Activate<IAudioEndpointVolume>(device);
                        volume?.RegisterControlChangeNotify(_volumeNotifier);
                        _devices[id] = new Device
                        {
                            Name = CoreAudio.GetFriendlyName(device) ?? "Speakers",
                            Sessions = sessions,
                            Volume = volume,
                            Meter = CoreAudio.Activate<IAudioMeterInformation>(device),
                        };
                    }
                    finally
                    {
                        CoreAudio.Release(device);
                    }
                }
            }
            finally
            {
                CoreAudio.Release(collection);
            }

            foreach (string id in new List<string>(_devices.Keys))
            {
                if (!present.Contains(id))
                    DropDevice(id);
            }
        }

        void DropDevice(string id)
        {
            Device device = _devices[id];
            _devices.Remove(id);
            try { device.Sessions.UnregisterSessionNotification(_sessionNotifier); }
            catch (COMException) { }
            if (device.Volume != null)
            {
                try { device.Volume.UnregisterControlChangeNotify(_volumeNotifier); }
                catch (COMException) { }
            }
            CoreAudio.Release(device.Sessions);
            CoreAudio.Release(device.Volume);
            CoreAudio.Release(device.Meter);
        }

        void SyncDefaultDevice()
        {
            _defaultId = null;
            if (_enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out IMMDevice device) != 0)
                return;
            try
            {
                if (device.GetId(out string id) == 0)
                    _defaultId = id;
            }
            finally
            {
                CoreAudio.Release(device);
            }
        }

        void CollectSessions(string deviceId, IAudioSessionManager2 manager, List<Session> found, HashSet<uint> livePids)
        {
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator list) != 0)
                throw new COMException("Session list unavailable");
            try
            {
                list.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (list.GetSession(i, out IAudioSessionControl2 control) != 0)
                        continue;
                    Session session = Describe(deviceId, control, livePids);
                    if (session != null)
                        found.Add(session);
                    else
                        CoreAudio.Release(control);
                }
            }
            finally
            {
                CoreAudio.Release(list);
            }
        }

        Session Describe(string deviceId, IAudioSessionControl2 control, HashSet<uint> livePids)
        {
            if (control.GetState(out int state) != 0 || state == CoreAudio.SessionStateExpired)
                return null;
            var session = new Session { Control = control, DeviceId = deviceId };
            if (control.IsSystemSoundsSession() == 0)
            {
                session.Kind = RowKind.SystemSounds;
                session.AppKey = SystemSoundsKey;
                return session;
            }
            if (control.GetProcessId(out uint pid) != 0 || pid == 0)
                return null;
            livePids.Add(pid);
            if (!_processes.TryGetValue(pid, out ProcessDetails details))
            {
                details = ProcessInfo.Describe(pid);
                _processes[pid] = details;
            }
            if (details == null)
                return null;
            session.Kind = RowKind.App;
            session.AppKey = details.ExeName;
            session.ImagePath = details.ImagePath;
            session.IsDiscordSounds = details.IsDiscordAudioService;
            if (control.GetDisplayName(out IntPtr name) == 0)
            {
                string displayName = CoreAudio.TakeString(name);
                if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@"))
                    session.DisplayName = displayName;
            }
            return session;
        }

        static bool TryReadLevel(Session session, out Level level)
        {
            level = Level.Default;
            var simple = (ISimpleAudioVolume)session.Control;
            if (simple.GetMasterVolume(out float volume) != 0 || simple.GetMute(out bool muted) != 0)
                return false;
            level = new Level((int)Math.Round(volume * 100), muted);
            return true;
        }

        // ---- Snapshots ----

        MixerSnapshot BuildSnapshot(bool withPeaks)
        {
            var snapshot = new MixerSnapshot();
            foreach (var pair in _devices)
            {
                Device device = pair.Value;
                if (device.Volume == null
                    || device.Volume.GetMasterVolumeLevelScalar(out float deviceVolume) != 0
                    || device.Volume.GetMute(out bool deviceMuted) != 0)
                    continue;
                var state = new DeviceState
                {
                    Id = pair.Key,
                    Name = device.Name,
                    Level = new Level((int)Math.Round(deviceVolume * 100), deviceMuted),
                    IsDefault = pair.Key == _defaultId,
                };
                if (withPeaks && device.Meter != null && device.Meter.GetPeakValue(out float devicePeak) == 0)
                    state.Peak = devicePeak * state.Level.Gain;
                snapshot.Devices.Add(state);
            }
            snapshot.Devices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

            var apps = new Dictionary<string, AppState>();
            var fromDefaultDevice = new HashSet<string>();
            AppState discordSounds = null;
            string discordImage = null;
            foreach (var session in _sessions)
            {
                if (!TryReadLevel(session, out Level level))
                    continue;
                if (!apps.TryGetValue(session.AppKey, out AppState app))
                {
                    app = new AppState { Key = session.AppKey, Kind = session.Kind, ImagePath = session.ImagePath, Name = NameFor(session), Level = level };
                    apps.Add(session.AppKey, app);
                }
                if (session.DeviceId == _defaultId && fromDefaultDevice.Add(session.AppKey))
                    app.Level = level;
                if (session.DisplayName != null && app.Kind == RowKind.App)
                    app.Name = session.DisplayName;
                if (ProcessInfo.IsDiscordExe(session.AppKey))
                    discordImage = session.ImagePath;

                float sessionPeak = 0f;
                if (withPeaks && ((IAudioMeterInformation)session.Control).GetPeakValue(out float peak) == 0)
                    sessionPeak = peak * level.Gain;
                app.Peak = Math.Max(app.Peak, sessionPeak);
                if (session.IsDiscordSounds)
                {
                    discordSounds = discordSounds ?? NewDiscordSoundsRow(session.ImagePath);
                    discordSounds.Peak = Math.Max(discordSounds.Peak, sessionPeak * DiscordSoundsLevel.Gain);
                }
            }
            // Show the sound-effects row whenever Discord is running, even before it has played its first sound.
            if (discordSounds == null && discordImage != null)
                discordSounds = NewDiscordSoundsRow(discordImage);

            var ordered = new List<AppState>(apps.Values);
            ordered.Sort((a, b) =>
            {
                int byKind = (a.Kind == RowKind.SystemSounds).CompareTo(b.Kind == RowKind.SystemSounds);
                return byKind != 0 ? byKind : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            });
            foreach (var app in ordered)
            {
                snapshot.Apps.Add(app);
                if (discordSounds != null && ProcessInfo.IsDiscordExe(app.Key))
                {
                    snapshot.Apps.Add(discordSounds);
                    discordSounds = null;
                }
            }
            return snapshot;
        }

        AppState NewDiscordSoundsRow(string imagePath) => new AppState
        {
            Key = DiscordSoundsKey,
            Kind = RowKind.DiscordSounds,
            Name = "Discord sound effects",
            ImagePath = imagePath,
            Level = DiscordSoundsLevel,
        };

        string NameFor(Session session)
        {
            if (session.Kind == RowKind.SystemSounds)
                return "System sounds";
            if (!_appNames.TryGetValue(session.ImagePath, out string name))
            {
                name = ProcessInfo.GetDisplayName(session.ImagePath);
                _appNames[session.ImagePath] = name;
            }
            return name;
        }

        void Publish(MixerSnapshot snapshot, bool always)
        {
            string signature = Signature(snapshot);
            if (!always && signature == _lastSignature)
                return;
            _lastSignature = signature;
            _ui.Post(_ => _onSnapshot(snapshot), null);
        }

        static string Signature(MixerSnapshot snapshot)
        {
            var sb = new StringBuilder();
            foreach (var device in snapshot.Devices)
                sb.Append('|').Append(device.Id).Append(device.Name).Append(device.Level.Volume).Append(device.Level.Muted).Append(device.IsDefault);
            foreach (var app in snapshot.Apps)
                sb.Append('|').Append(app.Key).Append(app.Name).Append(app.Level.Volume).Append(app.Level.Muted);
            return sb.ToString();
        }

        void PublishRemembered()
        {
            var copy = new Dictionary<string, Level>(_remembered, StringComparer.OrdinalIgnoreCase);
            _ui.Post(_ => _onRememberedChanged(copy), null);
        }

        // ---- Teardown ----

        void Shutdown()
        {
            // Hand Discord's sound effects their normal volume back; the per-channel control isn't visible in the Volume Mixer.
            foreach (var session in _sessions)
            {
                if (session.IsDiscordSounds)
                    Guarded(() => ApplyLevel(session, DiscordSoundsKey, Level.Default));
            }
            ReleaseSessions(_sessions);
            foreach (string id in new List<string>(_devices.Keys))
                DropDevice(id);
            CoreAudio.Release(_enumerator);
        }

        static void ReleaseSessions(List<Session> sessions)
        {
            foreach (var session in sessions)
                CoreAudio.Release(session.Control);
            sessions.Clear();
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        sealed class SessionNotifier : IAudioSessionNotification
        {
            readonly Action _onCreated;
            public SessionNotifier(Action onCreated) => _onCreated = onCreated;

            public int OnSessionCreated(IntPtr newSession)
            {
                _onCreated();
                return 0;
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        sealed class VolumeNotifier : IAudioEndpointVolumeCallback
        {
            readonly Action _onChanged;
            public VolumeNotifier(Action onChanged) => _onChanged = onChanged;

            public int OnNotify(IntPtr notifyData)
            {
                _onChanged();
                return 0;
            }
        }
    }
}
