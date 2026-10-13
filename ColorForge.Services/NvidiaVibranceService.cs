using System;
using System.Linq;
using NvAPIWrapper.Display;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Interfaces.Display;
using ColorForge.Models;

namespace ColorForge.Services;

internal sealed class NvidiaVibranceService
{
    public bool IsAvailable { get; }
    public NvidiaVibranceService()
    {
        try { IsAvailable = Display.GetDisplays().Length != 0; }
        catch { IsAvailable = false; }
    }
    public bool SetPercent(int percent, string deviceName) => SetLevel(Math.Clamp(((double)percent - 50) / 50, -1, 1), deviceName);
    public double? GetLevel(string deviceName) => ReadState(deviceName)?.Normalized;
    public NvidiaColorState? ReadState(string deviceName)
    {
        try
        {
            Display? display = Display.GetDisplays().FirstOrDefault(x => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (display == null) return null;
            static NvidiaColorState State(IDisplayDVCInfo info, bool legacy) => new(info.CurrentLevel, info.MinimumLevel, info.MaximumLevel, info.DefaultLevel, legacy);
            try
            {
                NvidiaColorState modern = State(DisplayApi.GetDVCInfoEx(display.Handle), false);
                if (modern.IsValid) return modern;
            }
            catch { }
            NvidiaColorState older = State(DisplayApi.GetDVCInfo(display.Handle), true);
            return older.IsValid ? older : null;
        }
        catch (Exception error) { AppLogService.Error("Could not read NVIDIA color state.", error); return null; }
    }
    public bool SetLevel(double normalizedLevel, string deviceName)
    {
        NvidiaColorState? state = ReadState(deviceName);
        return state != null && SetState(state.WithNormalized(normalizedLevel), deviceName);
    }
    public bool SetState(NvidiaColorState state, string deviceName)
    {
        if (!state.IsValid) return false;
        try
        {
            Display? display = Display.GetDisplays().FirstOrDefault(x => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            NvidiaColorState? current = ReadState(deviceName);
            if (display == null || current == null || current.Minimum != state.Minimum || current.Maximum != state.Maximum
                || current.Default != state.Default || current.Legacy != state.Legacy) return false;
            if (state.Legacy) DisplayApi.SetDVCLevel(display.Handle, state.Current);
            else DisplayApi.SetDVCLevelEx(display.Handle, state.Current);
            return ReadState(deviceName) == state;
        }
        catch (Exception error) { AppLogService.Error("NVIDIA color write or verification failed.", error); return false; }
    }
}
