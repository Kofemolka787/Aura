using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace ColorForge.Services;

internal static class EmbeddedToolExtractor
{
    public static string Extract(string resourceSuffix, string fileName, string expectedSha256)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "Tools");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);

        if (File.Exists(path) && Hash(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            return path;

        Assembly assembly = typeof(EmbeddedToolExtractor).Assembly;
        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resource == null)
            throw new FileNotFoundException("Встроенный компонент AURA не найден: " + resourceSuffix);

        string temp = path + ".tmp";
        using (Stream input = assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Не удалось открыть ресурс: " + resource))
        using (FileStream output = File.Create(temp))
            input.CopyTo(output);

        if (!Hash(temp).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException("Проверка целостности встроенного компонента AURA не пройдена.");
        }

        File.Move(temp, path, true);
        return path;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
