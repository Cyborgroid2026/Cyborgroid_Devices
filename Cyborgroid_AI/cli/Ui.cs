namespace Cyborgroid;

internal static class Ui
{
    private static readonly bool Color =
        !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Wrap(string code, string s) => Color ? $"\e[{code}m{s}\e[0m" : s;
    public static string Bold(string s) => Wrap("1", s);
    public static string Dim(string s) => Wrap("2", s);
    public static string Cyan(string s) => Wrap("36", s);
    public static string Green(string s) => Wrap("32", s);
    public static string Yellow(string s) => Wrap("33", s);
    public static string Red(string s) => Wrap("31", s);

    public static void Error(string msg) => Console.Error.WriteLine(Red("✖ ") + msg);
    public static void Info(string msg) => Console.WriteLine(Cyan("• ") + msg);
    public static void Ok(string msg) => Console.WriteLine(Green("✔ ") + msg);
    public static void Warn(string msg) => Console.WriteLine(Yellow("! ") + msg);

    public static void Banner(AppConfig cfg)
    {
        Console.WriteLine(Cyan(Bold("""
              ______      __                                _     __
             / ____/_  __/ /_  ____  _________ __________  (_)___/ /
            / /   / / / / __ \/ __ \/ ___/ __ `/ ___/ __ \/ / __  /
           / /___/ /_/ / /_/ / /_/ / /  / /_/ / /  / /_/ / / /_/ /
           \____/\__, /_.___/\____/_/   \__, /_/   \____/_/\__,_/
                /____/                 /____/
           """)));
        var net = Native.NetworkOnline ? Yellow("온라인 (대화에는 사용 안 함)") : Green("오프라인 ✓");
        Console.WriteLine(Dim($"  모델 {cfg.Model} · 네트워크 {net}") + Dim(" · /help 도움말 · /exit 종료"));
        if (cfg.Assistant.Tools)
            Console.WriteLine(Dim($"  에이전트 모드 · 작업 폴더 {Tools.WorkDir} · 파일 수정·명령 실행 전에 묻습니다 (/tools off 로 끄기)"));
        Console.WriteLine();
    }

    public static void Progress(string label, long done, long total, double bytesPerSec)
    {
        if (Console.IsOutputRedirected) return;
        var pct = total > 0 ? (double)done / total : 0;
        const int width = 30;
        var filled = (int)(pct * width);
        var bar = new string('█', filled) + new string('░', width - filled);
        var eta = bytesPerSec > 0 && total > 0 ? TimeSpan.FromSeconds((total - done) / bytesPerSec) : TimeSpan.Zero;
        Console.Write($"\r  {label} {bar} {pct,6:P1}  {Size(done)}/{Size(total)}  {Size((long)bytesPerSec)}/s  ETA {eta:hh\\:mm\\:ss}   ");
    }

    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00}GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0}MB",
        >= 1L << 10 => $"{b / 1024.0:0}KB",
        _ => $"{b}B",
    };
}
