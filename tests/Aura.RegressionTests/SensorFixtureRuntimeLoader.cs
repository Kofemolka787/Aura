using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class SensorFixtureRuntimeLoader
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            // NuGet intentionally has no Linux runtime asset for LHM. Load its managed
            // Sensor implementation explicitly for cache-only fixtures; never open Computer.
            string path = Path.Combine(AppContext.BaseDirectory, "SensorFixtureRuntime", "LibreHardwareMonitorLib.dll");
            AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
    }
}
