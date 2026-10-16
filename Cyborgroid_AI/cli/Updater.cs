using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cyborgroid;

public sealed class UpdateOptions
{
    public bool Check;
    public bool Engine;
    public bool App;
    public string? Model;
    public bool ModelRequested;
}

/// <summary>The only part of Cyborgroid that uses the network.</summary>
internal static class Updater
{
    private static readonly HttpClient Http = MakeClient(redirects: true);
    private static readonly HttpClient Probe = MakeClient(redirects: false);

    private static HttpClient MakeClient(bool redirects)
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"cyborgroid/{Program.Version}");
        return c;
    }

    public static async Task<int> Run(AppConfig cfg, UpdateOptions o)
    {
        if (!Native.NetworkOnline)
            throw new CyborgroidException(
                "업데이트에는 네트워크가 필요합니다. 와이파이/유선을 켠 뒤 다시 실행하세요.\n" +
                $"  인터페이스: {Native.NetworkDescription}");

        if (!o.Check) RequireWritable(cfg);

        await ApplyManifest(cfg, o.Check);

        var doModel = o.ModelRequested || (!o.Engine && !o.App);
        var failures = 0;
        if (doModel) failures += await UpdateModel(cfg, o.Model ?? cfg.Model, o.Check) ? 0 : 1;
        if (o.Engine) failures += await UpdateEngine(cfg, o.Check) ? 0 : 1;
        if (o.App) failures += UpdateApp(cfg, o.Check) ? 0 : 1;
        return failures == 0 ? 0 : 1;
    }

    private static void RequireWritable(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(cfg.ModelsDir);
            var probe = Path.Combine(cfg.ModelsDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            if (cfg.SourcePath.Length > 0)
                using (File.Open(cfg.SourcePath, FileMode.Open, FileAccess.ReadWrite)) { }
        }
        catch (UnauthorizedAccessException)
        {
            throw new CyborgroidException("관리자 권한이 필요합니다:  sudo cyborgroid update");
        }
    }

    private static async Task ApplyManifest(AppConfig cfg, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(cfg.Update.ManifestUrl)) return;
        Ui.Info($"매니페스트 확인: {cfg.Update.ManifestUrl}");
        var node = JsonNode.Parse(await Http.GetStringAsync(cfg.Update.ManifestUrl));
        if (node is null) return;
        var changed = false;
        if (node["catalog"] is JsonArray arr)
        {
            foreach (var m in arr.Deserialize<List<ModelEntry>>(AppConfig.Json) ?? new())
            {
                var existing = cfg.FindModel(m.Name);
                if (existing is not null) cfg.Catalog.Remove(existing);
                cfg.Catalog.Add(m);
                changed = true;
            }
        }
        if (node["engine_ref"]?.GetValue<string>() is { Length: > 0 } engineRef && engineRef != cfg.Update.EngineRef)
        {
            cfg.Update.EngineRef = engineRef;
            changed = true;
        }
        if (changed && !dryRun)
        {
            cfg.Save();
            Ui.Ok("모델 카탈로그를 갱신했습니다.");
        }
    }

    // ---- model -------------------------------------------------------------------------

    private static string Sidecar(string path) => path + ".sha256";

    private static string? VerifiedHash(string path)
    {
        var side = Sidecar(path);
        if (!File.Exists(path) || !File.Exists(side)) return null;
        var parts = File.ReadAllText(side).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && long.TryParse(parts[1], out var size) && size == new FileInfo(path).Length
            ? parts[0]
            : null;
    }

    private static async Task<(string? Sha, long Size)> RemoteInfo(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var res = await Probe.SendAsync(req);
        string? Header(string name) =>
            res.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault()?.Trim('"') : null;
        // Hugging Face exposes the LFS object's SHA-256 and size on the redirect response.
        var sha = Header("x-linked-etag");
        if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) sha = null;
        long.TryParse(Header("x-linked-size"), out var size);
        if (size == 0) size = res.Content.Headers.ContentLength ?? 0;
        if ((int)res.StatusCode >= 400) throw new CyborgroidException($"{url}: HTTP {(int)res.StatusCode}");
        return (sha?.ToLowerInvariant(), size);
    }

    private static async Task<bool> UpdateModel(AppConfig cfg, string name, bool check)
    {
        var entry = cfg.FindModel(name);
        if (entry is null)
        {
            Ui.Error($"카탈로그에 없는 모델: {name}  (cyborgroid models)");
            return false;
        }
        var path = cfg.ModelPath(entry);
        Ui.Info($"모델 {Ui.Bold(entry.Name)} 확인 중…");
        var (remoteSha, remoteSize) = await RemoteInfo(entry.Url);
        var expected = remoteSha ?? entry.Sha256?.ToLowerInvariant();
        var local = VerifiedHash(path);

        var upToDate = File.Exists(path) && (expected is not null
            ? local == expected
            : remoteSize > 0 && new FileInfo(path).Length == remoteSize);
        if (upToDate)
        {
            Ui.Ok($"{entry.Name}: 최신 상태입니다.");
            return true;
        }
        if (check)
        {
            Ui.Warn($"{entry.Name}: 업데이트 가능 ({(File.Exists(path) ? "새 버전" : "미설치")}, {Ui.Size(remoteSize)})");
            return true;
        }

        var part = path + ".part";
        await Download(entry.Url, part, entry.Name);

        Console.Write(Ui.Dim("  무결성 검증(SHA-256)… "));
        var actual = Native.Sha256File(part) ?? throw new CyborgroidException(Native.LastError);
        if (expected is not null && actual != expected)
        {
            File.Delete(part);
            Console.WriteLine(Ui.Red("실패"));
            Ui.Error($"해시 불일치: 기대 {expected}, 실제 {actual}. 파일을 삭제했습니다.");
            return false;
        }
        Console.WriteLine(Ui.Green(expected is null ? "완료 (원격 해시 없음)" : "일치"));

        File.Move(part, path, overwrite: true);
        File.WriteAllText(Sidecar(path), $"{actual} {new FileInfo(path).Length}\n");
        entry.Sha256 = actual;
        cfg.Save();
        Ui.Ok($"{entry.Name} 설치 완료: {path}");
        if (!string.Equals(cfg.Model, entry.Name, StringComparison.OrdinalIgnoreCase))
            Ui.Info($"기본 모델로 쓰려면:  sudo cyborgroid use {entry.Name}");
        return true;
    }

    private static async Task Download(string url, string part, string label)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return; // .part already complete
        res.EnsureSuccessStatusCode();
        if (res.StatusCode != HttpStatusCode.PartialContent) have = 0;
        if (have > 0) Ui.Info($"이어받기: {Ui.Size(have)}부터");

        var total = have + (res.Content.Headers.ContentLength ?? 0);
        await using var input = await res.Content.ReadAsStreamAsync();
        await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 20);
        var buf = new byte[1 << 20];
        var done = have;
        var sw = Stopwatch.StartNew();
        var lastDraw = TimeSpan.Zero;
        int n;
        while ((n = await input.ReadAsync(buf)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n));
            done += n;
            if (sw.Elapsed - lastDraw > TimeSpan.FromMilliseconds(200))
            {
                lastDraw = sw.Elapsed;
                Ui.Progress(label, done, total, (done - have) / Math.Max(0.001, sw.Elapsed.TotalSeconds));
            }
        }
        Ui.Progress(label, done, total, (done - have) / Math.Max(0.001, sw.Elapsed.TotalSeconds));
        Console.WriteLine();
    }

    // ---- engine ------------------------------------------------------------------------

    private static async Task<bool> UpdateEngine(AppConfig cfg, bool check)
    {
        var engineDir = Path.Combine(cfg.Home, "engine");
        var versionFile = Path.Combine(engineDir, "VERSION");
        var current = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "(없음)";
        var remote = (await Capture("git", "ls-remote", cfg.Update.EngineRepo, cfg.Update.EngineRef))
            .Split('\t', 2)[0].Trim();
        var latest = remote.Length >= 7 ? remote[..7] : remote;
        if (latest.Length > 0 && remote.StartsWith(current))
        {
            Ui.Ok($"엔진: 최신 상태입니다 ({current}).");
            return true;
        }
        if (check)
        {
            Ui.Warn($"엔진: 업데이트 가능 ({current} → {latest})");
            return true;
        }

        Ui.Info($"엔진 빌드: {current} → {latest} (몇 분 걸립니다)");
        var script = Path.Combine(cfg.Home, "scripts", "build-engine.sh");
        var rc = Run("bash", script, Path.Combine(cfg.Home, "src", "llama.cpp"), engineDir,
            cfg.Update.EngineRef, cfg.Update.EngineRepo);
        if (rc != 0)
        {
            Ui.Error("엔진 빌드 실패. 기존 엔진은 그대로 유지됩니다.");
            return false;
        }

        var backend = File.ReadAllText(Path.Combine(engineDir, "BACKEND")).Trim();
        cfg.Engine.GpuLayers = backend == "cpu" ? 0 : (cfg.Engine.GpuLayers == 0 ? 99 : cfg.Engine.GpuLayers);
        cfg.Save();
        Ui.Ok($"엔진 업데이트 완료 ({backend}).");
        return true;
    }

    // ---- app ---------------------------------------------------------------------------

    private static bool UpdateApp(AppConfig cfg, bool check)
    {
        var src = cfg.Update.AppSource;
        if (string.IsNullOrEmpty(src) || !File.Exists(Path.Combine(src, "install.sh")))
        {
            Ui.Error("앱 소스 경로를 모릅니다. 소스 폴더에서 다시 sudo ./install.sh 를 실행하세요.");
            return false;
        }
        // Run git as the owner of the checkout, not root.
        var owner = Environment.GetEnvironmentVariable("SUDO_USER");
        string[] Git(params string[] a) =>
            owner is { Length: > 0 } ? ["sudo", "-u", owner, "git", "-C", src, .. a] : ["git", "-C", src, .. a];

        if (Run(Git("fetch", "--quiet")) != 0) return false;
        var behind = CaptureSync(Git("rev-list", "--count", "HEAD..@{u}")).Trim();
        if (behind is "0" or "")
        {
            Ui.Ok("앱: 최신 상태입니다.");
            return true;
        }
        if (check)
        {
            Ui.Warn($"앱: {behind}개 커밋 업데이트 가능");
            return true;
        }
        if (Run(Git("pull", "--ff-only")) != 0) return false;
        return Run("bash", Path.Combine(src, "install.sh"), "--skip-engine") == 0;
    }

    // ---- process helpers ---------------------------------------------------------------

    private static int Run(params string[] argv)
    {
        var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string CaptureSync(params string[] argv)
    {
        var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private static Task<string> Capture(params string[] argv) => Task.Run(() => CaptureSync(argv));
}
