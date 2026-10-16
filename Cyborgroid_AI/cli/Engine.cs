using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Cyborgroid;

public enum PieceKind { Content = 0, Reasoning = 1, ToolCall = 2 }

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

    /// <summary>
    /// Streams one model reply. Text goes to <paramref name="onPiece"/>; tool calls the model
    /// asks for are added to <paramref name="toolCalls"/> when the reply ends.
    /// </summary>
    public ChatResult Chat(IEnumerable<ChatMessage> messages, Action<PieceKind, string> onPiece,
        JsonArray? tools = null, List<ToolCall>? toolCalls = null)
    {
        var a = _cfg.Assistant;
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray()),
            ["stream"] = true,
            ["temperature"] = a.Think ? Math.Min(a.Temperature, 0.6) : a.Temperature,
            ["top_p"] = a.Think ? 0.95 : a.TopP,
            ["max_tokens"] = a.MaxTokens,
            ["cache_prompt"] = true,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = a.Think },
        };
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();

        Action<PieceKind, string> sink = (kind, piece) =>
        {
            if (kind != PieceKind.ToolCall)
            {
                onPiece(kind, piece);
                return;
            }
            var f = piece.Split('\x1f', 3);
            if (f.Length < 3) return;
            toolCalls?.Add(new ToolCall(f[0].Length > 0 ? f[0] : $"call_{Guid.NewGuid():N}"[..13], f[1], f[2]));
        };
        var handle = GCHandle.Alloc(sink);
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

    private static JsonNode ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }).ToArray());
        return o;
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
