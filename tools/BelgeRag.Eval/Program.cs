// Çalışan sisteme (docker compose) HTTP üzerinden karşı ölçüm yapar; hiçbir bileşeni taklit etmez.
//
//   dotnet run --project tools/BelgeRag.Eval -- --label hashing --api http://localhost:8081
//
// Adımlar: (1) eval/corpus/*.md belgelerini API'ye yükler ve worker indeksleyene kadar bekler,
// (2) her soruyu dört arama yapılandırmasıyla sorar (tam metin, vektör, hibrit, hibrit+rerank),
// (3) her soruyu SSE sohbet uç noktasına iki kez sorar (ikincisi yanıt önbelleğini ölçer).
// Sonuçlar eval/results/<label>.json dosyasına yazılır, özet tablo konsola basılır.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BelgeRag.Core.Text;

var opts = Args.Parse(args);
var root = FindRepoRoot();
var questions = JsonSerializer.Deserialize<List<EvalQuestion>>(
    File.ReadAllText(Path.Combine(root, "eval", "questions.json")), Json.Web)!;

using var http = new HttpClient { BaseAddress = new Uri(opts.Api), Timeout = TimeSpan.FromMinutes(5) };
var login = await (await http.PostAsJsonAsync("/api/auth/token", new { username = opts.User, password = opts.Password }))
    .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());

var report = new Dictionary<string, object> { ["label"] = opts.Label, ["questions"] = questions.Count };

// ---------- 1) Alım: kuyruk + worker uçtan uca ----------
if (!opts.SkipIngest)
{
    var files = Directory.GetFiles(Path.Combine(root, "eval", "corpus"), "*.md").Order().ToList();
    var sw = Stopwatch.StartNew();
    var pending = new Dictionary<string, string>();
    foreach (var f in files)
    {
        var res = await http.PostAsJsonAsync("/api/documents",
            new { title = Path.GetFileNameWithoutExtension(f), content = await File.ReadAllTextAsync(f) });
        var body = await res.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        pending[Path.GetFileNameWithoutExtension(f)] = body.GetProperty("statusUrl").GetString()!;
    }
    var enqueueMs = sw.Elapsed.TotalMilliseconds;
    int chunks = 0;
    foreach (var (title, url) in pending)
    {
        while (true)
        {
            var s = await http.GetFromJsonAsync<JsonElement>(url);
            var status = s.GetProperty("status").GetString();
            if (status == "indexed") { chunks += s.GetProperty("chunkCount").GetInt32(); break; }
            if (status == "failed") throw new Exception($"{title} indekslenemedi: {s.GetProperty("error")}");
            await Task.Delay(50);
        }
    }
    var totalMs = sw.Elapsed.TotalMilliseconds;
    var bytes = files.Sum(f => new FileInfo(f).Length);
    report["ingest"] = new
    {
        documents = files.Count, bytes, chunks,
        enqueueMs = Math.Round(enqueueMs, 1), endToEndMs = Math.Round(totalMs, 1),
        chunksPerSecond = Math.Round(chunks / (totalMs / 1000), 1),
    };
    Console.WriteLine($"Alım: {files.Count} belge, {bytes / 1024} KB, {chunks} parça, kuyruğa yazma {enqueueMs:F0} ms, " +
                      $"indekslenme dahil toplam {totalMs:F0} ms");
}

