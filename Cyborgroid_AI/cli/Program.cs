using System.Reflection;
using System.Text;

namespace Cyborgroid;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private const string Usage = """
        Cyborgroid — 오프라인 로컬 Personal AI

        사용법:
          cyborgroid                      대화 시작 (네트워크 불필요)
          cyborgroid ask <질문>           한 번 묻고 답 받기 (파이프 입력도 가능)
          cyborgroid status               엔진·모델·네트워크 상태
          cyborgroid models               모델 목록
          cyborgroid sessions             저장된 대화 목록
          sudo cyborgroid use <모델>      기본 모델 변경
          sudo cyborgroid update          모델 업데이트 (네트워크 필요)
               --model <이름>            특정 모델 다운로드/업데이트
               --engine                  추론 엔진(llama.cpp) 재빌드
               --app                     Cyborgroid 자체 업데이트
               --all                     전부
               --check                   확인만 하고 설치하지 않음
          cyborgroid version
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        try
        {
            if (!Native.IsAvailable())
                throw new CyborgroidException("Rust 코어(libcyborgroid_core.so)를 찾을 수 없습니다. sudo ./install.sh 로 재설치하세요.");

            var cmd = args.Length > 0 ? args[0] : "";
            var rest = args.Skip(1).ToArray();
            switch (cmd)
            {
                case "" when Console.IsInputRedirected:
                    return Commands.Ask(AppConfig.Load(), Console.In.ReadToEnd());
                case "" or "chat":
                {
                    var cfg = AppConfig.Load();
                    using var engine = new Engine(cfg);
                    return new Repl(cfg, engine).Run();
                }
                case "ask":
                {
                    var question = string.Join(' ', rest);
                    if (Console.IsInputRedirected) question = (question + "\n\n" + Console.In.ReadToEnd()).Trim();
                    if (question.Length == 0) throw new CyborgroidException("질문을 입력하세요: cyborgroid ask \"...\"");
                    return Commands.Ask(AppConfig.Load(), question);
                }
                case "update":
                    return await Updater.Run(AppConfig.Load(), ParseUpdate(rest));
                case "status":
                    Commands.Status(AppConfig.Load());
                    return 0;
                case "models":
                    Commands.Models(AppConfig.Load());
                    return 0;
                case "use":
                    return Commands.Use(AppConfig.Load(), rest.FirstOrDefault());
                case "sessions":
                    foreach (var (id, when, preview) in ChatSession.List())
                        Console.WriteLine($"{Ui.Cyan(id)}  {Ui.Dim(when.ToString("yyyy-MM-dd HH:mm"))}  {preview}");
                    return 0;
                case "init-config":
                    return Commands.InitConfig(rest);
                case "version" or "--version" or "-v":
                    Console.WriteLine($"cyborgroid {Version} (core {Native.Version})");
                    return 0;
                case "help" or "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Ui.Error($"알 수 없는 명령: {cmd}");
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
        catch (CyborgroidException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Ui.Error("네트워크 오류: " + ex.Message);
            return 1;
        }
    }

    private static UpdateOptions ParseUpdate(string[] args)
    {
        var o = new UpdateOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check": o.Check = true; break;
                case "--engine": o.Engine = true; break;
                case "--app": o.App = true; break;
                case "--all": o.Engine = o.App = o.ModelRequested = true; break;
                case "--model":
                    o.ModelRequested = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.Model = args[++i];
                    break;
                default: throw new CyborgroidException($"알 수 없는 옵션: {args[i]}");
            }
        }
        return o;
    }
}

internal static class Commands
{
    public static int Ask(AppConfig cfg, string question)
    {
        using var engine = new Engine(cfg);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Engine.Cancel(); };
        engine.Start();
        var session = new ChatSession(cfg);
        session.Turns.Add(new ChatMessage("user", question.Trim()));
        engine.Chat(session.Prompt(), (kind, piece) =>
        {
            if (kind == PieceKind.Content) Console.Write(piece);
        });
        Console.WriteLine();
        return 0;
    }

    public static void Status(AppConfig cfg)
    {
        var e = cfg.Engine;
        var model = cfg.FindModel();
        var modelPath = model is null ? null : cfg.ModelPath(model);
        var engineDir = Path.Combine(cfg.Home, "engine");
        string Read(string f) => File.Exists(Path.Combine(engineDir, f)) ? File.ReadAllText(Path.Combine(engineDir, f)).Trim() : "?";
        string Mark(bool ok) => ok ? Ui.Green("✔") : Ui.Red("✖");

        Console.WriteLine($"  버전        cyborgroid {Program.Version} · core {Native.Version}");
        Console.WriteLine($"  설정        {cfg.SourcePath}");
        Console.WriteLine($"  엔진     {Mark(File.Exists(e.Server))} {e.Server}  [{Read("BACKEND")} · {Read("VERSION")}]");
        Console.WriteLine($"  모델     {Mark(modelPath is not null && File.Exists(modelPath))} {cfg.Model}  {Ui.Dim(modelPath ?? "(카탈로그에 없음)")}");
        Console.WriteLine($"  실행 중  {Mark(Native.cy_engine_health(e.Host, (ushort)e.Port) == 200)} {e.Host}:{e.Port} · ctx {e.Context} · GPU layers {e.GpuLayers}");
        Console.WriteLine($"  네트워크    {(Native.NetworkOnline ? Ui.Yellow("온라인") : Ui.Green("오프라인"))} {Ui.Dim(Native.NetworkDescription)}");
        Console.WriteLine($"  사용자 데이터 {AppConfig.UserDataDir}");
    }

    public static void Models(AppConfig cfg)
    {
        foreach (var m in cfg.Catalog)
        {
            var installed = File.Exists(cfg.ModelPath(m));
            var current = string.Equals(m.Name, cfg.Model, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {(current ? Ui.Green("●") : " ")} {Ui.Bold(m.Name),-14} " +
                              $"{(installed ? Ui.Green("설치됨") : Ui.Dim("미설치"))}  {Ui.Dim(m.Description)}");
        }
        Console.WriteLine(Ui.Dim("\n  설치: sudo cyborgroid update --model <이름> · 변경: sudo cyborgroid use <이름>"));
    }

    public static int Use(AppConfig cfg, string? name)
    {
        var m = cfg.FindModel(name) ?? throw new CyborgroidException($"카탈로그에 없는 모델: {name}  (cyborgroid models)");
        if (!File.Exists(cfg.ModelPath(m)))
            Ui.Warn($"{m.Name} 은(는) 아직 설치되지 않았습니다: sudo cyborgroid update --model {m.Name}");
        cfg.Model = m.Name;
        try { cfg.Save(); }
        catch (UnauthorizedAccessException) { throw new CyborgroidException("관리자 권한이 필요합니다: sudo cyborgroid use " + m.Name); }
        Ui.Ok($"기본 모델: {m.Name}");
        return 0;
    }

    /// <summary>Writes a fresh config; used by install.sh.</summary>
    public static int InitConfig(string[] args)
    {
        if (args.Length == 0) throw new CyborgroidException("usage: init-config <path> [--home DIR] [--gpu-layers N] [--app-source DIR]");
        var cfg = new AppConfig();
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--home":
                    cfg.Home = args[i + 1];
                    cfg.ModelsDir = Path.Combine(cfg.Home, "models");
                    cfg.Engine.Server = Path.Combine(cfg.Home, "engine", "bin", "llama-server");
                    break;
                case "--gpu-layers": cfg.Engine.GpuLayers = int.Parse(args[i + 1]); break;
                case "--app-source": cfg.Update.AppSource = args[i + 1]; break;
                case "--model": cfg.Model = args[i + 1]; break;
                default: throw new CyborgroidException($"알 수 없는 옵션: {args[i]}");
            }
        }
        cfg.Save(args[0]);
        Ui.Ok($"설정 파일 생성: {args[0]}");
        return 0;
    }
}
