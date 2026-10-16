using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyborgroid;

public sealed class EngineConfig
{
    public string Server { get; set; } = "/opt/cyborgroid/engine/bin/llama-server";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;
    public int Context { get; set; } = 8192;
    public int Threads { get; set; } = 0;
    public int GpuLayers { get; set; } = 0;
    public List<string> ExtraArgs { get; set; } = new();
    public int StartupTimeoutSec { get; set; } = 300;
}

public sealed class ModelEntry
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public string Description { get; set; } = "";
}

public sealed class AssistantConfig
{
    public string Name { get; set; } = "Cyborgroid";
    public string SystemPrompt { get; set; } =
        "You are Cyborgroid, a personal AI assistant running fully offline on the user's own computer. " +
        "Be accurate, direct and helpful. Answer in the language the user writes in. " +
        "You have no internet access; when a question needs current information, say so plainly.";
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.8;
    public int MaxTokens { get; set; } = 4096;
    public bool Think { get; set; } = false;
    public bool ShowThinking { get; set; } = false;
}

public sealed class UpdateConfig
{
    /// <summary>Optional JSON manifest that can replace the model catalog and engine ref.</summary>
    public string? ManifestUrl { get; set; }
    public string EngineRepo { get; set; } = "https://github.com/ggml-org/llama.cpp.git";
    public string EngineRef { get; set; } = "master";
    /// <summary>Source checkout this install was built from; `update --app` rebuilds from it.</summary>
    public string? AppSource { get; set; }
}

public sealed class AppConfig
{
    public string Home { get; set; } = "/opt/cyborgroid";
    public string ModelsDir { get; set; } = "/opt/cyborgroid/models";
    public string Model { get; set; } = "qwen3-8b";
    public EngineConfig Engine { get; set; } = new();
    public AssistantConfig Assistant { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public List<ModelEntry> Catalog { get; set; } = DefaultCatalog();

    [JsonIgnore] public string SourcePath { get; private set; } = "";

    public static List<ModelEntry> DefaultCatalog() => new()
    {
        new()
        {
            Name = "qwen3-8b", File = "Qwen3-8B-Q4_K_M.gguf",
            Url = "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
            Description = "기본 모델 · 8B · ~5.0GB · 8GB VRAM GPU 권장",
        },
        new()
        {
            Name = "qwen3-14b", File = "Qwen3-14B-Q4_K_M.gguf",
            Url = "https://huggingface.co/Qwen/Qwen3-14B-GGUF/resolve/main/Qwen3-14B-Q4_K_M.gguf",
            Description = "고성능 · 14B · ~9.0GB · RAM 16GB 이상",
        },
        new()
        {
            Name = "qwen3-1.7b", File = "Qwen3-1.7B-Q8_0.gguf",
            Url = "https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q8_0.gguf",
            Description = "경량 · 1.7B · ~1.8GB · 저사양 PC",
        },
    };

    public ModelEntry? FindModel(string? name = null) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Name, name ?? Model, StringComparison.OrdinalIgnoreCase));

    public string ModelPath(ModelEntry m) => Path.Combine(ModelsDir, m.File);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CYBORGROID_CONFIG") is { Length: > 0 } p ? p : "/etc/cyborgroid/config.json";

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            throw new CyborgroidException($"설정 파일이 없습니다: {path}\n  설치: sudo ./install.sh");
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json)
                  ?? throw new CyborgroidException($"설정 파일을 읽을 수 없습니다: {path}");
        cfg.SourcePath = path;
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= SourcePath is { Length: > 0 } ? SourcePath : DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
        SourcePath = path;
    }

    /// <summary>Per-user state (chat sessions, engine log). Never needs root.</summary>
    public static string UserDataDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "cyborgroid");
        }
    }
}

public sealed class CyborgroidException(string message) : Exception(message);
