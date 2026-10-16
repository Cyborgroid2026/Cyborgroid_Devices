using System.Text;

namespace Cyborgroid;

/// <summary>Interactive terminal chat.</summary>
internal sealed class Repl
{
    private readonly AppConfig _cfg;
    private readonly Engine _engine;
    private readonly ChatSession _session;
    private readonly Agent _agent;

    public Repl(AppConfig cfg, Engine engine)
    {
        _cfg = cfg;
        _engine = engine;
        _session = new ChatSession(cfg);
        _agent = new Agent(cfg, engine, _session, interactive: true);
    }

    public int Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (_agent.Interrupt())
            {
                e.Cancel = true;
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
            _agent.Send(input);
            _session.Save();
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
                      /tools [on|off]   에이전트 도구(파일 읽기·수정, 명령 실행) 전환
                      /system [텍스트]  시스템 프롬프트 보기/변경 (이번 실행에만)
                      /system save      현재 프롬프트를 내 기본값으로 저장 (sudo 불필요)
                      /system reset     내 프롬프트를 지우고 기본값으로 복귀
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
            case "/tools":
                _cfg.Assistant.Tools = arg switch { "on" => true, "off" => false, _ => !_cfg.Assistant.Tools };
                Ui.Ok("에이전트 도구: " + (_cfg.Assistant.Tools ? $"켜짐 (작업 폴더 {Tools.WorkDir})" : "꺼짐 (대화만)"));
                break;
            case "/system":
                SystemCommand(arg);
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

    private void SystemCommand(string arg)
    {
        var a = _cfg.Assistant;
        var path = AppConfig.UserPromptPath;
        switch (arg)
        {
            case "":
                Console.WriteLine(Ui.Dim(a.UserSystemPrompt is null ? "[기본 프롬프트]" : $"[내 프롬프트: {path}]"));
                Console.WriteLine(Ui.Dim(a.EffectiveSystemPrompt));
                break;
            case "save":
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, a.EffectiveSystemPrompt + "\n");
                Ui.Ok($"저장했습니다: {path}");
                break;
            case "reset":
                if (File.Exists(path)) File.Delete(path);
                a.UserSystemPrompt = null;
                Ui.Ok("기본 프롬프트로 돌아갔습니다.");
                break;
            default:
                a.UserSystemPrompt = arg;
                Ui.Ok("시스템 프롬프트를 변경했습니다. 계속 쓰려면 /system save");
                break;
        }
    }
}
