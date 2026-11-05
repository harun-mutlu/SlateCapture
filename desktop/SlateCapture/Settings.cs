using System.Text.Json;

namespace SlateCapture;

public sealed class Settings
{
    public int Level { get; set; }
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 8;
    public bool Audio { get; set; }
    public string Language { get; set; } = "en";
    public string Folder { get; set; } = DefaultFolder();

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool Portable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    public static string DataFolder => Portable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlateCapture");

    static string FilePath => Path.Combine(DataFolder, "settings.json");

    static string DefaultFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        return Path.Combine(videos, "SlateCapture");
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
