using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlateCapture;

public enum FeedbackKind { Bug, Idea, Other }

public enum FeedbackResult { Sent, Queued, Failed }

public sealed record FeedbackItem(
    FeedbackKind Kind,
    string Message,
    string? Email,
    Dictionary<string, string> Details,
    string? Log,
    bool Technical,
    string Language,
    DateTime Time);

static class Feedback
{
    const string Hook = "V2FjZEJpVTRnRkt4c0dJVkVteUhRbkJndENRZWRLSlJPWGNkRkM1dVNGTWJlRl84QjVVTUdPaXVqWnhmVTM5VXJPU2svMDIzNTUwNDE1MjYzNjQ5NzY1MS9za29vaGJldy9pcGEvbW9jLmRyb2NzaWQvLzpzcHR0aA==";

    public const int MaxLength = 1500;
    const int HourlyLimit = 5;
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Queue<string> Events = new();
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    sealed class State
    {
        public string Id { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        public List<DateTime> Sent { get; set; } = [];
        public List<FeedbackItem> Pending { get; set; } = [];
    }

    static State? _state;
    static State Current => _state ??= Load();
    static string FilePath => Path.Combine(Settings.DataFolder, "feedback.json");

    public static string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    public static string OsName
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return RuntimeInformation.OSDescription;
            var build = Environment.OSVersion.Version.Build;
            return $"Windows {(build >= 22000 ? 11 : 10)} ({build})";
        }
    }

    public static void Note(string text)
    {
        lock (Events)
        {
            Events.Enqueue($"{DateTime.Now:HH:mm:ss} {text}");
            while (Events.Count > 8) Events.Dequeue();
        }
    }

    public static string RecentEvents()
    {
        lock (Events) return string.Join("\n", Events);
    }

    public static bool CanSend(out TimeSpan wait)
    {
        var now = DateTime.UtcNow;
        var sent = Current.Sent;
        sent.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        wait = TimeSpan.Zero;
        if (sent.Count > 0 && now - sent[^1] < MinGap)
            wait = MinGap - (now - sent[^1]);
        else if (sent.Count >= HourlyLimit)
            wait = sent[0].AddHours(1) - now;
        return wait <= TimeSpan.Zero;
    }

    public static async Task<FeedbackResult> Submit(FeedbackItem item)
    {
        await Gate.WaitAsync();
        try
        {
            Current.Sent.Add(DateTime.UtcNow);
            var result = await Post(item);
            if (result == Delivery.Retry)
            {
                Current.Pending.Add(item);
                Save();
                return FeedbackResult.Queued;
            }
            Save();
            return result == Delivery.Sent ? FeedbackResult.Sent : FeedbackResult.Failed;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task Flush()
    {
        await Gate.WaitAsync();
        try
        {
            while (Current.Pending.Count > 0)
            {
                if (await Post(Current.Pending[0]) == Delivery.Retry) break;
                Current.Pending.RemoveAt(0);
                Save();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    enum Delivery { Sent, Retry, Rejected }

    static async Task<Delivery> Post(FeedbackItem item)
    {
        try
        {
            using var content = new StringContent(Payload(item).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Url(), content);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return Delivery.Sent;
            return code == 429 || code >= 500 ? Delivery.Retry : Delivery.Rejected;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Delivery.Retry;
        }
    }

    static string Url()
    {
        var chars = Encoding.UTF8.GetString(Convert.FromBase64String(Hook)).ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    static JsonObject Payload(FeedbackItem item)
    {
        var (title, color) = item.Kind switch
        {
            FeedbackKind.Bug => ("🐞 Hata bildirimi", 0xF2555A),
            FeedbackKind.Idea => ("💡 Öneri", 0xF0B232),
            _ => ("💬 Diğer", 0x80848E),
        };

        var fields = new JsonArray();
        void Add(string name, string? value, bool inline = true)
        {
            if (!string.IsNullOrWhiteSpace(value))
                fields.Add(new JsonObject { ["name"] = name, ["value"] = Clip(value, 1000), ["inline"] = inline });
        }

        Add("E-posta", item.Email);
        foreach (var (name, value) in item.Details) Add(name, value);
        if (!string.IsNullOrWhiteSpace(item.Log))
            Add("Son bağlantı kaydı", $"```\n{Clip(item.Log, 900)}\n```", false);

        var footer = $"Dil: {item.Language} · Kimlik #{Current.Id}";
        if (!item.Technical) footer += " · Teknik bilgi eklenmedi";

        var embed = new JsonObject
        {
            ["title"] = title,
            ["description"] = Clip(item.Message, MaxLength),
            ["color"] = color,
            ["fields"] = fields,
            ["footer"] = new JsonObject { ["text"] = footer },
            ["timestamp"] = item.Time.ToUniversalTime().ToString("o"),
        };

        return new JsonObject
        {
            ["username"] = "SlateCapture",
            ["embeds"] = new JsonArray(embed),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
    }

    static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    static State Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new State();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
