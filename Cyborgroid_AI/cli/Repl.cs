using System.Text;

namespace Cyborgroid;

/// <summary>Interactive terminal chat.</summary>
internal sealed class Repl
{
    private readonly AppConfig _cfg;
    private readonly Engine _engine;
    private readonly ChatSession _session;
    private volatile bool _generating;

    public Repl(AppConfig cfg, Engine engine)
    {
        _cfg = cfg;
        _engine = engine;
        _session = new ChatSession(cfg);
    }

    public int Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (_generating)
            {
                e.Cancel = true;
                Engine.Cancel();
            }
            else
            {
                _engine.Dispose();
            }
        };

        Ui.Banner(_cfg);
        Console.Write(Ui.Dim("  엔진 준비 중… "));
        _engine.Start();
        Console.WriteLine(Ui.Green("완료"));
        Console.WriteLine();

        while (true)
        {
            var input = ReadInput();
            if (input is null) break;
            if (input.Length == 0) continue;
            if (input.StartsWith('/'))
            {
                if (!Command(input)) break;
                continue;
            }
            Send(input);
        }
        if (_session.Turns.Count > 0) _session.Save();
        Console.WriteLine(Ui.Dim("안녕히 가세요."));
        return 0;
    }

    private static string? ReadInput()
    {
        Console.Write(Ui.Bold(Ui.Green("you ❯ ")));
        var sb = new StringBuilder();
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null) return sb.Length > 0 ? sb.ToString() : null;
            if (line.EndsWith('\\'))
            {
                sb.Append(line, 0, line.Length - 1).Append('\n');
                Console.Write(Ui.Dim("    … "));
                continue;
            }
            sb.Append(line);
            return sb.ToString().Trim();
        }
    }

    private void Send(string text)
    {
        _session.Turns.Add(new ChatMessage("user", text));
        var answer = new StringBuilder();
        var thinkingShown = false;
        var inReasoning = false;

        Console.Write(Ui.Bold(Ui.Cyan($"{_cfg.Assistant.Name} ❯ ")));
        _generating = true;
        ChatResult result;
        try
        {
            result = _engine.Chat(_session.Prompt(), (kind, piece) =>
            {
                if (kind == PieceKind.Reasoning)
                {
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
                answer.Append(piece);
                Console.Write(piece);
            });
        }
        catch (CyborgroidException ex)
        {
            Console.WriteLine();
            Ui.Error(ex.Message);
            _session.Turns.RemoveAt(_session.Turns.Count - 1);
            return;
        }
        finally
        {
            _generating = false;
        }

        Console.WriteLine();
        if (result == ChatResult.Cancelled) Console.WriteLine(Ui.Dim("  (중단됨)"));
        if (answer.Length > 0)
            _session.Turns.Add(new ChatMessage("assistant", answer.ToString().Trim()));
        else
            _session.Turns.RemoveAt(_session.Turns.Count - 1);
        Console.WriteLine();
        _session.Save();
    }

    /// <summary>Handles a slash command. Returns false to exit.</summary>
    private bool Command(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit" or "/q":
                return false;
            case "/help" or "/?":
                Console.WriteLine("""
                      /clear            새 대화 시작
                      /think [on|off]   깊은 추론 모드 전환 (느리지만 더 정확)
                      /showthink        추론 과정 표시 전환
                      /system [텍스트]  시스템 프롬프트 보기/변경 (이번 실행에만)
                      /sessions         저장된 대화 목록
                      /load <id>        저장된 대화 불러오기
                      /status           엔진·모델·네트워크 상태
                      /exit             종료 (Ctrl+D)
                      줄 끝에 \ 를 붙이면 여러 줄 입력, 답변 중 Ctrl+C 로 중단
                    """);
                break;
            case "/clear":
                if (_session.Turns.Count > 0) _session.Save();
                _session.Clear();
                Ui.Ok("새 대화를 시작합니다.");
                break;
            case "/think":
                _cfg.Assistant.Think = arg switch { "on" => true, "off" => false, _ => !_cfg.Assistant.Think };
                Ui.Ok("추론 모드: " + (_cfg.Assistant.Think ? "켜짐" : "꺼짐"));
                break;
            case "/showthink":
                _cfg.Assistant.ShowThinking = !_cfg.Assistant.ShowThinking;
                Ui.Ok("추론 과정 표시: " + (_cfg.Assistant.ShowThinking ? "켜짐" : "꺼짐"));
                break;
            case "/system":
                if (arg.Length > 0)
                {
                    _cfg.Assistant.SystemPrompt = arg;
                    Ui.Ok("시스템 프롬프트를 변경했습니다.");
                }
                else Console.WriteLine(Ui.Dim(_cfg.Assistant.SystemPrompt));
                break;
            case "/sessions":
                foreach (var (id, when, preview) in ChatSession.List().Take(20))
                    Console.WriteLine($"  {Ui.Cyan(id)}  {Ui.Dim(when.ToString("MM-dd HH:mm"))}  {preview}");
                break;
            case "/load":
                try
                {
                    _session.Load(arg);
                    Ui.Ok($"불러옴: {_session.Id} ({_session.Turns.Count}개 메시지)");
                }
                catch (CyborgroidException ex) { Ui.Error(ex.Message); }
                break;
            case "/status":
                Commands.Status(_cfg);
                break;
            default:
                Ui.Warn($"알 수 없는 명령: {parts[0]}  (/help)");
                break;
        }
        return true;
    }
}
