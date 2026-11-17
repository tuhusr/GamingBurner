using System.Runtime.InteropServices;

namespace GamingBurner;

/// <summary>
/// GPU core and memory clock offsets through the NVIDIA driver (NVAPI Pstates20), the same
/// mechanism overclocking tools use. Offsets are bounded by the range the driver itself
/// reports for this card and are lost on restart.
///
/// Layout of NV_GPU_PERF_PSTATES20_INFO_V2 (all fields 4 bytes, 7416 bytes total):
///   header  : version, flags, numPstates, numClocks, numBaseVoltages            (20 bytes)
///   pstate  : pstateId, flags, clocks[8], baseVoltages[4]                        (456 bytes) x16
///   clock   : domainId, typeId, flags, delta value, delta min, delta max, data   (44 bytes)
/// </summary>
public sealed unsafe class GpuTuning
{
    const int InfoSize = 7416, HeaderSize = 20, PstateSize = 456, ClockSize = 44, MaxPstates = 16, MaxClocks = 8;
    const uint Ver2 = 0x00021CF8, Ver3 = 0x00031CF8;
    const uint DomainGraphics = 0, DomainMemory = 4;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _getPstates, _setPstates;
    IntPtr _gpu;
    uint _version, _pstateId;
    bool _hasCore, _hasMem;

    public bool Available { get; private set; }
    public string Error { get; private set; } = "not initialised";
    public int CoreOffsetMhz { get; private set; }
    public int MemOffsetMhz { get; private set; }
    public int CoreMinMhz { get; private set; }
    public int CoreMaxMhz { get; private set; }
    public int MemMinMhz { get; private set; }
    public int MemMaxMhz { get; private set; }