// ---------- 2) Arama ablasyonu ----------
var configs = new (string Name, string Mode, bool Rerank)[]
{
    ("fulltext", "FullText", false), ("vector", "Vector", false), ("hybrid", "Hybrid", false), ("hybrid+rerank", "Hybrid", true),
};
var retrieval = new Dictionary<string, object>();
var perQuestion = new List<object>();
Console.WriteLine();
Console.WriteLine("| Yapılandırma | Belge Hit@1 | Belge Hit@5 | MRR@10 | Kanıt@5 (tümü) | Kanıt@5 (aynı dil) | Kanıt@5 (çapraz dil) | p50 ms | p95 ms |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
foreach (var (name, mode, rerank) in configs)
{
    var rows = new List<(EvalQuestion Q, int DocRank, bool Evidence5, double Ms, double ServerMs)>();
    // Isınma: ilk istekteki JIT/bağlantı maliyeti ölçüme girmesin.
    await http.GetAsync($"/api/search?q=warmup&topK=3&mode={mode}&rerank={rerank}");
    foreach (var q in questions)
    {
        var sw = Stopwatch.StartNew();
        var r = await http.GetFromJsonAsync<JsonElement>(
            $"/api/search?q={Uri.EscapeDataString(q.Question)}&topK=10&mode={mode}&rerank={rerank}");
        var ms = sw.Elapsed.TotalMilliseconds;
        var hits = r.GetProperty("hits").EnumerateArray().ToList();
        var docRank = hits.FindIndex(h => h.GetProperty("documentTitle").GetString() == q.Doc) + 1;
        var evidence5 = hits.Take(5).Any(h => q.Evidence.Any(e => Contains(h.GetProperty("text").GetString()!, e)));
        rows.Add((q, docRank, evidence5, ms, r.GetProperty("timings").GetProperty("totalMs").GetDouble()));
    }
    double Rate(IEnumerable<bool> xs) { var l = xs.ToList(); return l.Count == 0 ? 0 : Math.Round((double)l.Count(x => x) / l.Count, 3); }
    var m = new
    {
        hit1 = Rate(rows.Select(x => x.DocRank == 1)),
        hit5 = Rate(rows.Select(x => x.DocRank is >= 1 and <= 5)),
        mrr10 = Math.Round(rows.Average(x => x.DocRank == 0 ? 0 : 1.0 / x.DocRank), 3),
        evidence5 = Rate(rows.Select(x => x.Evidence5)),
        evidence5SameLanguage = Rate(rows.Where(x => !x.Q.CrossLingual).Select(x => x.Evidence5)),
        evidence5CrossLingual = Rate(rows.Where(x => x.Q.CrossLingual).Select(x => x.Evidence5)),
        p50Ms = Percentile(rows.Select(x => x.Ms), 50),
        p95Ms = Percentile(rows.Select(x => x.Ms), 95),
        serverP50Ms = Percentile(rows.Select(x => x.ServerMs), 50),
    };
    retrieval[name] = m;
    perQuestion.AddRange(rows.Select(x => new { config = name, id = x.Q.Id, docRank = x.DocRank, evidence5 = x.Evidence5 }));
    Console.WriteLine($"| {name} | {m.hit1:P1} | {m.hit5:P1} | {m.mrr10:F3} | {m.evidence5:P1} | {m.evidence5SameLanguage:P1} | " +
                      $"{m.evidence5CrossLingual:P1} | {m.p50Ms:F1} | {m.p95Ms:F1} |");
}
report["retrieval"] = retrieval;
report["retrievalPerQuestion"] = perQuestion;

// ---------- 3) SSE sohbet: ilk token gecikmesi, doğrulayıcı, yanıt önbelleği ----------
if (!opts.SkipChat)
{
    var chatQuestions = opts.ChatLimit > 0 ? questions.Take(opts.ChatLimit).ToList() : questions;
    var runs = new List<ChatRun>();
    foreach (var pass in new[] { "cold", "cached" })
    {
        foreach (var q in chatQuestions)
        {
            var run = await Chat(http, q, pass);
            runs.Add(run);
            if (opts.Verbose) Console.WriteLine($"[{pass}] {q.Id}: {run.Verdict} ttft={run.TtftMs:F0}ms  {Trim(run.Answer, 140)}");
        }
    }
    var cold = runs.Where(r => r.Pass == "cold").ToList();
    var cached = runs.Where(r => r.Pass == "cached").ToList();
    object Summ(List<ChatRun> rs) => new
    {
        n = rs.Count,
        ttftP50Ms = Percentile(rs.Select(r => r.TtftMs), 50),
        ttftP95Ms = Percentile(rs.Select(r => r.TtftMs), 95),
        totalP50Ms = Percentile(rs.Select(r => r.TotalMs), 50),
        totalP95Ms = Percentile(rs.Select(r => r.TotalMs), 95),
        tokenEventsP50 = Percentile(rs.Select(r => (double)r.TokenEvents), 50),
        fromCache = rs.Count(r => r.FromCache),
        answerContainsEvidence = Math.Round((double)rs.Count(r => r.AnswerHasEvidence) / rs.Count, 3),
        verdicts = rs.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count()),
        promptTokensP50 = Percentile(rs.Select(r => (double)r.PromptTokens), 50),
    };
    report["chat"] = new { cold = Summ(cold), cached = Summ(cached) };
    report["chatRuns"] = runs;

    var c = JsonSerializer.SerializeToElement(report["chat"], Json.Web);
    Console.WriteLine();
    Console.WriteLine($"Sohbet ({cold.Count} soru): ilk token p50 {c.GetProperty("cold").GetProperty("ttftP50Ms")} ms / p95 " +
                      $"{c.GetProperty("cold").GetProperty("ttftP95Ms")} ms, toplam p50 {c.GetProperty("cold").GetProperty("totalP50Ms")} ms; " +
                      $"önbellekli tekrar: ilk token p50 {c.GetProperty("cached").GetProperty("ttftP50Ms")} ms, " +
                      $"{cached.Count(r => r.FromCache)}/{cached.Count} önbellekten");
    Console.WriteLine($"Yanıtta kanıt ifadesi: {cold.Count(r => r.AnswerHasEvidence)}/{cold.Count}; doğrulayıcı: " +
                      string.Join(", ", cold.GroupBy(r => r.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
}

var outDir = Path.Combine(root, "eval", "results");
Directory.CreateDirectory(outDir);
var outFile = Path.Combine(outDir, $"{opts.Label}.json");
await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(report, Json.Pretty));
Console.WriteLine($"\n→ {Path.GetRelativePath(root, outFile)}");
return;

static async Task<ChatRun> Chat(HttpClient http, EvalQuestion q, string pass)
{
    var sw = Stopwatch.StartNew();
    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
    {
        Content = JsonContent.Create(new { question = q.Question, topK = 5 }),
    };
    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    res.EnsureSuccessStatusCode();
    using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(), Encoding.UTF8);
    double ttft = -1;
    var answer = new StringBuilder();
    int tokens = 0;
    string verdict = "?";
    bool fromCache = false;
    int promptTokens = 0;
    string? evt = null;
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("event: ")) { evt = line[7..]; continue; }
        if (!line.StartsWith("data: ")) continue;
        var data = JsonDocument.Parse(line[6..]).RootElement;
        switch (evt)
        {
            case "token":
                if (ttft < 0) ttft = sw.Elapsed.TotalMilliseconds;
                tokens++;
                answer.Append(data.GetProperty("text").GetString());
                break;
            case "verdict":
                verdict = data.GetProperty("verdict").GetProperty("label").GetString()!;
                break;
            case "done":
                fromCache = data.GetProperty("metrics").GetProperty("answerFromCache").GetBoolean();
                promptTokens = data.GetProperty("metrics").GetProperty("promptTokens").GetInt32();
                break;
            case "error":
                verdict = "Error";
                break;
        }
    }
    var text = answer.ToString();
    return new ChatRun(q.Id, pass, Math.Round(ttft, 1), Math.Round(sw.Elapsed.TotalMilliseconds, 1), tokens, verdict, fromCache,
        promptTokens, q.Evidence.Any(e => Contains(text, e)), text);
}

