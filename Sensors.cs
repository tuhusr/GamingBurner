using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamingBurner;

public struct HwSample
{
    public bool GpuOk;
    public string GpuName;
    public double GpuUtil, GpuMemCtrlUtil, GpuTemp, GpuPowerW, GpuFanPct, GpuClockMhz, GpuMemClockMhz;
    public double VramUsedMb, VramTotalMb;
    public double CpuTotal, CpuMaxThread;
    public double[] CpuThreads;
    public double RamUsedGb, RamTotalGb, CommitUsedGb;
    public double GameRamMb, GameCpuCores;
    public long Seq; // counts up with every fresh reading
    public double CpuClockMhz;      // effective clock speed across the processor
    public double DiskActivePct;    // share of time the drives were busy
    public double HardFaultsPerSec; // memory pages Windows had to fetch back from disk (sustained values mean memory is short)
    public double CpuTemp, GpuHotspot, GpuMemTemp, GpuFanRpm; // NaN when not readable
}

/// <summary>Polls GPU (NVIDIA NVML), CPU load per thread, and memory twice a second on its own thread.</summary>
public sealed class Sensors : IDisposable
{
    // --- NVML (nvml.dll ships with the NVIDIA driver in System32) ---
    [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong Total, Free, Used; }
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int NvInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] static extern int NvShutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int NvHandle(uint index, out IntPtr dev);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] static extern int NvName(IntPtr dev, byte[] name, uint len);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int NvUtilRates(IntPtr dev, out NvUtil u);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int NvMemInfo(IntPtr dev, out NvMem m);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int NvTemp(IntPtr dev, int sensor, out uint c);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int NvPower(IntPtr dev, out uint milliwatts);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")] static extern int NvFan(IntPtr dev, out uint pct);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] static extern int NvClock(IntPtr dev, int type, out uint mhz);

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus s);

    IntPtr _gpu;
    bool _nvml;
    string _gpuName = "";
    long[] _cpuIdle, _cpuTotalTime; // previous idle and total times per thread
    PerformanceCounter _perfClock, _perfDiskIdle, _perfPagesIn;
    double _baseMhz;

    [StructLayout(LayoutKind.Sequential)]
    struct CpuTimes { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, [Out] CpuTimes[] info, int length, out int returned);

    /// <summary>Load of every processor thread from one system call: the share of time not idle since the last pass.</summary>
    void ReadCpu(ref HwSample s)
    {
        int n = Environment.ProcessorCount;
        var times = new CpuTimes[n];
        if (NtQuerySystemInformation(8, times, n * Marshal.SizeOf<CpuTimes>(), out _) != 0) return;
        bool first = _cpuIdle == null;
        if (first) { _cpuIdle = new long[n]; _cpuTotalTime = new long[n]; }
        var load = new double[n];
        double idleSum = 0, totalSum = 0;
        for (int i = 0; i < n; i++)
        {
            long idle = times[i].Idle, total = times[i].Kernel + times[i].User; // kernel time includes idle time
            long dIdle = idle - _cpuIdle[i], dTotal = total - _cpuTotalTime[i];
            _cpuIdle[i] = idle; _cpuTotalTime[i] = total;
            if (first || dTotal <= 0) continue;
            load[i] = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            if (load[i] > s.CpuMaxThread) s.CpuMaxThread = load[i];
            idleSum += dIdle; totalSum += dTotal;
        }
        if (first) return;
        s.CpuThreads = load;
        s.CpuTotal = totalSum > 0 ? Math.Clamp(100.0 * (totalSum - idleSum) / totalSum, 0, 100) : 0;
    }
    Thread _thread;
    volatile bool _stop;
    HwSample _latest;
    readonly object _lock = new();

    public volatile int PollMs = 1000; // how often sensors are read
    public volatile bool Paused;       // true while live tracking is stopped: nothing is read
    public HwMonitor Hw; // optional: CPU temperature and extra GPU sensors
    public uint GamePid; // set by the UI so the game's own RAM and CPU use are reported
    long _gameCpuTicks; long _gameCpuStamp; uint _gameCpuPid;

    public HwSample Latest { get { lock (_lock) return _latest; } }
    public string GpuError { get; private set; }
    public string DriverVersion { get; private set; } = "unknown";
    [DllImport("nvml.dll", EntryPoint = "nvmlSystemGetDriverVersion")] static extern int NvDriver(byte[] version, uint len);

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sensors", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Init()
    {
        try
        {
            if (NvInit() == 0 && NvHandle(0, out _gpu) == 0)
            {
                _nvml = true;
                var buf = new byte[96];
                if (NvName(_gpu, buf, (uint)buf.Length) == 0)
                    _gpuName = System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');
                var ver = new byte[80];
                if (NvDriver(ver, (uint)ver.Length) == 0)
                    DriverVersion = System.Text.Encoding.ASCII.GetString(ver).TrimEnd('\0');
            }
            else GpuError = "NVML did not initialise";
        }
        catch (Exception ex) { GpuError = "NVML unavailable: " + ex.Message; }

        // Three Windows counters: processor speed relative to its rated clock, drive idle time, pages read back from disk.
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                _baseMhz = Convert.ToDouble(key?.GetValue("~MHz") ?? 0);
            _perfClock = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _perfDiskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", true);
            _perfPagesIn = new PerformanceCounter("Memory", "Pages Input/sec", true);
            _perfClock.NextValue(); _perfDiskIdle.NextValue(); _perfPagesIn.NextValue();
        }
        catch { _perfClock = null; _perfDiskIdle = null; _perfPagesIn = null; }
    }

    void Loop()
    {
        Init();
        int pass = 0;
        while (!_stop)
        {
            if (Paused) { Thread.Sleep(200); continue; }
            int wait = Math.Clamp(PollMs, 250, 5000);
            Thread.Sleep(pass == 0 ? 300 : wait);
            pass++;
            int slowEvery = Math.Max(1, 2000 / wait);
            var s = new HwSample { GpuName = _gpuName, CpuTemp = double.NaN, GpuHotspot = double.NaN, GpuMemTemp = double.NaN, GpuFanRpm = double.NaN };
            if (Hw != null)
            {
                if (pass % slowEvery == 0 || pass == 1) Hw.Update(); // the slowest readings are refreshed about every two seconds
                s.CpuTemp = Hw.CpuTemp; s.GpuHotspot = Hw.GpuHotspot; s.GpuMemTemp = Hw.GpuMemTemp; s.GpuFanRpm = Hw.GpuFanRpm;
            }
            if (_nvml)
            {
                try
                {
                    s.GpuOk = true;
                    if (NvUtilRates(_gpu, out var u) == 0) { s.GpuUtil = u.Gpu; s.GpuMemCtrlUtil = u.Memory; }
                    if (NvMemInfo(_gpu, out var m) == 0) { s.VramUsedMb = m.Used / 1048576.0; s.VramTotalMb = m.Total / 1048576.0; }
                    if (NvTemp(_gpu, 0, out var t) == 0) s.GpuTemp = t;
                    if (NvPower(_gpu, out var p) == 0) s.GpuPowerW = p / 1000.0;
                    if (NvFan(_gpu, out var f) == 0) s.GpuFanPct = f;
                    if (NvClock(_gpu, 0, out var c) == 0) s.GpuClockMhz = c;
                    if (NvClock(_gpu, 2, out var mc) == 0) s.GpuMemClockMhz = mc;
                }
                catch { s.GpuOk = false; }
            }
            try { ReadCpu(ref s); } catch { }
            try
            {
                if (_perfClock != null && _baseMhz > 0) s.CpuClockMhz = _baseMhz * _perfClock.NextValue() / 100.0;
                if (_perfDiskIdle != null) s.DiskActivePct = Math.Clamp(100.0 - _perfDiskIdle.NextValue(), 0, 100);
                if (_perfPagesIn != null) s.HardFaultsPerSec = _perfPagesIn.NextValue();
            }
            catch { }
            var ms = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
            if (GlobalMemoryStatusEx(ref ms))
            {
                s.RamTotalGb = ms.TotalPhys / 1073741824.0;
                s.RamUsedGb = (ms.TotalPhys - ms.AvailPhys) / 1073741824.0;
                s.CommitUsedGb = (ms.TotalPageFile - ms.AvailPageFile) / 1073741824.0;
            }
            uint pid = GamePid;
            if (pid != 0)
            {
                try
                {
                    using var pr = Process.GetProcessById((int)pid);
                    s.GameRamMb = pr.WorkingSet64 / 1048576.0;
                    long cpu = pr.TotalProcessorTime.Ticks, now = Stopwatch.GetTimestamp();
                    if (_gameCpuPid == pid && now > _gameCpuStamp)
                        s.GameCpuCores = (cpu - _gameCpuTicks) / (double)TimeSpan.TicksPerSecond
                                         / ((now - _gameCpuStamp) / (double)Stopwatch.Frequency);
                    _gameCpuTicks = cpu; _gameCpuStamp = now; _gameCpuPid = pid;
                }
                catch { }
            }
            s.Seq = pass;
            lock (_lock) _latest = s;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1200);
        if (_nvml) { try { NvShutdown(); } catch { } }
        _perfClock?.Dispose(); _perfDiskIdle?.Dispose(); _perfPagesIn?.Dispose();
    }
}
