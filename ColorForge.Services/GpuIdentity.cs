using System;
using System.Linq;
using System.Runtime.InteropServices;
using NvAPIWrapper.GPU;
using Vortice.DXGI;

namespace ColorForge.Services;

internal static class GpuIdentity
{
    public static GpuTarget Resolve(PhysicalGPU gpu)
    {
        string[] displays = NvAPIWrapper.Display.Display.GetDisplays()
            .Where(x => x.PhysicalGPUs.Length == 1 && x.PhysicalGPUs[0].GPUId == gpu.GPUId)
            .Select(x => x.Name).ToArray();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
        {
            if (adapter == null) continue;
            using (adapter)
            {
                if (adapter.Description1.VendorId != 0x10DE) continue;
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                {
                    if (output == null) continue;
                    using (output)
                    {
                        string displayName = output.Description.DeviceName;
                        if (!displays.Contains(displayName, StringComparer.OrdinalIgnoreCase)) continue;
                        string pnp = FindPnpId(displayName);
                        if (string.IsNullOrWhiteSpace(pnp)) continue;
                        return new($"nvidia:{gpu.BusInformation.BusId}:{gpu.BusInformation.BusSlot}:{gpu.BusInformation.PCIIdentifiers}", gpu.FullName,
                            adapter.Description1.Luid, "pnp:" + pnp);
                    }
                }
            }
        }
        throw new NotSupportedException("Не удалось однозначно сопоставить NVAPI, Direct3D и датчики выбранной GPU. Другой адаптер не используется.");
    }

    private static string FindPnpId(string displayName)
    {
        for (uint index = 0; ; index++)
        {
            DisplayDevice device = new() { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) return string.Empty;
            if (string.Equals(device.Name, displayName, StringComparison.OrdinalIgnoreCase)) return device.Id;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);
}
