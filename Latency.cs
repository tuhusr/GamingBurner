using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GamingBurner;

/// <summary>What the latency engine knows about the game right now.</summary>
public struct LatencyStats
{
    public bool Valid;
    public double PcLatencyMs;      // input received by the PC to frame on screen, when the game reports it (NaN otherwise)
    public double FrameToScreenMs;  // start of a frame's work to that frame appearing on screen
    public double ClickMs;          // last mouse click to the frame that showed it (NaN until a click is seen)
    public double ClickAgeSec;      // how long ago that click was
    public double GpuBusyMs, GpuWaitMs, CpuBusyMs, CpuWaitMs;
    public double GpuBusyShare;     // share of each frame the graphics card spent working (0..1)
    public double ShownFps;         // frames that actually reached the screen per second
    public double DroppedPct;       // frames rendered but never shown, last 30 s
    public string PresentMode;
    public bool Tearing;
    public string Limit;            // "Graphics card", "Processor" or "Balanced"
}

/// <summary>One frame as reported by the engine. NaN means "not available for this frame".</summary>
public struct LatencyFrame
{
    public long Qpc;
    public float FrameMs, DisplayGapMs, UntilShownMs, FrameToScreenMs, PcLatencyMs, ClickMs, AllInputMs;
    public float CpuBusyMs, CpuWaitMs, GpuBusyMs, GpuWaitMs;
}

/// <summary>
/// Latency and display timing. Runs Intel's PresentMon (open source, MIT licence) as a helper
/// for the one game being measured and reads its per-frame output. PresentMon follows every
/// frame from the game through Windows to the moment the display shows it, which is what
/// makes latency, dropped frames and "who is the limit" measurable. Nothing is injected
/// into the game.
/// </summary>
public sealed class LatencyEngine : IDisposable
{
    const int Capacity = 32768;
    static readonly string SessionName = HwMonitor.IsElevated ? "GamingBurnerLatency-Admin" : "GamingBurnerLatency";

    readonly LatencyFrame[] _ring = new LatencyFrame[Capacity];
    readonly object _lock = new();
    int _head, _len;
    Process _proc;
    IntPtr _job;
    Thread _reader;
    uint _pid;
    string _presentMode = "";
    bool _tearing;
    float _lastClickMs = float.NaN; long _lastClickQpc;
    List<LatencyFrame> _recording;

    public string EnginePath { get; }
    public bool Available => EnginePath != null;
    public string Error { get; private set; }
    public bool Running => _proc != null && !_proc.HasExited;

