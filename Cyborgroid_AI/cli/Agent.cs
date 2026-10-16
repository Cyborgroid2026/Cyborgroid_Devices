using System.Text;

namespace Cyborgroid;

/// <summary>
/// Handles one user message as an agent loop: the model replies, may call tools,
/// sees their results, and repeats until it answers without calling a tool.
/// </summary>
internal sealed class Agent
{
    private readonly AppConfig _cfg;
    private readonly Engine _engine;
    private readonly Tools _tools;
    private readonly TextWriter _log;
    private readonly bool _interactive;
    private volatile bool _generating;
    private volatile bool _cancelled;
    private volatile CancellationTokenSource? _toolCts;

    public ChatSession Session { get; }

    /// <param name="interactive">REPL output; otherwise only the answer goes to stdout and activity to stderr.</param>
    public Agent(AppConfig cfg, Engine engine, ChatSession session, bool interactive)
    {
        _cfg = cfg;
        _engine = engine;
        Session = session;
        _interactive = interactive;
        _log = interactive ? Console.Out : Console.Error;
        _tools = new Tools(_log);
    }

    /// <summary>Ctrl+C handler. Returns true if a reply or command was running and is now stopping.</summary>
    public bool Interrupt()
    {
        if (_generating)
        {
            _cancelled = true;
            Engine.Cancel();
            return true;
        }
        if (_toolCts is { } cts)
        {
            _cancelled = true;
            cts.Cancel();
            return true;
        }
        return false;
    }

    public void Send(string text)
    {
        var start = Session.Turns.Count;
        Session.Turns.Add(new ChatMessage("user", text));
        _cancelled = false;
        var tools = _cfg.Assistant.Tools ? Tools.Definitions() : null;
        var reserve = tools?.ToJsonString().Length ?? 0;

        for (var step = 0; step < Math.Max(1, _cfg.Assistant.MaxSteps); step++)
        {
            var calls = new List<ToolCall>();
            var answer = new StringBuilder();
            ChatResult result;
            _generating = true;
            try
            {
                result = _engine.Chat(Session.Prompt(reserve), Renderer(answer), tools, calls);
            }
            catch (CyborgroidException ex)
            {
                if (_interactive) Console.WriteLine();
                Ui.Error(ex.Message);
                // Drop the request only if nothing happened yet; otherwise keep the tool history.
                if (step == 0) Session.Turns.RemoveRange(start, Session.Turns.Count - start);
                return;
            }
            finally
            {
                _generating = false;
            }

            var content = answer.ToString().Trim();
            if (_interactive || content.Length > 0) Console.WriteLine();

            if (result == ChatResult.Cancelled || calls.Count == 0)
            {
                if (result == ChatResult.Cancelled) _log.WriteLine(Ui.Dim("  (중단됨)"));
                if (content.Length > 0) Session.Turns.Add(new ChatMessage("assistant", content));
                else if (step == 0) Session.Turns.RemoveRange(start, Session.Turns.Count - start);
                if (_interactive) Console.WriteLine();
                return;
            }

            // Every tool call must get an answer, even when the user stops midway.
            Session.Turns.Add(new ChatMessage("assistant", content, calls));
            foreach (var call in calls)
            {
                string output;
                if (_cancelled)
                {
                    output = "Cancelled by the user.";
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    _toolCts = cts;
                    try { output = _tools.Execute(call, cts.Token); }
                    finally { _toolCts = null; }
                }
                Session.Turns.Add(new ChatMessage("tool", output, null, call.Id));
            }
            if (_cancelled)
            {
                _log.WriteLine(Ui.Dim("  (중단됨)"));
                return;
            }
        }
        Ui.Warn($"한 요청에 허용된 단계({_cfg.Assistant.MaxSteps})를 모두 썼습니다. 계속하려면 \"계속\"이라고 입력하세요.");
    }

    /// <summary>Prints the streamed reply: answer text always, reasoning only when asked for.</summary>
    private Action<PieceKind, string> Renderer(StringBuilder answer)
    {
        var thinkingShown = false;
        var inReasoning = false;

        if (_interactive) Console.Write(Ui.Bold(Ui.Cyan($"{_cfg.Assistant.Name} ❯ ")));
        return (kind, piece) =>
        {
            if (kind == PieceKind.Reasoning)
            {
                if (!_interactive) return;
                if (_cfg.Assistant.ShowThinking)
                {
                    if (!inReasoning) Console.Write(Ui.Dim("\n  [생각] "));
                    inReasoning = true;
                    Console.Write(Ui.Dim(piece));
                }
                else if (!thinkingShown)
                {
                    Console.Write(Ui.Dim("(생각 중…) "));
                    thinkingShown = true;
                }
                return;
            }
            if (inReasoning)
            {
                Console.Write("\n\n");
                inReasoning = false;
            }
            // Leading blank lines look odd right after the name prompt.
            if (answer.Length == 0) piece = piece.TrimStart('\n', '\r');
            if (piece.Length == 0) return;
            answer.Append(piece);
            Console.Write(piece);
        };
    }
}
