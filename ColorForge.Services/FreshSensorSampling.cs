using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace ColorForge.Services;

internal static class FreshSensorSampling
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo> Setters = new();
    public static void BeginUpdate(IEnumerable<IHardware> hardware)
    {
        foreach (IHardware device in hardware)
        {
            foreach (ISensor sensor in device.Sensors)
            {
                if (sensor.SensorType is not (SensorType.Temperature or SensorType.Load)) continue;
                // LHM 0.9.6 keeps the old Value when a driver silently fails. Its concrete
                // Sensor is internal, but exposes a public nullable Value setter. The pinned
                // adapter clears that cache before Update; a changed contract fails closed.
                PropertyInfo setter = Setters.GetOrAdd(sensor.GetType(), type =>
                {
                    PropertyInfo? property = type.GetProperty(nameof(ISensor.Value), BindingFlags.Instance | BindingFlags.Public);
                    if (type.Assembly != typeof(ISensor).Assembly || property?.SetMethod?.IsPublic != true || property.PropertyType != typeof(float?))
                        throw new NotSupportedException("Не удалось очистить кэш датчика для подтверждения свежей температуры.");
                    return property;
                });
                setter.SetValue(sensor, null);
            }
            BeginUpdate(device.SubHardware);
        }
    }
}