    public bool Init()
    {
        try
        {
            var init = (delegate* unmanaged[Cdecl]<int>)QueryInterface(0x0150E828);
            var enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, uint*, int>)QueryInterface(0xE5AC921F);
            _getPstates = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)QueryInterface(0x6FF81213);
            _setPstates = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)QueryInterface(0x0F4DAE6B);
            if (init == null || enumGpus == null || _getPstates == null || _setPstates == null)
                return Fail("this NVIDIA driver does not expose clock control");
            int rc = init();
            if (rc != 0) return Fail($"the NVIDIA driver interface did not start (status {rc})");
            IntPtr* handles = stackalloc IntPtr[64];
            uint count = 0;
            rc = enumGpus(handles, &count);
            if (rc != 0 || count == 0) return Fail($"no NVIDIA graphics card found (status {rc})");
            _gpu = handles[0];
            if (!Read()) return false;
            if (!_hasCore && !_hasMem) return Fail("the card reports no adjustable clocks");
            Available = true;
            Error = null;
            return true;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    bool Fail(string why) { Error = why; Available = false; return false; }

    /// <summary>Reads the current offsets and the allowed range for the top performance state.</summary>
    bool Read()
    {
        byte* info = (byte*)NativeMemory.AllocZeroed(InfoSize);
        try
        {
            int rc = -1;
            foreach (uint ver in new[] { Ver3, Ver2 })
            {
                new Span<byte>(info, InfoSize).Clear();
                *(uint*)info = ver;
                rc = _getPstates(_gpu, info);
                if (rc == 0) { _version = ver; break; }
            }
            if (rc != 0) return Fail($"reading clock settings failed (status {rc})");

            uint numPstates = Math.Min(*(uint*)(info + 8), MaxPstates);
            uint numClocks = Math.Min(*(uint*)(info + 12), MaxClocks);
            if (numPstates == 0) return Fail("the card reports no performance states");
            byte* p0 = info + HeaderSize; // first entry is the highest performance state
            _pstateId = *(uint*)p0;
            _hasCore = _hasMem = false;
            for (int c = 0; c < numClocks; c++)
            {
                byte* clk = p0 + 8 + c * ClockSize;
                uint domain = *(uint*)clk;
                bool editable = (*(uint*)(clk + 8) & 1) != 0;
                int value = *(int*)(clk + 12), min = *(int*)(clk + 16), max = *(int*)(clk + 20);
                if (!editable || max <= min) continue;
                if (domain == DomainGraphics)
                {
                    _hasCore = true;
                    CoreOffsetMhz = value / 1000; CoreMinMhz = min / 1000; CoreMaxMhz = max / 1000;
                }
                else if (domain == DomainMemory)
                {
                    _hasMem = true;
                    MemOffsetMhz = value / 1000; MemMinMhz = min / 1000; MemMaxMhz = max / 1000;
                }
            }
            return true;
        }
        finally { NativeMemory.Free(info); }
    }

    /// <summary>Applies both offsets, then reads them back to confirm the driver accepted them.</summary>
    public bool Apply(int coreMhz, int memMhz)
    {
        if (!Available) return false;
        coreMhz = Math.Clamp(coreMhz, CoreMinMhz, CoreMaxMhz);
        memMhz = Math.Clamp(memMhz, MemMinMhz, MemMaxMhz);
        byte* info = (byte*)NativeMemory.AllocZeroed(InfoSize);
        try
        {
            // A sparse request: only the performance state and the clocks being changed.
            *(uint*)info = _version;
            *(uint*)(info + 8) = 1; // numPstates
            byte* p0 = info + HeaderSize;
            *(uint*)p0 = _pstateId;
            uint n = 0;
            if (_hasCore)
            {
                byte* clk = p0 + 8 + n++ * ClockSize;
                *(uint*)clk = DomainGraphics;
                *(int*)(clk + 12) = coreMhz * 1000;
            }
            if (_hasMem)
            {
                byte* clk = p0 + 8 + n++ * ClockSize;
                *(uint*)clk = DomainMemory;
                *(int*)(clk + 12) = memMhz * 1000;
            }
            *(uint*)(info + 12) = n; // numClocks
            int rc = _setPstates(_gpu, info);
            if (rc != 0) { Error = rc == -137 ? "administrator rights are required to change clocks" : $"the driver refused the change (status {rc})"; return false; }
        }
        catch (Exception ex) { Error = ex.Message; return false; }
        finally { NativeMemory.Free(info); }

        if (!Read()) return false;
        Available = true;
        if ((_hasCore && CoreOffsetMhz != coreMhz) || (_hasMem && MemOffsetMhz != memMhz))
        {
            Error = $"the driver kept core {CoreOffsetMhz} MHz, memory {MemOffsetMhz} MHz";
            return false;
        }
        Error = null;
        return true;
    }

    // ---------- Fan control (ClientFanCoolers) ----------
    // Control struct: version, unknown, count, reserved[8], then 32 items of {coolerId, level, mode, reserved[8]} (1452 bytes).
    // Status struct : version, count, reserved (32 bytes), then 32 items of {coolerId, rpm, minLevel, maxLevel, level, reserved[8]} (1704 bytes).
    const int FanControlSize = 1452, FanStatusSize = 1704;
    const uint FanControlVer = 0x000105AC, FanStatusVer = 0x000106A8;
    delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _fanGetControl, _fanSetControl, _fanGetStatus;

    public bool FanAvailable { get; private set; }
    public bool FanManual { get; private set; }
    public string FanError { get; private set; }
    public int FanMinPct { get; private set; } = 30;
    public int FanMaxPct { get; private set; } = 100;

    public bool InitFan()
    {
        try
        {
            if (_gpu == IntPtr.Zero) { FanError = "no NVIDIA graphics card found"; return false; }
            _fanGetControl = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)QueryInterface(0x814B209F);
            _fanSetControl = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)QueryInterface(0xA58971A5);
            _fanGetStatus = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)QueryInterface(0x35AED5E8);
            if (_fanGetControl == null || _fanSetControl == null || _fanGetStatus == null)
            { FanError = "this NVIDIA driver does not expose fan control"; return false; }

            byte* st = (byte*)NativeMemory.AllocZeroed(FanStatusSize);
            try
            {
                *(uint*)st = FanStatusVer;
                int rc = _fanGetStatus(_gpu, st);
                if (rc != 0) { FanError = $"reading fan status failed (status {rc})"; return false; }
                uint count = *(uint*)(st + 4);
                if (count == 0) { FanError = "the card reports no controllable fans"; return false; }
                FanMinPct = (int)*(uint*)(st + 40 + 8);
                FanMaxPct = (int)*(uint*)(st + 40 + 12);
                if (FanMaxPct <= FanMinPct) { FanMinPct = 30; FanMaxPct = 100; }
            }
            finally { NativeMemory.Free(st); }
            FanAvailable = true;
            return true;
        }
        catch (Exception ex) { FanError = ex.Message; return false; }
    }

    /// <summary>mode 1 = hold every fan at the given percentage; mode 0 = hand back to the card's own curve.</summary>
    bool WriteFan(uint mode, uint level)
    {
        if (!FanAvailable) return false;
        byte* c = (byte*)NativeMemory.AllocZeroed(FanControlSize);
        try
        {
            *(uint*)c = FanControlVer;
            int rc = _fanGetControl(_gpu, c);
            if (rc != 0) { FanError = $"reading fan control failed (status {rc})"; return false; }
            uint count = Math.Min(*(uint*)(c + 8), 32);
            for (int i = 0; i < count; i++)
            {
                byte* item = c + 44 + i * 44;
                *(uint*)(item + 4) = level;
                *(uint*)(item + 8) = mode;
            }
            rc = _fanSetControl(_gpu, c);
            if (rc != 0)
            {
                FanError = rc == -137 ? "administrator rights are required to change fan speed" : $"the driver refused the fan change (status {rc})";
                return false;
            }
            FanError = null;
            return true;
        }
        catch (Exception ex) { FanError = ex.Message; return false; }
        finally { NativeMemory.Free(c); }
    }

    public bool SetFan(double percent)
    {
        bool ok = WriteFan(1, (uint)Math.Clamp(Math.Round(percent), FanMinPct, FanMaxPct));
        if (ok) FanManual = true;
        return ok;
    }

    public bool FanAuto()
    {
        bool ok = WriteFan(0, 0);
        if (ok) FanManual = false;
        return ok;
    }

    public string Describe() => Available
        ? $"core offset {CoreOffsetMhz} MHz (allowed {CoreMinMhz}..{CoreMaxMhz}), memory offset {MemOffsetMhz} MHz (allowed {MemMinMhz}..{MemMaxMhz}), pstate id {_pstateId}, struct version 0x{_version:X8}"
        : "unavailable: " + Error;
}
