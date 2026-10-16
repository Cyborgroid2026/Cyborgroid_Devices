using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Cyborgroid;

public enum PieceKind { Content = 0, Reasoning = 1 }

public enum ChatResult { Complete, Cancelled }

/// <summary>Drives the local inference engine through the Rust core.</summary>
public sealed unsafe class Engine : IDisposable
{
    private readonly AppConfig _cfg;
    private bool _started;

    public Engine(AppConfig cfg) => _cfg = cfg;

    public static string LogPath => Path.Combine(AppConfig.UserDataDir, "engine.log");

    public bool IsHealthy => Native.cy_engine_health(_cfg.Engine.Host, (ushort)_cfg.Engine.Port) == 200;

    /// <summary>Starts (or reuses) the engine with the configured model.</summary>
    public void Start()
    {
        var model = _cfg.FindModel()
            ?? throw new CyborgroidException($"카탈로그에 없는 모델입니다: {_cfg.Model}");
        var path = _cfg.ModelPath(model);
        if (!File.Exists(path))
            throw new CyborgroidException($"모델 파일이 없습니다: {path}\n  다운로드: sudo cyborgroid update --model {model.Name}");

        var e = _cfg.Engine;
        var rc = Native.cy_engine_start(e.Server, path, e.Host, (ushort)e.Port, (uint)e.Context,
            (uint)Math.Max(0, e.Threads), e.GpuLayers, LogPath, string.Join('\n', e.ExtraArgs),
            (uint)e.StartupTimeoutSec);
        if (rc < 0) throw new CyborgroidException("엔진 시작 실패: " + Native.LastError);
        _started = rc == 1;
    }

    public ChatResult Chat(IEnumerable<ChatMessage> messages, Action<PieceKind, string> onPiece)
    {
        var a = _cfg.Assistant;
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            }).ToArray()),
            ["stream"] = true,
            ["temperature"] = a.Think ? Math.Min(a.Temperature, 0.6) : a.Temperature,
            ["top_p"] = a.Think ? 0.95 : a.TopP,
            ["max_tokens"] = a.MaxTokens,
            ["cache_prompt"] = true,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = a.Think },
        };

        var handle = GCHandle.Alloc(onPiece);
        try
        {
            var rc = Native.cy_chat_stream(_cfg.Engine.Host, (ushort)_cfg.Engine.Port,
                body.ToJsonString(), &OnPiece, GCHandle.ToIntPtr(handle));
            return rc switch
            {
                0 => ChatResult.Complete,
                1 => ChatResult.Cancelled,
                _ => throw new CyborgroidException(Native.LastError),
            };
        }
        finally
        {
            handle.Free();
        }
    }

    public static void Cancel() => Native.cy_cancel();

    [UnmanagedCallersOnly]
    private static void OnPiece(int kind, byte* text, nint user)
    {
        try
        {
            var sink = (Action<PieceKind, string>)GCHandle.FromIntPtr(user).Target!;
            sink((PieceKind)kind, Marshal.PtrToStringUTF8((nint)text) ?? "");
        }
        catch
        {
            // Never let an exception unwind into Rust.
        }
    }

    public void Dispose()
    {
        if (_started) Native.cy_engine_stop();
        _started = false;
    }
}