static bool Contains(string text, string evidence) => Norm(text).Contains(Norm(evidence), StringComparison.Ordinal);

static string Norm(string s) => Regex.Replace(TextFolding.Fold(s.Replace("**", "")), @"\s+", " ");

static double Percentile(IEnumerable<double> xs, int p)
{
    var s = xs.Order().ToArray();
    if (s.Length == 0) return 0;
    var rank = p / 100.0 * (s.Length - 1);
    var lo = (int)Math.Floor(rank);
    var hi = (int)Math.Ceiling(rank);
    return Math.Round(s[lo] + (s[hi] - s[lo]) * (rank - lo), 1);
}

static string Trim(string s, int n) => s.Length <= n ? s.ReplaceLineEndings(" ") : s[..n].ReplaceLineEndings(" ") + "…";

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BelgeRag.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("BelgeRag.sln bulunamadı");
}

sealed record EvalQuestion(string Id, string Doc, bool CrossLingual, string Question, string[] Evidence);

sealed record ChatRun(string Id, string Pass, double TtftMs, double TotalMs, int TokenEvents, string Verdict, bool FromCache,
    int PromptTokens, bool AnswerHasEvidence, string Answer);

static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

sealed record Args(string Api, string Label, string User, string Password, bool SkipIngest, bool SkipChat, int ChatLimit, bool Verbose)
{
    public static Args Parse(string[] a)
    {
        string Get(string name, string def) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }
        return new Args(Get("--api", "http://localhost:8081"), Get("--label", "run"), Get("--user", "admin"),
            Get("--password", "admin-demo"), a.Contains("--skip-ingest"), a.Contains("--skip-chat"),
            int.Parse(Get("--chat-limit", "0")), a.Contains("--verbose"));
    }
}
