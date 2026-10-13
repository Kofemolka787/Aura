using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ColorForge.Services;

public sealed class GpuStressService : IGpuStressRunner
{
    private const uint ElementCount = 65536;
    private const string Shader = @"
RWStructuredBuffer<uint> data : register(u0);
[numthreads(256,1,1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint v = id.x;
    [loop] for (uint i=0; i<256; i++) {
        v = v * 1664525u + 1013904223u;
        v ^= v >> 13;
    }
    data[id.x] = v;
}";
    private static readonly uint[] Expected = Enumerable.Range(0, (int)ElementCount).Select(index =>
    {
        uint v = (uint)index;
        unchecked { for (int i = 0; i < 256; i++) { v = v * 1664525u + 1013904223u; v ^= v >> 13; } }
        return v;
    }).ToArray();
    private readonly IHardwareTelemetry _hardware;
    public GpuStressService(IHardwareTelemetry hardware) => _hardware = hardware;

    public Task<GpuStressResult> RunAsync(int seconds, GpuTarget target, Action<HardwareSnapshot, int>? sample, CancellationToken token) =>
        Task.Run(() => Run(Math.Clamp(seconds, 3, 300), target, sample, token), token);

    private GpuStressResult Run(int seconds, GpuTarget target, Action<HardwareSnapshot, int>? sample, CancellationToken token)
    {
        var bytecode = Compiler.Compile(Shader, "CSMain", "AURA-GpuStress.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
        using ID3D11Device device = CreateDevice(target.AdapterLuid);
        using ID3D11DeviceContext context = device.ImmediateContext;
        using ID3D11ComputeShader shader = device.CreateComputeShader(bytecode.Span);
        using ID3D11Buffer buffer = device.CreateBuffer(ElementCount * 4, BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
        using ID3D11Buffer staging = device.CreateBuffer(ElementCount * 4, BindFlags.None, ResourceUsage.Staging,
            CpuAccessFlags.Read, ResourceOptionFlags.None, 0);
        using ID3D11UnorderedAccessView view = device.CreateUnorderedAccessView(buffer,
            new UnorderedAccessViewDescription(buffer, Format.Unknown, 0, ElementCount));
        context.CSSetShader(shader);
        context.CSSetUnorderedAccessView(0, view);
        Stopwatch timer = Stopwatch.StartNew();
        double peak = 0, load = 0;
        int samples = 0;
        uint[] actual = new uint[ElementCount];
        bool stable = true;
        try
        {
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                token.ThrowIfCancellationRequested();
                for (int batch = 0; batch < 16; batch++) context.Dispatch(ElementCount / 256, 1, 1);
                context.CopyResource(staging, buffer);
                var mapped = context.Map(staging, MapMode.Read);
                try
                {
                    int[] signed = new int[ElementCount];
                    Marshal.Copy(mapped.DataPointer, signed, 0, signed.Length);
                    Buffer.BlockCopy(signed, 0, actual, 0, signed.Length * sizeof(int));
                    if (!actual.SequenceEqual(Expected)) throw new InvalidOperationException("GPU вернул ошибочный результат вычислений.");
                }
                finally { context.Unmap(staging, 0); }
                HardwareSnapshot snapshot = _hardware.ReadSnapshot(target.TelemetrySelector);
                double temperature = HardwareSafety.RequireTemperature(snapshot, true);
                if (!snapshot.GpuLoadPercent.HasValue || !double.IsFinite(snapshot.GpuLoadPercent.Value))
                    throw new InvalidOperationException("Не удалось подтвердить нагрузку выбранного GPU.");
                peak = Math.Max(peak, temperature);
                load += snapshot.GpuLoadPercent.Value;
                samples++;
                sample?.Invoke(snapshot, Math.Clamp((int)(timer.Elapsed.TotalSeconds / seconds * 100), 0, 100));
                if (temperature >= 83 || snapshot.GpuHotSpotC >= 98 || device.DeviceRemovedReason.Failure)
                { stable = false; break; }
            }
        }
        finally
        {
            context.CSSetUnorderedAccessView(0, null);
            context.CSSetShader(null);
            context.Flush();
        }
        double average = samples == 0 ? 0 : load / samples;
        return new(stable && samples >= 3 && average >= 20, peak, average, timer.Elapsed);
    }

    private static ID3D11Device CreateDevice(long luid)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
        {
            if (adapter == null) continue;
            using (adapter)
            {
                if (adapter.Description1.Luid != luid) continue;
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? device).CheckError();
                return device ?? throw new InvalidOperationException("Direct3D не создал выбранное устройство.");
            }
        }
        throw new NotSupportedException("Выбранный GPU отключён. Нагрузка на другом устройстве не запускается.");
    }
}
