using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace GamingBurner;

/// <summary>
/// CPU temperature and GPU fan control through LibreHardwareMonitor.
/// GPU fan control goes through the NVIDIA driver and works without admin rights.
/// CPU temperature needs low-level access: the app must run as administrator and the
/// PawnIO driver must be installed, otherwise the value stays unavailable.
/// </summary>
public sealed class HwMonitor : IDisposable
{
    Computer _computer;
    IHardware _cpu, _gpu;
    ISensor _cpuTemp, _gpuHotspot, _gpuMemTemp, _gpuFanRpm;
    readonly List<ISensor> _fanControls = new();
    readonly object _lock = new();

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string Error { get; private set; }
    public bool CanControlFan => _fanControls.Count > 0;
    public bool FanManual { get; private set; }
    public string CpuName => _cpu?.Name ?? "";

    public bool Open()
    {
        try
        {
            // The processor sensors only return data to an administrator, so skip that work otherwise.
            _computer = new Computer { IsCpuEnabled = IsElevated, IsGpuEnabled = true };
            _computer.Open();
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu && _cpu == null) _cpu = hw;
                if (hw.HardwareType == HardwareType.GpuNvidia && _gpu == null) _gpu = hw;
            }
            Update();
            if (_cpu != null)
            {
                // Ryzen reports the package reading as "Core (Tctl/Tdie)"; fall back to any temperature.
                _cpuTemp = _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Tctl"))
                        ?? _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package"))
                        ?? _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
            }
            if (_gpu != null)
            {
                _gpuHotspot = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Hot"));
                _gpuMemTemp = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Memory"));
                _gpuFanRpm = _gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                _fanControls.AddRange(_gpu.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null));
            }
            return true;
        }
        catch (Exception ex) { Error = ex.Message; return false; }
    }

    public void Update()
    {
        lock (_lock)
        {
            try { _cpu?.Update(); _gpu?.Update(); } catch { }
        }
    }

    static double Val(ISensor s) => s?.Value is float v && v > 0 ? v : double.NaN;
    public double CpuTemp => Val(_cpuTemp);
    public double GpuHotspot => Val(_gpuHotspot);
    public double GpuMemTemp => Val(_gpuMemTemp);
    public double GpuFanRpm => Val(_gpuFanRpm);

    /// <summary>Holds every GPU fan at a fixed percentage until FanAuto is called.</summary>
    public bool SetFan(double percent)
    {
        lock (_lock)
        {
            try
            {
                foreach (var s in _fanControls)
                {
                    float min = s.Control.MinSoftwareValue, max = s.Control.MaxSoftwareValue;
                    s.Control.SetSoftware((float)Math.Clamp(percent, min, max));
                }
                FanManual = _fanControls.Count > 0;
                return FanManual;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }
    }

    /// <summary>Hands the fans back to the card's own automatic curve.</summary>
    public void FanAuto()
    {
        lock (_lock)
        {
            try { foreach (var s in _fanControls) s.Control.SetDefault(); } catch (Exception ex) { Error = ex.Message; }
            FanManual = false;
        }
    }

    public IEnumerable<string> Describe()
    {
        foreach (var hw in new[] { _cpu, _gpu })
        {
            if (hw == null) continue;
            yield return $"[{hw.HardwareType}] {hw.Name}";
            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                string ctl = s.Control != null ? $"  control {s.Control.ControlMode} range {s.Control.MinSoftwareValue}-{s.Control.MaxSoftwareValue}" : "";
                yield return $"   {s.SensorType,-12} {s.Name,-28} {s.Value?.ToString("0.##") ?? "n/a"}{ctl}";
            }
        }
    }

    public void Dispose()
    {
        if (FanManual) FanAuto(); // never leave the fans pinned after exit
        try { _computer?.Close(); } catch { }
    }
}
