using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ColorForge.Services;

public sealed record GpuStressResult(bool Stable, double PeakTemperatureC, double AverageLoadPercent, TimeSpan Duration);

public sealed class GpuStressService
{
    private const uint ElementCount = 262144;
    private const string ShaderSource = @"
RWStructuredBuffer<float4> data : register(u0);
[numthreads(256, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x % 262144;
    float4 v = data[index] + float4(index * 0.000001, 0.173, 0.713, 1.0);
    [loop] for (uint i = 0; i < 256; ++i)
    {
        v = frac(v.yzwx * 1.6180339 + sqrt(abs(v) + 0.0001));
        v += sin(v * 6.2831853) * 0.00001;
    }
    data[index] = v;
}";

    private readonly HardwareMonitoringService _hardware;

    public GpuStressService(HardwareMonitoringService hardware) => _hardware = hardware;

    public async Task<GpuStressResult> RunAsync(
        int seconds,
        string? preferredGpuName,
        Action<HardwareSnapshot, int>? sample,
        CancellationToken cancellationToken)
    {
        seconds = Math.Clamp(seconds, 3, 60);
        ReadOnlyMemory<byte> bytecode = Compiler.Compile(ShaderSource, "CSMain", "AURA-GpuStress.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
        using ID3D11Device device = CreateNvidiaDevice(preferredGpuName);
        using ID3D11DeviceContext context = device.ImmediateContext;
        using ID3D11ComputeShader shader = device.CreateComputeShader(bytecode.Span);
        using ID3D11Buffer buffer = device.CreateBuffer(
            ElementCount * 16,
            BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            16);
        UnorderedAccessViewDescription uavDescription = new UnorderedAccessViewDescription(buffer, Format.Unknown, 0, ElementCount);
        using ID3D11UnorderedAccessView uav = device.CreateUnorderedAccessView(buffer, uavDescription);

        context.CSSetShader(shader);
        context.CSSetUnorderedAccessView(0, uav);
        Stopwatch timer = Stopwatch.StartNew();
        double peak = 0;
        double loadSum = 0;
        int samples = 0;
        bool stable = true;
        try
        {
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < 3; i++)
                    context.Dispatch(ElementCount / 256, 1, 1);
                context.Flush();

                await Task.Delay(80, cancellationToken);
                HardwareSnapshot snapshot = _hardware.ReadSnapshot(preferredGpuName);
                peak = Math.Max(peak, snapshot.GpuTemperatureC ?? 0);
                loadSum += snapshot.GpuLoadPercent ?? 0;
                samples++;
                sample?.Invoke(snapshot, Math.Clamp((int)(timer.Elapsed.TotalSeconds / seconds * 100), 0, 100));

                if (snapshot.GpuTemperatureC >= 84 || snapshot.GpuHotSpotC >= 98)
                {
                    stable = false;
                    break;
                }
                if (device.DeviceRemovedReason.Failure)
                {
                    stable = false;
                    break;
                }
            }
        }
        finally
        {
            context.CSSetUnorderedAccessView(0, null);
            context.CSSetShader(null);
            context.Flush();
        }

        return new GpuStressResult(stable, peak, samples == 0 ? 0 : loadSum / samples, timer.Elapsed);
    }

    private static ID3D11Device CreateNvidiaDevice(string? preferredGpuName)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; ; index++)
        {
            var result = factory.EnumAdapters1(index, out IDXGIAdapter1? adapter);
            if (result.Failure)
                break;
            if (adapter == null)
                continue;

            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                if (description.VendorId != 0x10DE)
                    continue;
                if (!string.IsNullOrWhiteSpace(preferredGpuName) && !GpuNamesMatch(description.Description, preferredGpuName))
                    continue;

                FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    levels,
                    out ID3D11Device? device).CheckError();
                return device ?? throw new InvalidOperationException("Direct3D не создал устройство NVIDIA.");
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredGpuName))
            return CreateNvidiaDevice(null);
        throw new NotSupportedException("Direct3D 11 не нашёл совместимый адаптер NVIDIA для нагрузочного теста.");
    }

    private static bool GpuNamesMatch(string left, string right)
    {
        static string Normalize(string value) => value
            .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("GEFORCE", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("LAPTOP GPU", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }
}