    public LatencyEngine()
    {
        try
        {
            string dir = AppPaths.EngineDir;
            EnginePath = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "PresentMon*.exe").OrderByDescending(f => f).FirstOrDefault()
                : null;
            if (EnginePath == null) Error = "the latency engine (PresentMon) is missing from " + dir;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>Points the engine at a game (0 stops it). Safe to call every tick; it only acts on a change.</summary>
    public void Track(uint pid)
    {
        if (!Available) return;
        if (pid == _pid && (pid == 0 || Running)) return;
        Stop();
        _pid = pid;
        if (pid == 0) return;
        try
        {
            var psi = new ProcessStartInfo(EnginePath,
                $"--process_id {pid} --output_stdout --no_console_stats --qpc_time --session_name {SessionName} --stop_existing_session")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            _proc = Process.Start(psi);
            try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            AttachToJob(_proc);
            _proc.ErrorDataReceived += (_, _) => { };
            _proc.BeginErrorReadLine();
            var p = _proc;
            _reader = new Thread(() => Read(p)) { IsBackground = true, Name = "Latency engine reader", Priority = ThreadPriority.BelowNormal };
            _reader.Start();
            Error = null;
        }
        catch (Exception ex) { Error = "the latency engine did not start: " + ex.Message; _proc = null; }
    }

    void Read(Process p)
    {
        try
        {
            int[] col = null;
            string[] names =
            {
                "TimeInQPC", "MsBetweenPresents", "MsBetweenDisplayChange", "MsUntilDisplayed", "MsPCLatency", "CPUStartQPC",
                "MsCPUBusy", "MsCPUWait", "MsGPUBusy", "MsGPUWait", "MsAllInputToPhotonLatency", "MsClickToPhotonLatency",
                "PresentMode", "AllowsTearing"
            };
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (col == null)
                {
                    if (!line.StartsWith("Application")) continue;
                    col = names.Select(n => Array.IndexOf(parts, n)).ToArray();
                    continue;
                }
                float F(int i) => col[i] >= 0 && col[i] < parts.Length &&
                    float.TryParse(parts[col[i]], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
                long L(int i) => col[i] >= 0 && col[i] < parts.Length && long.TryParse(parts[col[i]], out var v) ? v : 0;

                var fr = new LatencyFrame
                {
                    Qpc = L(0), FrameMs = F(1), DisplayGapMs = F(2), UntilShownMs = F(3), PcLatencyMs = F(4),
                    CpuBusyMs = F(6), CpuWaitMs = F(7), GpuBusyMs = F(8), GpuWaitMs = F(9), AllInputMs = F(10), ClickMs = F(11)
                };
                // A frame that was never shown reports no display time (or zero).
                if (fr.UntilShownMs <= 0) fr.UntilShownMs = float.NaN;
                long cpuStart = L(5);
                fr.FrameToScreenMs = float.IsNaN(fr.UntilShownMs) || cpuStart == 0 || fr.Qpc == 0
                    ? float.NaN
                    : (float)((fr.Qpc - cpuStart) * 1000.0 / Stopwatch.Frequency) + fr.UntilShownMs;

                lock (_lock)
                {
                    _ring[_head] = fr;
                    _head = (_head + 1) % Capacity;
                    if (_len < Capacity) _len++;
                    if (col[12] >= 0 && col[12] < parts.Length) _presentMode = parts[col[12]];
                    if (col[13] >= 0 && col[13] < parts.Length) _tearing = parts[col[13]] == "1";
                    if (!float.IsNaN(fr.ClickMs)) { _lastClickMs = fr.ClickMs; _lastClickQpc = fr.Qpc; }
                    _recording?.Add(fr);
                }
            }
        }
        catch { /* the engine was stopped */ }
    }

    public LatencyStats Snapshot()
    {
        var s = new LatencyStats { PcLatencyMs = double.NaN, FrameToScreenMs = double.NaN, ClickMs = double.NaN, PresentMode = "" };
        lock (_lock)
        {
            if (_len == 0) return s;
            long newest = _ring[(_head - 1 + Capacity) % Capacity].Qpc;
            if (Stopwatch.GetTimestamp() - newest > 3 * Stopwatch.Frequency) return s; // stale: the game stopped drawing
            long oneSec = newest - Stopwatch.Frequency, thirtySec = newest - 30 * Stopwatch.Frequency;
            double pc = 0, fts = 0, gb = 0, gw = 0, cb = 0, cw = 0, ft = 0, gap = 0;
            int nPc = 0, nFts = 0, nGpu = 0, nCpu = 0, nFt = 0, nGap = 0, total30 = 0, dropped30 = 0;
            for (int i = 1; i <= _len; i++)
            {
                ref var f = ref _ring[(_head - i + Capacity) % Capacity];
                if (f.Qpc < thirtySec) break;
                total30++;
                if (float.IsNaN(f.UntilShownMs)) dropped30++;
                if (f.Qpc < oneSec) continue;
                if (!float.IsNaN(f.PcLatencyMs)) { pc += f.PcLatencyMs; nPc++; }
                if (!float.IsNaN(f.FrameToScreenMs)) { fts += f.FrameToScreenMs; nFts++; }
                if (!float.IsNaN(f.GpuBusyMs)) { gb += f.GpuBusyMs; gw += float.IsNaN(f.GpuWaitMs) ? 0 : f.GpuWaitMs; nGpu++; }
                if (!float.IsNaN(f.CpuBusyMs)) { cb += f.CpuBusyMs; cw += float.IsNaN(f.CpuWaitMs) ? 0 : f.CpuWaitMs; nCpu++; }
                if (!float.IsNaN(f.FrameMs)) { ft += f.FrameMs; nFt++; }
                if (!float.IsNaN(f.DisplayGapMs) && f.DisplayGapMs > 0) { gap += f.DisplayGapMs; nGap++; }
            }
            if (nFt == 0) return s;
            s.Valid = true;
            if (nPc > 0) s.PcLatencyMs = pc / nPc;
            if (nFts > 0) s.FrameToScreenMs = fts / nFts;
            if (nGpu > 0) { s.GpuBusyMs = gb / nGpu; s.GpuWaitMs = gw / nGpu; }
            if (nCpu > 0) { s.CpuBusyMs = cb / nCpu; s.CpuWaitMs = cw / nCpu; }
            double frame = ft / nFt;
            s.GpuBusyShare = nGpu > 0 && frame > 0 ? Math.Clamp(s.GpuBusyMs / frame, 0, 1) : double.NaN;
            s.Limit = double.IsNaN(s.GpuBusyShare) ? "" : s.GpuBusyShare >= 0.92 ? "Graphics card" : s.GpuBusyShare <= 0.80 ? "Processor" : "Balanced";
            if (nGap > 0) s.ShownFps = 1000.0 / (gap / nGap);
            s.DroppedPct = total30 > 0 ? 100.0 * dropped30 / total30 : 0;
            s.PresentMode = _presentMode;
            s.Tearing = _tearing;
            if (!float.IsNaN(_lastClickMs))
            {
                s.ClickMs = _lastClickMs;
                s.ClickAgeSec = (Stopwatch.GetTimestamp() - _lastClickQpc) / (double)Stopwatch.Frequency;
            }
        }
        return s;
    }

    public void StartRecording() { lock (_lock) _recording = new List<LatencyFrame>(100000); }

    public LatencyFrame[] StopRecording()
    {
        lock (_lock)
        {
            var a = _recording?.ToArray() ?? Array.Empty<LatencyFrame>();
            _recording = null;
            return a;
        }
    }

    public string PresentModeNow { get { lock (_lock) return _presentMode; } }

    void Stop()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(); } catch { }
        try { p.Dispose(); } catch { }
        lock (_lock) { _len = 0; _head = 0; _lastClickMs = float.NaN; }
        // Killing the helper leaves its Windows trace session behind; ask a fresh copy to remove it.
        try
        {
            using var cleanup = Process.Start(new ProcessStartInfo(EnginePath, $"--session_name {SessionName} --terminate_existing_session")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            cleanup?.WaitForExit(3000);
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    // ----- Tie the helper's lifetime to this app, so it can never be left running on its own -----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attrs, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    unsafe void AttachToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObjectW(IntPtr.Zero, null);
                const int size = 144;                    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on 64-bit
                byte* info = stackalloc byte[size];
                new Span<byte>(info, size).Clear();
                *(uint*)(info + 16) = 0x2000;            // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(_job, 9, (IntPtr)info, size);
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { }
    }
}

/// <summary>Connection quality from one small ping a second: round trip time, jitter and loss.</summary>
public struct NetStats
{
    public bool Valid;
    public double PingMs, JitterMs, LossPct; // over the last 30 pings
    public double RouterMs;                  // ping to your own router (NaN if it does not answer)
}

public sealed class NetMonitor : IDisposable
{
    readonly Queue<double> _results = new(); // NaN = lost
    readonly object _lock = new();
    Thread _thread;
    volatile bool _stop;
    double _router = double.NaN;
    public volatile string Target = "1.1.1.1";
    public volatile bool Enabled = true;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Network pings", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    static string Gateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any))
                        return g.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    void Loop()
    {
        using var ping = new Ping();
        string gateway = Gateway();
        int n = 0;
        while (!_stop)
        {
            if (!Enabled)
            {
                lock (_lock) _results.Clear();
                Thread.Sleep(500);
                continue;
            }
            double ms = double.NaN;
            try
            {
                var r = ping.Send(Target, 1000);
                if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
            }
            catch { }
            double router = _router;
            if (gateway != null && n++ % 5 == 0)
            {
                try
                {
                    var r = ping.Send(gateway, 500);
                    router = r.Status == IPStatus.Success ? r.RoundtripTime : double.NaN;
                }
                catch { router = double.NaN; }
            }
            lock (_lock)
            {
                _results.Enqueue(ms);
                while (_results.Count > 30) _results.Dequeue();
                _router = router;
            }
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
        }
    }

    public NetStats Snapshot()
    {
        lock (_lock)
        {
            var s = new NetStats { RouterMs = _router, PingMs = double.NaN };
            if (_results.Count == 0) return s;
            double sum = 0, jitter = 0, prev = double.NaN; int ok = 0, pairs = 0;
            foreach (var v in _results)
            {
                if (double.IsNaN(v)) continue;
                sum += v; ok++;
                if (!double.IsNaN(prev)) { jitter += Math.Abs(v - prev); pairs++; }
                prev = v;
            }
            s.Valid = true;
            s.LossPct = 100.0 * (_results.Count - ok) / _results.Count;
            if (ok > 0) s.PingMs = sum / ok;
            s.JitterMs = pairs > 0 ? jitter / pairs : 0;
            return s;
        }
    }

    public void Dispose() { _stop = true; _thread?.Join(1500); }
}
