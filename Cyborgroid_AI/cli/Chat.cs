using System.Text.Json;

namespace Cyborgroid;

public sealed record ChatMessage(string Role, string Content);

/// <summary>A conversation: system prompt + turns, trimmed to fit the context window.</summary>
public sealed class ChatSession
{
    private readonly AppConfig _cfg;
    public List<ChatMessage> Turns { get; } = new();
    public string Id { get; private set; } = NewId();

    public ChatSession(AppConfig cfg) => _cfg = cfg;

    private static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public static string SessionsDir => Path.Combine(AppConfig.UserDataDir, "sessions");

    public IEnumerable<ChatMessage> Prompt()
    {
        var system = _cfg.Assistant.SystemPrompt +
                     $"\nCurrent local date and time: {DateTime.Now:yyyy-MM-dd HH:mm (ddd)}.";
        // Rough budget: ~3 chars per token, leaving room for the reply.
        var budget = Math.Max(2000, (_cfg.Engine.Context - _cfg.Assistant.MaxTokens) * 3);
        var kept = new List<ChatMessage>();
        var used = system.Length;
        for (var i = Turns.Count - 1; i >= 0; i--)
        {
            used += Turns[i].Content.Length;
            if (used > budget && kept.Count > 0) break;
            kept.Insert(0, Turns[i]);
        }
        // A conversation must not start with an assistant turn.
        while (kept.Count > 1 && kept[0].Role == "assistant") kept.RemoveAt(0);
        return kept.Prepend(new ChatMessage("system", system));
    }

    public void Clear()
    {
        Turns.Clear();
        Id = NewId();
    }

    public string Save()
    {
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(Turns, AppConfig.Json));
        return path;
    }

    public void Load(string id)
    {
        var path = Path.Combine(SessionsDir, id.EndsWith(".json") ? id : id + ".json");
        if (!File.Exists(path)) throw new CyborgroidException($"세션이 없습니다: {id}");
        var turns = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(path), AppConfig.Json) ?? new();
        Turns.Clear();
        Turns.AddRange(turns);
        Id = Path.GetFileNameWithoutExtension(path);
    }

    public static IEnumerable<(string Id, DateTime When, string Preview)> List()
    {
        if (!Directory.Exists(SessionsDir)) yield break;
        foreach (var f in Directory.GetFiles(SessionsDir, "*.json").OrderByDescending(f => f))
        {
            string preview;
            try
            {
                var turns = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(f), AppConfig.Json);
                preview = turns?.FirstOrDefault(t => t.Role == "user")?.Content ?? "";
            }
            catch (JsonException) { preview = "(읽을 수 없음)"; }
            preview = preview.ReplaceLineEndings(" ");
            yield return (Path.GetFileNameWithoutExtension(f), File.GetLastWriteTime(f),
                preview.Length > 60 ? preview[..60] + "…" : preview);
        }
    }
}
