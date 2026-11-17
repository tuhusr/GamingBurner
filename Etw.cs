using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamingBurner;

/// <summary>
/// Real-time ETW consumer for Present() calls. Every Present a process makes is
/// timestamped by Windows with the QueryPerformanceCounter clock (100 ns ticks on this
/// PC) at the moment of the call, so frame times are exact to the clock resolution and
/// do not depend on when this app gets scheduled. Nothing is injected into the game.
/// </summary>
public sealed unsafe class EtwPresentSource : IDisposable
{
    // Separate names so a normal copy and an administrator copy never stop each other's capture.
    static readonly string SessionName = HwMonitor.IsElevated ? "GamingBurnerPresentSession-Admin" : "GamingBurnerPresentSession";
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const ushort DxgiPresentStart = 42;
    const ushort D3D9PresentStart = 1;
    const uint DxgiPresentTest = 0x1; // DXGI_PRESENT_TEST: not a real frame

    const int PropsSize = 120;
    const int NameBytes = 1024;
    const uint WnodeFlagTracedGuid = 0x00020000;
    const uint RealTimeMode = 0x100;
    const uint UseMsFlushTimer = 0x10;
    const uint ProcessRealTime = 0x100;
    const uint ProcessEventRecord = 0x10000000;
    const uint ProcessRawTimestamp = 0x1000;
    const uint ControlQuery = 0, ControlStop = 1;
    const int ErrorAlreadyExists = 183;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")]
    static extern int EnableTraceEx2(ulong handle, in Guid provider, uint code, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll")]
    static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")]
    static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")]
    static extern int CloseTrace(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void RecordCallback(byte* record);

    readonly FrameTracker _tracker;
    readonly RecordCallback _callback; // kept alive for the life of the session
    ulong _session;
    ulong _trace = ulong.MaxValue;
    IntPtr _logfile, _loggerName;
    Thread _thread;
    volatile bool _disposed;

    public string Error { get; private set; }
    public bool AccessDenied { get; private set; }
    public bool Running => _thread != null && _thread.IsAlive;

    public EtwPresentSource(FrameTracker tracker)
    {
        _tracker = tracker;
        _callback = OnRecord;
    }

    static IntPtr NewProps()
    {
        int size = PropsSize + NameBytes * 2;
        IntPtr p = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)p, size).Clear();
        byte* b = (byte*)p;
        *(uint*)(b + 0) = (uint)size;            // Wnode.BufferSize
        *(uint*)(b + 40) = 1;                    // Wnode.ClientContext = QPC clock
        *(uint*)(b + 44) = WnodeFlagTracedGuid;  // Wnode.Flags
        *(uint*)(b + 48) = 64;                   // BufferSize (KB)
        *(uint*)(b + 52) = 64;                   // MinimumBuffers
        *(uint*)(b + 56) = 256;                  // MaximumBuffers
        *(uint*)(b + 64) = RealTimeMode | UseMsFlushTimer;
        *(uint*)(b + 68) = 200;                  // FlushTimer (ms): delivery delay only, timestamps are taken at the call
        *(uint*)(b + 112) = (uint)(PropsSize + NameBytes); // LogFileNameOffset
        *(uint*)(b + 116) = PropsSize;           // LoggerNameOffset
        return p;
    }

