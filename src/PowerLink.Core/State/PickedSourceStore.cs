using System.Text.Json;

namespace PowerLink.Core.State;

public static class PickedSourceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string DefaultStateFilePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerLink",
            "picked.json");

    public static void Save(PickedSource picked, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(picked);

        var target = path ?? DefaultStateFilePath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(picked, JsonOptions);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(target))
            File.Replace(tmp, target, destinationBackupFileName: null);
        else
            File.Move(tmp, target);
    }

    public static PickedSource? TryLoad(string? path = null)
    {
        var source = path ?? DefaultStateFilePath;
        if (!File.Exists(source))
            return null;

        try
        {
            var json = File.ReadAllText(source);
            return JsonSerializer.Deserialize<PickedSource>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Clear(string? path = null)
    {
        var target = path ?? DefaultStateFilePath;
        if (File.Exists(target))
            File.Delete(target);
    }
}
