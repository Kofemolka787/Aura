using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
using Microsoft.Win32;

namespace ColorForge.Services;

public enum CpuTuneBackend
{
    None,
    AmdPbo,
    IntelTurboPower
}

public sealed record CpuTuneSupport(
    bool IsSupported,
    string ProcessorName,
    string Detail,
    CpuTuneBackend Backend = CpuTuneBackend.None,
    string AdapterId = "none");

public sealed class CpuAutoTuneService
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPackagePowerLimit = 0x610;
    private const uint MsrPackagePowerInfo = 0x614;
    private const string RyzenModuleSha256 = "301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450";
    private const string IntelModuleSha256 = "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";

    private static readonly ProfileScale[] CandidateScales =
    {
        new("low", "Low Performance", 1.05),
        new("medium", "Medium Performance", 1.12),
        new("ultra", "Ultra Performance", 1.20)
    };

    private static readonly IReadOnlyDictionary<int, AmdAdapter> AmdAdapters = BuildAmdAdapters();

    private readonly HardwareMonitoringService _hardware;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ulong? _intelOriginalPowerLimit;
    private CpuPboProfile? _amdStockProfile;
    private int? _amdCodeName;

    public CpuAutoTuneService(HardwareMonitoringService hardware) => _hardware = hardware;

    public CpuTuneSupport ProbeSupport()
    {
        string name = ReadProcessorName();
        if (!_hardware.ReadSnapshot().CpuTemperatureC.HasValue)
            return new CpuTuneSupport(false, name, "Недоступен датчик температуры CPU. AURA блокирует нагрузочный тест без теплового контроля.");

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            return ProbeAmd(name);

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return ProbeIntel(name);

        return new CpuTuneSupport(false, name, "Производитель процессора не поддерживает доступный AURA аппаратный интерфейс.");
    }

    public async Task<AutoTuneResult> TuneAsync(
        IProgress<AutoTuneProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        CpuTuneSupport support = ProbeSupport();
        try
        {
            if (!support.IsSupported)
                throw new NotSupportedException(support.Detail);

            return support.Backend switch
            {
                CpuTuneBackend.AmdPbo => await TuneAmdAsync(support, progress, cancellationToken),
                CpuTuneBackend.IntelTurboPower => await TuneIntelAsync(support, progress, cancellationToken),
                _ => throw new NotSupportedException("Для процессора не выбран безопасный аппаратный адаптер.")
            };
        }
        catch
        {
            TryResetStock(support);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ApplySavedProfile(string profileId)
    {
        CpuTuneSupport support = ProbeSupport();
        if (!support.IsSupported)
            throw new NotSupportedException(support.Detail);

        ProfileScale scale = CandidateScales.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))
            ?? new ProfileScale("stock", "Stock", 1.0);

        switch (support.Backend)
        {
            case CpuTuneBackend.AmdPbo:
                using (PawnIoBridge bridge = OpenRyzenModule())
                {
                    AmdAdapter adapter = GetActiveAmdAdapter(bridge);
                    CpuPboProfile stock = GetAmdStockProfile(support.ProcessorName, adapter);
                    ApplyAmdProfile(bridge, adapter, ScaleProfile(stock, scale));
                }
                break;
            case CpuTuneBackend.IntelTurboPower:
                using (PawnIoBridge bridge = OpenIntelModule())
                {
                    IntelPowerState state = ReadIntelPowerState(bridge);
                    _intelOriginalPowerLimit ??= state.RawLimit;
                    WriteIntelPowerLimit(bridge, state, scale.Factor);
                }
                break;
            default:
                throw new NotSupportedException("Профиль несовместим с найденным процессором.");
        }
    }

    public void ResetToStock()
    {
        CpuTuneSupport support = ProbeSupport();
        if (!support.IsSupported)
            throw new NotSupportedException(support.Detail);
        ResetToStock(support);
    }

    private CpuTuneSupport ProbeAmd(string name)
    {
        if (!PawnIoBridge.IsInstalled)
            return new CpuTuneSupport(false, name, "Нужен официальный подписанный драйвер PawnIO. Он устанавливается только после подтверждения.");

        try
        {
            using PawnIoBridge bridge = OpenRyzenModule();
            AmdAdapter adapter = GetActiveAmdAdapter(bridge);
            if (!adapter.SupportsPbo)
            {
                return new CpuTuneSupport(
                    false,
                    name,
                    $"{adapter.Generation} определён, но это поколение не имеет универсального PBO-интерфейса. Безопасный разгон возможен только через BIOS конкретной платы.",
                    CpuTuneBackend.None,
                    adapter.Id);
            }

            if (adapter.IsOverclockableCommand != 0)
            {
                ulong[] response = SendSmuLocked(bridge, adapter.IsOverclockableCommand, 0);
                if (response.Length > 0 && response[0] == 0)
                    return new CpuTuneSupport(false, name, $"{adapter.Generation} поддерживается, но BIOS/плата заблокировали PBO.", CpuTuneBackend.None, adapter.Id);
            }

            CpuPboProfile stock = GetAmdStockProfile(name, adapter);
            return new CpuTuneSupport(
                true,
                name,
                $"{adapter.Generation} · PBO/SMU определён по возможностям. База: PPT {stock.PptWatts} W, TDC {stock.TdcAmps} A, EDC {stock.EdcAmps} A; напряжение и память не меняются.",
                CpuTuneBackend.AmdPbo,
                adapter.Id);
        }
        catch (Exception ex)
        {
            return new CpuTuneSupport(false, name, "AMD SMU: " + ex.Message);
        }
    }

    private CpuTuneSupport ProbeIntel(string name)
    {
        if (!PawnIoBridge.IsInstalled)
            return new CpuTuneSupport(false, name, "Нужен официальный подписанный драйвер PawnIO для чтения и безопасного возврата лимитов Intel Turbo.");

        try
        {
            using PawnIoBridge bridge = OpenIntelModule();
            IntelPowerState state = ReadIntelPowerState(bridge);
            _intelOriginalPowerLimit ??= state.RawLimit;
            if (state.IsLocked)
            {
                return new CpuTuneSupport(
                    false,
                    name,
                    "Intel обнаружен, но BIOS установил аппаратный lock на MSR Package Power Limit. Программа не может снять этот lock без прошивки BIOS.",
                    CpuTuneBackend.None,
                    "intel-rapl-locked");
            }

            string unlock = IsIntelMultiplierUnlocked(name)
                ? "разблокированная модель"
                : "множитель заблокирован; AURA оптимизирует только штатный Turbo";
            return new CpuTuneSupport(
                true,
                name,
                $"Intel RAPL · {unlock}. Текущие PL1 {state.Pl1Watts:0} W / PL2 {state.Pl2Watts:0} W; напряжение и множитель не меняются.",
                CpuTuneBackend.IntelTurboPower,
                "intel-rapl");
        }
        catch (Exception ex)
        {
            return new CpuTuneSupport(false, name, "Intel RAPL недоступен на этой платформе: " + ex.Message);
        }
    }

    private async Task<AutoTuneResult> TuneAmdAsync(
        CpuTuneSupport support,
        IProgress<AutoTuneProgress>? progress,
        CancellationToken cancellationToken)
    {
        using PawnIoBridge bridge = OpenRyzenModule();
        AmdAdapter adapter = GetActiveAmdAdapter(bridge);
        CpuPboProfile stock = GetAmdStockProfile(support.ProcessorName, adapter);
        ApplyAmdProfile(bridge, adapter, stock);
        TestResult baseline = await RunCpuLoadAsync(8, 86, progress, 2, "Базовый замер", cancellationToken);
        if (!baseline.Stable)
            throw new InvalidOperationException("CPU достиг 86 °C уже на базовом профиле. Автонастройка остановлена.");
        CpuPboProfile selected = stock;
        TestResult selectedTest = baseline;

        for (int i = 0; i < CandidateScales.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CpuPboProfile candidate = ScaleProfile(stock, CandidateScales[i]);
            ApplyAmdProfile(bridge, adapter, candidate);
            int startPercent = 18 + i * 18;
            TestResult test = await RunCpuLoadAsync(10, 86, progress, startPercent, $"Проверка {candidate.Label}", cancellationToken);
            if (!test.Stable || test.PeakTemperatureC >= 86 || test.Score < selectedTest.Score * 0.985)
            {
                ApplyAmdProfile(bridge, adapter, selected);
                break;
            }
            selected = candidate;
            selectedTest = test;
        }

        ApplyAmdProfile(bridge, adapter, selected);
        TestResult confirmation = await RunCpuLoadAsync(18, 86, progress, 78, "Финальная проверка", cancellationToken);
        if (!confirmation.Stable || confirmation.PeakTemperatureC >= 86 || confirmation.Score < baseline.Score * 0.985)
        {
            selected = stock;
            ApplyAmdProfile(bridge, adapter, stock);
            confirmation = baseline;
        }

        double gain = baseline.Score <= 0 ? 0 : (confirmation.Score / baseline.Score - 1d) * 100d;
        progress?.Report(new AutoTuneProgress(100, "Готово", selected.Label, _hardware.ReadSnapshot(), selected.PptWatts));
        return new AutoTuneResult
        {
            Target = AutoTuneTarget.Processor,
            Success = true,
            Title = selected.Id == "stock" ? "Оставлен безопасный Stock" : selected.Label + " выбран",
            Detail = $"{adapter.Generation}: PPT {selected.PptWatts} W · TDC {selected.TdcAmps} A · EDC {selected.EdcAmps} A. Без ручного напряжения; параметры сбрасываются после перезагрузки.",
            CpuProfileId = selected.Id,
            CpuBackendId = adapter.Id,
            CpuPptWatts = selected.PptWatts,
            CpuTdcAmps = selected.TdcAmps,
            CpuEdcAmps = selected.EdcAmps,
            PeakTemperatureC = confirmation.PeakTemperatureC,
            ImprovementPercent = Math.Max(0, gain)
        };
    }

    private async Task<AutoTuneResult> TuneIntelAsync(
        CpuTuneSupport support,
        IProgress<AutoTuneProgress>? progress,
        CancellationToken cancellationToken)
    {
        using PawnIoBridge bridge = OpenIntelModule();
        IntelPowerState stock = ReadIntelPowerState(bridge);
        _intelOriginalPowerLimit ??= stock.RawLimit;
        TestResult baseline = await RunCpuLoadAsync(8, 92, progress, 2, "Базовый Intel Turbo", cancellationToken);
        if (!baseline.Stable)
            throw new InvalidOperationException("CPU достиг 92 °C уже на штатном Intel Turbo. Автонастройка остановлена.");
        ProfileScale selectedScale = new("stock", "Stock", 1.0);
        TestResult selectedTest = baseline;
        IntelPowerState selectedState = stock;

        for (int i = 0; i < CandidateScales.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileScale scale = CandidateScales[i];
            IntelPowerState candidate = WriteIntelPowerLimit(bridge, stock, scale.Factor);
            TestResult test = await RunCpuLoadAsync(10, 92, progress, 18 + i * 18, $"Проверка {scale.Label}", cancellationToken);
            if (!test.Stable || test.PeakTemperatureC >= 92 || test.Score < selectedTest.Score * 0.985)
            {
                WriteMsr(bridge, MsrPackagePowerLimit, selectedState.RawLimit);
                break;
            }
            selectedScale = scale;
            selectedState = candidate;
            selectedTest = test;
        }

        WriteMsr(bridge, MsrPackagePowerLimit, selectedState.RawLimit);
        TestResult confirmation = await RunCpuLoadAsync(18, 92, progress, 78, "Финальная проверка", cancellationToken);
        if (!confirmation.Stable || confirmation.PeakTemperatureC >= 92 || confirmation.Score < baseline.Score * 0.985)
        {
            selectedScale = new ProfileScale("stock", "Stock", 1.0);
            selectedState = stock;
            WriteMsr(bridge, MsrPackagePowerLimit, stock.RawLimit);
            confirmation = baseline;
        }

        double gain = baseline.Score <= 0 ? 0 : (confirmation.Score / baseline.Score - 1d) * 100d;
        progress?.Report(new AutoTuneProgress(100, "Готово", selectedScale.Label, _hardware.ReadSnapshot(), (int)Math.Round(selectedState.Pl1Watts)));
        return new AutoTuneResult
        {
            Target = AutoTuneTarget.Processor,
            Success = true,
            Title = selectedScale.Id == "stock" ? "Оставлены штатные Intel Turbo limits" : selectedScale.Label + " выбран",
            Detail = $"Intel Turbo Power: PL1 {selectedState.Pl1Watts:0} W · PL2 {selectedState.Pl2Watts:0} W. Множитель и напряжение не изменялись; исходный MSR сохранён для отката.",
            CpuProfileId = selectedScale.Id,
            CpuBackendId = "intel-rapl",
            CpuPptWatts = (int)Math.Round(selectedState.Pl1Watts),
            PeakTemperatureC = confirmation.PeakTemperatureC,
            ImprovementPercent = Math.Max(0, gain)
        };
    }

    private async Task<TestResult> RunCpuLoadAsync(
        int seconds,
        double maximumTemperatureC,
        IProgress<AutoTuneProgress>? progress,
        int basePercent,
        string stage,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource loadStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long operations = 0;
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        Task[] workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
        {
            double x = 0.001 + worker;
            long local = 0;
            while (!loadStop.IsCancellationRequested)
            {
                for (int i = 1; i <= 12000; i++)
                    x = Math.Sqrt(x + i) * 1.0000001192092896;
                local += 12000;
            }
            Interlocked.Add(ref operations, local);
            GC.KeepAlive(x);
        }, loadStop.Token)).ToArray();

        Stopwatch timer = Stopwatch.StartNew();
        double peak = 0;
        bool stable = true;
        try
        {
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HardwareSnapshot snapshot = _hardware.ReadSnapshot();
                peak = Math.Max(peak, snapshot.CpuTemperatureC ?? 0);
                if (snapshot.CpuTemperatureC >= maximumTemperatureC)
                {
                    stable = false;
                    break;
                }
                int local = (int)(timer.Elapsed.TotalSeconds / seconds * 17);
                progress?.Report(new AutoTuneProgress(
                    Math.Clamp(basePercent + local, 0, 98),
                    stage,
                    snapshot.CpuTemperatureC.HasValue ? $"CPU {snapshot.CpuTemperatureC:0} °C · {snapshot.CpuClockMhz:0} MHz" : "Нагрузка на все потоки",
                    snapshot));
                await Task.Delay(500, cancellationToken);
            }
        }
        finally
        {
            loadStop.Cancel();
            try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
        }

        return new TestResult(operations / Math.Max(0.1, timer.Elapsed.TotalSeconds), peak, stable);
    }

    private AmdAdapter GetActiveAmdAdapter(PawnIoBridge bridge)
    {
        ulong[] code = bridge.Execute("ioctl_get_code_name", null, 1);
        if (code.Length != 1 || !AmdAdapters.TryGetValue((int)code[0], out AmdAdapter? adapter))
            throw new NotSupportedException("Поколение AMD SMU не распознано подписанным модулем.");
        _amdCodeName = (int)code[0];
        return adapter;
    }

    private CpuPboProfile GetAmdStockProfile(string processorName, AmdAdapter adapter)
    {
        if (_amdStockProfile != null && _amdCodeName == adapter.CodeName)
            return _amdStockProfile;

        int tdp = EstimateAmdTdp(processorName, adapter);
        _amdStockProfile = tdp switch
        {
            <= 15 => new CpuPboProfile("stock", "Stock", 25, 20, 30),
            <= 28 => new CpuPboProfile("stock", "Stock", 40, 30, 45),
            <= 35 => new CpuPboProfile("stock", "Stock", 48, 35, 50),
            <= 45 => new CpuPboProfile("stock", "Stock", 65, 45, 70),
            <= 65 => new CpuPboProfile("stock", "Stock", 88, 60, 90),
            <= 95 => new CpuPboProfile("stock", "Stock", 128, 80, 125),
            <= 105 => new CpuPboProfile("stock", "Stock", 142, 95, 140),
            <= 120 => new CpuPboProfile("stock", "Stock", 162, 120, 180),
            _ => new CpuPboProfile("stock", "Stock", 230, 160, 225)
        };
        return _amdStockProfile;
    }

    private static CpuPboProfile ScaleProfile(CpuPboProfile stock, ProfileScale scale) => new(
        scale.Id,
        scale.Label,
        ScaleLimit(stock.PptWatts, scale.Factor, 280),
        ScaleLimit(stock.TdcAmps, scale.Factor, 220),
        ScaleLimit(stock.EdcAmps, scale.Factor, 300));

    private static int ScaleLimit(int value, double factor, int absoluteMaximum) =>
        Math.Clamp((int)Math.Round(value * factor), value, Math.Min(absoluteMaximum, (int)Math.Ceiling(value * 1.25)));

    private static int EstimateAmdTdp(string name, AmdAdapter adapter)
    {
        string upper = name.ToUpperInvariant();
        Match match = Regex.Match(upper, @"(?:RYZEN|THREADRIPPER)[^0-9]*(?:[3579]\s*)?(\d{4})");
        int model = match.Success && int.TryParse(match.Groups[1].Value, out int parsed) ? parsed : 0;

        if (upper.Contains("THREADRIPPER")) return 170;
        if (upper.Contains(" U ") || Regex.IsMatch(upper, @"\dU\b")) return 15;
        if (upper.Contains(" HS") || upper.Contains(" H ")) return 35;
        if (upper.Contains(" HX")) return 55;
        if (Regex.IsMatch(upper, @"\d{4}GE\b")) return 35;
        if (Regex.IsMatch(upper, @"\d{4}G\b")) return 65;
        if (model is 7950 or 7900 && upper.Contains("X") && !upper.Contains("X3D")) return 170;
        if (model == 9950) return 170;
        if (model == 9900) return 120;
        if (upper.Contains("X3D")) return 120;
        if (model is 7700 or 7600 or 9700 or 9600) return upper.Contains("X") && model < 9000 ? 105 : 65;
        if (model is 5950 or 5900 or 5800 or 3950 or 3900 or 3800) return upper.Contains("X") ? 105 : 65;
        if (model == 3600 && upper.Contains("X")) return 95;
        if (adapter.IsMobileFamily) return 45;
        return 65;
    }

    private static IReadOnlyDictionary<int, AmdAdapter> BuildAmdAdapters()
    {
        Dictionary<int, AmdAdapter> map = new();
        void Add(AmdAdapter adapter) => map[adapter.CodeName] = adapter;

        Add(new AmdAdapter(3, "matisse", "Ryzen 3000 / Zen 2", true, false, 0x53, 0x54, 0x55, 0x6F));
        Add(new AmdAdapter(5, "castle-peak", "Threadripper 3000 / Zen 2", true, false, 0x53, 0x54, 0x55, 0x6F));
        Add(new AmdAdapter(11, "vermeer", "Ryzen 5000 / Zen 3", true, false, 0x53, 0x54, 0x55, 0x6F));
        Add(new AmdAdapter(21, "chagall", "Threadripper 5000 / Zen 3", true, false, 0x53, 0x54, 0x55, 0x6F));
        Add(new AmdAdapter(16, "raphael", "Ryzen 7000 / Zen 4", true, false, 0x56, 0x57, 0x58, 0x6F));
        Add(new AmdAdapter(17, "granite-ridge", "Ryzen 9000 / Zen 5", true, false, 0x56, 0x57, 0x58, 0x6F));
        Add(new AmdAdapter(27, "storm-peak", "Threadripper 7000 / Zen 4", true, false, 0x56, 0x57, 0x58, 0x6F));
        Add(new AmdAdapter(28, "dragon-range", "Ryzen 7000 HX / Zen 4", true, true, 0x56, 0x57, 0x58, 0x6F));
        Add(new AmdAdapter(38, "shimada-peak", "Threadripper 9000 / Zen 5", true, false, 0x56, 0x57, 0x58, 0x6F));

        foreach ((int code, string id, string generation, byte overclockable) in new[]
        {
            (2, "picasso", "Ryzen 3000 APU / Picasso", (byte)0x87),
            (6, "raven-ridge", "Ryzen 2000 APU / Raven Ridge", (byte)0x4C),
            (7, "raven-ridge2", "Ryzen APU / Raven Ridge 2", (byte)0x4C),
            (15, "dali", "Athlon/Ryzen Mobile / Dali", (byte)0x87),
            (19, "fireflight", "Ryzen Embedded / FireFlight", (byte)0x4C)
        })
            Add(new AmdAdapter(code, id, generation, true, true, 0x2F, 0x34, 0x36, overclockable));

        foreach ((int code, string id, string generation) in new[]
        {
            (1, "renoir", "Ryzen 4000 APU / Renoir"), (10, "rembrandt", "Ryzen 6000 APU / Rembrandt"),
            (12, "van-gogh", "Ryzen Custom APU / Van Gogh"),
            (13, "cezanne", "Ryzen 5000 APU / Cezanne"), (22, "lucienne", "Ryzen 5000 Mobile / Lucienne"),
            (23, "phoenix", "Ryzen 7000/8000 APU / Phoenix"), (24, "phoenix2", "Ryzen 7000/8000 APU / Phoenix 2"),
            (25, "mendocino", "Ryzen 7000 Mobile / Mendocino"), (29, "mero", "Ryzen Custom APU / Mero"),
            (30, "hawk-point", "Ryzen 8000 APU / Hawk Point"),
            (31, "strix-point", "Ryzen AI 300 / Strix Point"), (32, "strix-halo", "Ryzen AI Max / Strix Halo"),
            (33, "krackan-point", "Ryzen AI / Krackan Point"), (34, "krackan-point2", "Ryzen AI / Krackan Point 2")
        })
            Add(new AmdAdapter(code, id, generation, true, true, 0x32, 0x38, 0x3A, 0x82));

        foreach ((int code, string id, string generation) in new[]
        {
            (0, "colfax", "Threadripper 2000 / Colfax"),
            (4, "whitehaven", "Threadripper 1000 / Whitehaven"), (8, "summit-ridge", "Ryzen 1000 / Summit Ridge"),
            (9, "pinnacle-ridge", "Ryzen 2000 / Pinnacle Ridge"),
            (18, "naples", "EPYC Naples"),
            (20, "rome", "EPYC Rome"), (26, "genoa", "EPYC Genoa"),
            (35, "turin", "EPYC Turin"), (36, "turin-d", "EPYC Turin-D"), (37, "bergamo", "EPYC Bergamo"),
            (39, "carrizo", "AMD Carrizo"), (40, "bristol-ridge", "AMD Bristol Ridge"), (41, "stoney-ridge", "AMD Stoney Ridge")
        })
            Add(new AmdAdapter(code, id, generation, false, true, 0, 0, 0, 0));

        return map;
    }

    private static void ApplyAmdProfile(PawnIoBridge bridge, AmdAdapter adapter, CpuPboProfile profile)
    {
        using Mutex mutex = new(false, "Access_PCI");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new TimeoutException("Другой аппаратный монитор удерживает доступ к PCI. Закрой HWiNFO/Ryzen Master и повтори.");
        try
        {
            SendSmu(bridge, adapter.PptCommand, (ulong)profile.PptWatts * 1000);
            SendSmu(bridge, adapter.TdcCommand, (ulong)profile.TdcAmps * 1000);
            SendSmu(bridge, adapter.EdcCommand, (ulong)profile.EdcAmps * 1000);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static ulong[] SendSmu(PawnIoBridge bridge, byte command, ulong firstArgument)
    {
        if (command == 0)
            throw new NotSupportedException("SMU-команда недоступна для этого поколения.");
        ulong[] input = { command, firstArgument, 0, 0, 0, 0, 0 };
        return bridge.Execute("ioctl_send_smu_command", input, 6);
    }

    private static ulong[] SendSmuLocked(PawnIoBridge bridge, byte command, ulong firstArgument)
    {
        using Mutex mutex = new(false, "Access_PCI");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new TimeoutException("Другой аппаратный монитор удерживает доступ к PCI. Закрой HWiNFO/Ryzen Master и повтори.");
        try { return SendSmu(bridge, command, firstArgument); }
        finally { mutex.ReleaseMutex(); }
    }

    private static IntelPowerState ReadIntelPowerState(PawnIoBridge bridge)
    {
        ulong unitMsr = ReadMsr(bridge, MsrRaplPowerUnit);
        int exponent = (int)(unitMsr & 0xF);
        double unit = 1d / (1 << exponent);
        ulong raw = ReadMsr(bridge, MsrPackagePowerLimit);
        ulong info = ReadMsr(bridge, MsrPackagePowerInfo);
        double pl1 = (raw & 0x7FFF) * unit;
        double pl2 = ((raw >> 32) & 0x7FFF) * unit;
        double max = ((info >> 32) & 0x7FFF) * unit;
        if (pl1 <= 0 || pl2 <= 0)
            throw new NotSupportedException("Процессор не предоставил рабочие PL1/PL2.");
        if (max <= 0)
            max = Math.Min(253, Math.Max(pl1, pl2) * 1.25);
        return new IntelPowerState(raw, unit, pl1, pl2, max, (raw & (1UL << 63)) != 0);
    }

    private static IntelPowerState WriteIntelPowerLimit(PawnIoBridge bridge, IntelPowerState baseline, double factor)
    {
        if (baseline.IsLocked)
            throw new NotSupportedException("MSR Package Power Limit заблокирован BIOS.");
        double pl1 = Math.Min(baseline.MaximumWatts, baseline.Pl1Watts * factor);
        double pl2 = Math.Min(baseline.MaximumWatts, baseline.Pl2Watts * factor);
        ulong pl1Raw = (ulong)Math.Clamp((long)Math.Round(pl1 / baseline.PowerUnit), 1, 0x7FFF);
        ulong pl2Raw = (ulong)Math.Clamp((long)Math.Round(pl2 / baseline.PowerUnit), 1, 0x7FFF);
        ulong raw = baseline.RawLimit;
        raw = (raw & ~0x7FFFUL) | pl1Raw;
        raw = (raw & ~(0x7FFFUL << 32)) | (pl2Raw << 32);
        raw |= 1UL << 15;
        raw |= 1UL << 47;
        WriteMsr(bridge, MsrPackagePowerLimit, raw);
        ulong verify = ReadMsr(bridge, MsrPackagePowerLimit);
        if ((verify & 0x7FFF) != pl1Raw || ((verify >> 32) & 0x7FFF) != pl2Raw)
            throw new InvalidOperationException("BIOS отклонил новые Intel Turbo power limits.");
        return new IntelPowerState(verify, baseline.PowerUnit, pl1, pl2, baseline.MaximumWatts, false);
    }

    private static ulong ReadMsr(PawnIoBridge bridge, uint msr)
    {
        ulong[] value = bridge.Execute("ioctl_read_msr", new[] { (ulong)msr }, 1);
        if (value.Length != 1)
            throw new InvalidOperationException($"MSR 0x{msr:X} не вернул значение.");
        return value[0];
    }

    private static void WriteMsr(PawnIoBridge bridge, uint msr, ulong value) =>
        bridge.Execute("ioctl_write_msr", new[] { (ulong)msr, value }, 0);

    private void ResetToStock(CpuTuneSupport support)
    {
        switch (support.Backend)
        {
            case CpuTuneBackend.AmdPbo:
                using (PawnIoBridge bridge = OpenRyzenModule())
                {
                    AmdAdapter adapter = GetActiveAmdAdapter(bridge);
                    ApplyAmdProfile(bridge, adapter, GetAmdStockProfile(support.ProcessorName, adapter));
                }
                break;
            case CpuTuneBackend.IntelTurboPower:
                using (PawnIoBridge bridge = OpenIntelModule())
                {
                    _intelOriginalPowerLimit ??= ReadIntelPowerState(bridge).RawLimit;
                    WriteMsr(bridge, MsrPackagePowerLimit, _intelOriginalPowerLimit.Value);
                }
                break;
            default:
                throw new NotSupportedException("Безопасный аппаратный откат для процессора недоступен.");
        }
    }

    private void TryResetStock(CpuTuneSupport support)
    {
        try { if (support.Backend != CpuTuneBackend.None) ResetToStock(support); }
        catch { }
    }

    private static PawnIoBridge OpenRyzenModule() => OpenModule("RyzenSMU.bin", RyzenModuleSha256);
    private static PawnIoBridge OpenIntelModule() => OpenModule("IntelMSR.bin", IntelModuleSha256);

    private static PawnIoBridge OpenModule(string resource, string expectedSha256)
    {
        byte[] module = PawnIoBridge.ReadEmbeddedModule(resource);
        string hash = Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant();
        if (!hash.Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"Проверка целостности модуля {resource} не пройдена.");

        PawnIoBridge bridge = PawnIoBridge.Open();
        try
        {
            bridge.LoadModule(module);
            return bridge;
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    private static bool IsIntelMultiplierUnlocked(string name)
    {
        string upper = name.ToUpperInvariant();
        return Regex.IsMatch(upper, @"\b(?:I[3579]-?\d+|ULTRA\s+[3579]\s+\d+)(?:K|KF|KS|X|XE|HK|HX)\b") ||
               Regex.IsMatch(upper, @"\bXEON\b.*\bW-?\d+X\b");
    }

    private static string ReadProcessorName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Процессор не определён";
        }
        catch { return "Процессор не определён"; }
    }

    private sealed record AmdAdapter(int CodeName, string Id, string Generation, bool SupportsPbo, bool IsMobileFamily, byte PptCommand, byte TdcCommand, byte EdcCommand, byte IsOverclockableCommand);
    private sealed record CpuPboProfile(string Id, string Label, int PptWatts, int TdcAmps, int EdcAmps);
    private sealed record ProfileScale(string Id, string Label, double Factor);
    private sealed record IntelPowerState(ulong RawLimit, double PowerUnit, double Pl1Watts, double Pl2Watts, double MaximumWatts, bool IsLocked);
    private sealed record TestResult(double Score, double PeakTemperatureC, bool Stable);
}
