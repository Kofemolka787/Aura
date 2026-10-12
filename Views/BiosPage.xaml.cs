using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ColorForge.Services;

namespace ColorForge.Views;

public partial class BiosPage : UserControl
{
    public BiosPage() => InitializeComponent();

    private void RestartToFirmware_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            "Компьютер немедленно перезагрузится в настройки UEFI.\n\nСохрани открытые файлы. Продолжить?",
            "AURA · Переход в UEFI",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 0")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            AppLogService.Error("Restart to UEFI failed.", ex);
            MessageBox.Show(ex.Message, "AURA · UEFI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