    public bool Start()
    {
        IntPtr props = NewProps();
        try
        {
            int rc = StartTraceW(out _session, SessionName, props);
            if (rc == ErrorAlreadyExists)
            {
                // A previous run left the session behind; stop it and start clean.
                IntPtr stop = NewProps();
                ControlTraceW(0, SessionName, stop, ControlStop);
                Marshal.FreeHGlobal(stop);
                Marshal.FreeHGlobal(props);
                props = NewProps();
                rc = StartTraceW(out _session, SessionName, props);
            }
            if (rc == 5)
            {
                // Windows only lets administrators (and members of "Performance Log Users") watch other programs' frames.
                AccessDenied = true;
                Error = "Windows needs administrator rights to measure frames on this PC";
                return false;
            }
            if (rc != 0) { Error = $"StartTrace failed (Windows error {rc})"; return false; }
        }
        finally { Marshal.FreeHGlobal(props); }

        int e1 = EnableOnly(Dxgi, DxgiPresentStart);
        int e2 = EnableOnly(D3D9, D3D9PresentStart);
        if (e1 != 0 && e2 != 0) { Error = $"EnableTrace failed (Windows errors {e1}, {e2})"; Stop(); return false; }

        const int logfileSize = 448; // sizeof(EVENT_TRACE_LOGFILEW) on x64
        _logfile = Marshal.AllocHGlobal(logfileSize);
        new Span<byte>((void*)_logfile, logfileSize).Clear();
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        byte* lf = (byte*)_logfile;
        *(IntPtr*)(lf + 8) = _loggerName;
        *(uint*)(lf + 28) = ProcessRealTime | ProcessEventRecord | ProcessRawTimestamp;
        *(IntPtr*)(lf + 424) = Marshal.GetFunctionPointerForDelegate(_callback);

        _trace = OpenTraceW(_logfile);
        if (_trace == ulong.MaxValue)
        {
            Error = $"OpenTrace failed (Windows error {Marshal.GetLastWin32Error()})";
            Stop();
            return false;
        }

        _thread = new Thread(() => ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero))
        {
            IsBackground = true,
            Name = "ETW present consumer"
        };
        _thread.Start();
        return true;
    }

    /// <summary>
    /// Turns a provider on for one event only (the Present call). Windows then skips writing every
    /// other graphics event in every process, so the game does less tracing work and this app
    /// receives only what it needs. Timing of the Present events themselves is unchanged.
    /// </summary>
    int EnableOnly(in Guid provider, ushort eventId)
    {
        byte* ids = stackalloc byte[6];          // EVENT_FILTER_EVENT_ID
        ids[0] = 1; ids[1] = 0;                  // FilterIn = true
        *(ushort*)(ids + 2) = 1;                 // Count
        *(ushort*)(ids + 4) = eventId;
        byte* desc = stackalloc byte[16];        // EVENT_FILTER_DESCRIPTOR
        *(ulong*)desc = (ulong)ids;
        *(uint*)(desc + 8) = 6;
        *(uint*)(desc + 12) = 0x80000200;        // EVENT_FILTER_TYPE_EVENT_ID
        byte* parms = stackalloc byte[48];       // ENABLE_TRACE_PARAMETERS (version 2)
        new Span<byte>(parms, 48).Clear();
        *(uint*)parms = 2;
        *(IntPtr*)(parms + 32) = (IntPtr)desc;
        *(uint*)(parms + 40) = 1;
        int rc = EnableTraceEx2(_session, provider, 1, 5, 0, 0, 0, (IntPtr)parms);
        Filtered = rc == 0;
        // Older systems may refuse the filter: fall back to receiving everything and filtering here.
        return rc == 0 ? 0 : EnableTraceEx2(_session, provider, 1, 5, 0, 0, 0, IntPtr.Zero);
    }

    public bool Filtered { get; private set; }

    void OnRecord(byte* r)
    {
        // EVENT_RECORD layout: ProcessId @12, TimeStamp @16, ProviderId @24, Id @40, UserData @96
        ushort id = *(ushort*)(r + 40);
        Guid* provider = (Guid*)(r + 24);
        if (id == DxgiPresentStart && *provider == Dxgi)
        {
            ushort len = *(ushort*)(r + 86);
            byte* data = *(byte**)(r + 96);
            // Payload: swap chain pointer (8), Flags (4), SyncInterval (4)
            if (len >= 12 && data != null && (*(uint*)(data + 8) & DxgiPresentTest) != 0) return;
            _tracker.OnPresent(*(uint*)(r + 12), *(long*)(r + 16));
        }
        else if (id == D3D9PresentStart && *provider == D3D9)
        {
            _tracker.OnPresent(*(uint*)(r + 12), *(long*)(r + 16));
        }
    }

    /// <summary>Events Windows dropped before delivering them (0 means every frame was counted).</summary>
    public long QueryLostEvents()
    {
        if (_session == 0) return -1;
        IntPtr p = NewProps();
        try
        {
            if (ControlTraceW(_session, null, p, ControlQuery) != 0) return -1;
            byte* b = (byte*)p;
            return *(uint*)(b + 88) + (long)*(uint*)(b + 100); // EventsLost + RealTimeBuffersLost
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    void Stop()
    {
        if (_trace != ulong.MaxValue) { CloseTrace(_trace); _trace = ulong.MaxValue; }
        if (_session != 0)
        {
            IntPtr p = NewProps();
            ControlTraceW(_session, null, p, ControlStop);
            Marshal.FreeHGlobal(p);
            _session = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _thread?.Join(1500);
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
    }
}

public sealed class ProcFrames
{
    public const int Capacity = 65536; // > 2.5 minutes at 400 fps
    public uint Pid;
    public string Name = "";
    public long LastTs;
    public long TotalFrames;
    public readonly long[] Ts = new long[Capacity];   // QPC tick at which each frame ended
    public readonly float[] Ms = new float[Capacity]; // frame time of that frame
    public int Head, Len;
}

public struct FrameStats
{
    public bool Valid;
    public uint Pid;
    public string Process;
    public double Fps;          // frames / elapsed time over the last second
    public double FrameMs;      // average frame time over the last second
    public double MaxMs;        // worst frame in the last second
    public double Low1, Low01;  // 1% and 0.1% low fps over the long window
    public double AvgLong;      // average fps over the long window
    public int FramesLong;
    public long TotalFrames;
}

public sealed class FrameTracker
{
    public static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;
    const double GapMs = 5000; // a pause this long is a load screen or alt-tab, not a frame

    readonly object _lock = new();
    readonly Dictionary<uint, ProcFrames> _procs = new();
    readonly uint _selfPid = (uint)Environment.ProcessId;

    // Desktop apps that also present frames but are never the game being measured.
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "brave", "chrome", "msedge", "firefox", "opera", "msedgewebview2", "explorer", "dwm",
        "Discord", "steamwebhelper", "steam", "Code", "devenv", "obs64", "NVIDIA Overlay", "NVIDIA app",
        "slack", "Spotify", "WindowsTerminal", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "SystemSettings", "ApplicationFrameHost", "iCUE", "lghub", "qbittorrent",
        "MSIAfterburner", "RTSS", "Taskmgr", "vlc", "mpc-hc64", "PhotosApp", "Photos", "gameoverlayui64",
        "ProtonVPN.Client", "MarvelRivals_Launcher", "Widgets", "LockApp", "CrossDeviceResume"
    };

    // Benchmark recording of the target process
    List<float> _bench;
    uint _benchPid;

    public void OnPresent(uint pid, long ts)
    {
        if (pid == _selfPid) return;
        lock (_lock)
        {
            if (!_procs.TryGetValue(pid, out var p))
            {
                p = new ProcFrames { Pid = pid };
                _procs[pid] = p;
            }
            if (p.LastTs != 0)
            {
                double ms = (ts - p.LastTs) / TicksPerMs;
                if (ms > 0 && ms < GapMs)
                {
                    p.Ts[p.Head] = ts;
                    p.Ms[p.Head] = (float)ms;
                    p.Head = (p.Head + 1) % ProcFrames.Capacity;
                    if (p.Len < ProcFrames.Capacity) p.Len++;
                    p.TotalFrames++;
                    if (_bench != null && pid == _benchPid) _bench.Add((float)ms);
                }
            }
            p.LastTs = ts;
        }
    }

    static string NameOf(uint pid)
    {
        try { using var pr = Process.GetProcessById((int)pid); return pr.ProcessName; }
        catch { return "pid " + pid; }
    }

    /// <summary>Picks the process to report: the pinned one, else the one presenting most in the last second.</summary>
    public uint PickTarget(uint pinned = 0)
    {
        long now = Stopwatch.GetTimestamp();
        long oneSec = Stopwatch.Frequency;
        lock (_lock)
        {
            if (pinned != 0 && _procs.TryGetValue(pinned, out var pp) && now - pp.LastTs < 3 * oneSec) return pinned;
            uint best = 0; int bestCount = 9;
            List<uint> dead = null;
            foreach (var p in _procs.Values)
            {
                if (now - p.LastTs > 30 * oneSec) { (dead ??= new()).Add(p.Pid); continue; }
                if (p.Name.Length == 0) p.Name = NameOf(p.Pid);
                if (NotGames.Contains(p.Name)) continue;
                int n = CountSince(p, p.LastTs - oneSec);
                if (now - p.LastTs < 2 * oneSec && n > bestCount) { bestCount = n; best = p.Pid; }
            }
            if (dead != null) foreach (var d in dead) _procs.Remove(d);
            return best;
        }
    }

    static int CountSince(ProcFrames p, long since)
    {
        int n = 0;
        for (int i = 1; i <= p.Len; i++)
        {
            int idx = (p.Head - i + ProcFrames.Capacity) % ProcFrames.Capacity;
            if (p.Ts[idx] < since) break;
            n++;
        }
        return n;
    }

    // The 30-second figures (1% low, 0.1% low, average) need a sort, so they are refreshed
    // once a second and reused in between. Every frame is still included when they are computed.
    uint _longPid; long _longStamp; Summary _longSummary; int _longFrames;

    public FrameStats Snapshot(uint pid, double longWindowSec = 30)
    {
        var s = new FrameStats { Pid = pid };
        float[] window = null;
        lock (_lock)
        {
            if (pid == 0 || !_procs.TryGetValue(pid, out var p) || p.Len == 0) return s;
            if (p.Name.Length == 0) p.Name = NameOf(pid);
            s.Process = p.Name;
            s.TotalFrames = p.TotalFrames;

            long newest = p.LastTs;
            long shortSince = newest - Stopwatch.Frequency;
            double sumShort = 0, maxShort = 0; int nShort = 0;
            for (int i = 1; i <= p.Len; i++)
            {
                int idx = (p.Head - i + ProcFrames.Capacity) % ProcFrames.Capacity;
                if (p.Ts[idx] < shortSince) break;
                float ms = p.Ms[idx];
                sumShort += ms; nShort++; if (ms > maxShort) maxShort = ms;
            }
            if (nShort > 0)
            {
                s.FrameMs = sumShort / nShort;
                s.Fps = 1000.0 / s.FrameMs;
                s.MaxMs = maxShort;
            }

            long now = Stopwatch.GetTimestamp();
            if (pid != _longPid || now - _longStamp >= Stopwatch.Frequency)
            {
                long longSince = newest - (long)(longWindowSec * Stopwatch.Frequency);
                int nLong = CountSince(p, longSince);
                window = new float[nLong];
                for (int i = 1; i <= nLong; i++)
                    window[i - 1] = p.Ms[(p.Head - i + ProcFrames.Capacity) % ProcFrames.Capacity];
                _longPid = pid; _longStamp = now;
            }
        }
        if (window != null)
        {
            _longSummary = Summarize(window);
            _longFrames = window.Length;
        }
        if (_longFrames == 0) return s;
        s.FramesLong = _longFrames;
        s.AvgLong = _longSummary.AvgFps; s.Low1 = _longSummary.Low1; s.Low01 = _longSummary.Low01;
        s.Valid = true;
        return s;
    }

    /// <summary>Copies the newest frame times (oldest first) for graphing.</summary>
    public float[] RecentFrameTimes(uint pid, int count)
    {
        lock (_lock)
        {
            if (!_procs.TryGetValue(pid, out var p)) return Array.Empty<float>();
            int n = Math.Min(count, p.Len);
            var a = new float[n];
            for (int i = 0; i < n; i++)
                a[i] = p.Ms[(p.Head - n + i + ProcFrames.Capacity) % ProcFrames.Capacity];
            return a;
        }
    }

    public void StartBenchmark(uint pid)
    {
        lock (_lock) { _benchPid = pid; _bench = new List<float>(200000); }
    }

    public float[] StopBenchmark()
    {
        lock (_lock)
        {
            var a = _bench?.ToArray() ?? Array.Empty<float>();
            _bench = null;
            return a;
        }
    }

    public bool Benchmarking { get { lock (_lock) return _bench != null; } }
    public int BenchmarkFrames { get { lock (_lock) return _bench?.Count ?? 0; } }

    public struct Summary
    {
        public int Frames; public double Seconds;
        public double AvgFps, Low1, Low01, MinFps, MaxFps;
        public double AvgMs, P50Ms, P99Ms, P999Ms, MaxMs, MinMs, StdevMs;
    }

    /// <summary>
    /// Average fps is frames divided by elapsed time. The 1% low is the frame rate of the
    /// 99th-percentile frame time (99% of frames were at least this fast); 0.1% low likewise.
    /// </summary>
    public static Summary Summarize(float[] ms)
    {
        var r = new Summary { Frames = ms.Length };
        if (ms.Length == 0) return r;
        var sorted = (float[])ms.Clone();
        Array.Sort(sorted);
        double sum = 0;
        foreach (var v in ms) sum += v;
        double mean = sum / ms.Length, var = 0;
        foreach (var v in ms) var += (v - mean) * (v - mean);
        r.Seconds = sum / 1000.0;
        r.AvgMs = mean;
        r.AvgFps = 1000.0 / mean;
        r.StdevMs = Math.Sqrt(var / ms.Length);
        r.MinMs = sorted[0];
        r.MaxMs = sorted[^1];
        r.P50Ms = Percentile(sorted, 0.50);
        r.P99Ms = Percentile(sorted, 0.99);
        r.P999Ms = Percentile(sorted, 0.999);
        r.Low1 = 1000.0 / r.P99Ms;
        r.Low01 = 1000.0 / r.P999Ms;
        r.MinFps = 1000.0 / r.MaxMs;
        r.MaxFps = 1000.0 / r.MinMs;
        return r;
    }

    static double Percentile(float[] sorted, double q)
    {
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
