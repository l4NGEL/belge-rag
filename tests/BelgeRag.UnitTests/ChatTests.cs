using BelgeRag.Core.Agents;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Llm;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace BelgeRag.UnitTests;

public class ContextBudgeterTests
{
    private readonly TiktokenTokenCounter _tokens = new();

    private static SearchHit Hit(int i, int words) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"belge{i}", 0, string.Join(' ', Enumerable.Repeat($"bilgi{i}", words)), 1.0 / (i + 1));

    [Fact]
    public void PromptNeverExceedsWindowMinusAnswerReserve()
    {
        var opts = new TokenBudgetOptions { ContextWindowTokens = 1024, ReservedForAnswer = 256 };
        var hits = Enumerable.Range(0, 10).Select(i => Hit(i, 120)).ToList();
        var ctx = new ContextBudgeter(_tokens, opts).Fit(PromptBuilder.AnswerInstructions, "soru nedir", hits, []);

        var prompt = PromptBuilder.AnswerInstructions + PromptBuilder.BuildUserMessage("soru nedir", ctx.Sources);
        Assert.True(_tokens.Count(prompt) <= opts.ContextWindowTokens - opts.ReservedForAnswer);
        Assert.True(ctx.DroppedChunks + ctx.TruncatedChunks > 0);
        Assert.Equal(hits[0].ChunkId, ctx.Sources[0].ChunkId); // sıralama korunur
    }

    [Fact]
    public void ChunkThatPartlyFits_IsTruncatedNotDropped()
    {
        var opts = new TokenBudgetOptions { ContextWindowTokens = 700, ReservedForAnswer = 100, MinChunkTokens = 20 };
        var ctx = new ContextBudgeter(_tokens, opts).Fit("sistem", "soru", [Hit(0, 200), Hit(1, 400)], []);
        Assert.Equal(2, ctx.Sources.Count);
        Assert.Equal(1, ctx.TruncatedChunks);
        Assert.EndsWith("…", ctx.Sources[1].Text);
    }

    [Fact]
    public void History_KeepsNewestTurns_WithinShare()
    {
        var opts = new TokenBudgetOptions { ContextWindowTokens = 1200, ReservedForAnswer = 200, MaxHistoryShare = 0.25 };
        var history = Enumerable.Range(0, 12)
            .Select(i => new ChatTurn($"soru {i}", string.Join(' ', Enumerable.Repeat($"yanit{i}", 30)), DateTimeOffset.UtcNow))
            .ToList();
        var ctx = new ContextBudgeter(_tokens, opts).Fit("sistem", "yeni soru", [], history);

        Assert.InRange(ctx.History.Count, 1, 11);
        Assert.Equal("soru 11", ctx.History[^1].Question);
        Assert.Equal(12 - ctx.History.Count, ctx.DroppedHistoryTurns);
    }

    [Fact]
    public void QuestionLargerThanWindow_Throws()
    {
        var opts = new TokenBudgetOptions { ContextWindowTokens = 256, ReservedForAnswer = 128 };
        var huge = string.Join(' ', Enumerable.Repeat("uzun", 500));
        Assert.Throws<InvalidOperationException>(() => new ContextBudgeter(_tokens, opts).Fit("s", huge, [], []));
    }
}

public class PromptAndExtractiveModelTests
{
    private static readonly SearchHit[] Sources =
    [
        new(Guid.NewGuid(), Guid.NewGuid(), "GeoCommand", 0,
            "GeoCommand WPF istemcisi SignalR ile canlı konum alır. Veriler PostGIS üzerinde saklanır.", 0),
        new(Guid.NewGuid(), Guid.NewGuid(), "blade-fem", 0,
            "blade-fem C++17 ile yazılmış bir sonlu eleman çözücüsüdür. Yakınsama analitik çözümle doğrulanır.", 0),
    ];

    [Fact]
    public void BuildUserMessage_RoundTripsThroughParse()
    {
        var msg = PromptBuilder.BuildUserMessage("Konumlar nerede saklanır?", Sources);
        var (parsed, question) = PromptBuilder.Parse(msg);
        Assert.Equal("Konumlar nerede saklanır?", question);
        Assert.Equal([1, 2], parsed.Select(p => p.Number));
        Assert.StartsWith("GeoCommand WPF", parsed[0].Text);
    }

    [Fact]
    public void Extractive_PicksSupportingSentence_WithCitation()
    {
        var history = new ChatHistory();
        history.AddUserMessage(PromptBuilder.BuildUserMessage("blade-fem yakınsama nasıl doğrulanır?", Sources));
        var answer = ExtractiveChatCompletionService.Answer(history);
        Assert.Contains("Yakınsama analitik çözümle doğrulanır [2]", answer);
    }

    [Fact]
    public void Extractive_SaysNoAnswer_WhenNothingOverlaps()
    {
        var history = new ChatHistory();
        history.AddUserMessage(PromptBuilder.BuildUserMessage("Kuantum bilgisayar kübitleri?", Sources));
        Assert.Equal(ExtractiveChatCompletionService.NoAnswer, ExtractiveChatCompletionService.Answer(history));
    }

    [Fact]
    public async Task Extractive_StreamingConcatenation_EqualsNonStreaming()
    {
        var svc = new ExtractiveChatCompletionService();
        var history = new ChatHistory();
        history.AddUserMessage(PromptBuilder.BuildUserMessage("GeoCommand konumu neyle alır?", Sources));

        var full = (await svc.GetChatMessageContentsAsync(history))[0].Content;
        var parts = new List<string>();
        await foreach (var p in svc.GetStreamingChatMessageContentsAsync(history)) parts.Add(p.Content!);

        Assert.True(parts.Count > 3);
        Assert.Equal(full, string.Concat(parts));
    }
}

public class VerifierTests
{
    private static readonly SearchHit[] Sources =
    [
        new(Guid.NewGuid(), Guid.NewGuid(), "d", 0, "RabbitMQ kuyruğu başarısız mesajları dead-letter kuyruğuna taşır.", 0),
    ];

    [Fact]
    public void SupportedAnswerWithValidCitation_IsGrounded()
    {
        var v = new GroundingScorer().Score("Başarısız mesajlar dead-letter kuyruğuna taşınır [1].", Sources);
        Assert.Equal(VerdictLabel.Grounded, v.Label);
        Assert.Equal([1], v.CitedSources);
    }

    [Fact]
    public void InvalidCitation_PreventsGrounded()
    {
        var v = new GroundingScorer().Score("Başarısız mesajlar dead-letter kuyruğuna taşınır [3].", Sources);
        Assert.Equal([3], v.InvalidCitations);
        Assert.NotEqual(VerdictLabel.Grounded, v.Label);
    }

    [Fact]
    public void UnsupportedClaims_AreUngrounded()
    {
        var v = new GroundingScorer().Score("Kafka partisyonları tüketici gruplarına dağıtılır [1].", Sources);
        Assert.Equal(VerdictLabel.Ungrounded, v.Label);
    }

    [Theory]
    [InlineData("Başarısız mesajlar dead-letter kuyruğuna taşınır [1, 2].", new[] { 1, 2 })]
    [InlineData("Başarısız mesajlar dead-letter kuyruğuna taşınır [1], [2].", new[] { 1, 2 })]
    public void CitationLists_AreParsed(string answer, int[] expected)
    {
        var two = new[] { Sources[0], Sources[0] with { ChunkId = Guid.NewGuid() } };
        var v = new GroundingScorer().Score(answer, two);
        Assert.Equal(expected, v.CitedSources);
        Assert.Equal(VerdictLabel.Grounded, v.Label);
    }

    [Theory]
    [InlineData("Kaynaklarda bu soruya doğrudan bir bilgi bulunmamaktadır [4].")]
    [InlineData("Bu kaynaklarda verilen bilgilere göre, bu soruya yanıt bulunmuyor.")]
    [InlineData("The sources do not mention the number of tiles.")]
    public void ModelRefusals_AreNoAnswer(string answer) =>
        Assert.Equal(VerdictLabel.NoAnswer, new GroundingScorer().Score(answer, Sources).Label);

    [Fact]
    public void NoAnswerPhrase_IsNoAnswer() =>
        Assert.Equal(VerdictLabel.NoAnswer, new GroundingScorer().Score(ExtractiveChatCompletionService.NoAnswer, Sources).Label);

    [Theory]
    [InlineData("""{"supported": true, "reason": "ok"}""", true)]
    [InlineData("""Sonuç: ```json {"supported": false, "reason": "x"} ```""", false)]
    public void ParseJudge_ExtractsJsonFromNoisyOutput(string raw, bool expected) =>
        Assert.Equal(expected, VerifierAgent.ParseJudge(raw)!.Value.Supported);

    [Fact]
    public void ParseJudge_ReturnsNullOnGarbage() => Assert.Null(VerifierAgent.ParseJudge("evet destekleniyor"));
}

public class RagChatOrchestratorTests
{
    private static async Task<ChatHarness> Seeded()
    {
        var h = new ChatHarness();
        await h.Index.AddAsync("GeoCommand",
            "GeoCommand WPF istemcisi SignalR ile canlı konum alır. Konum verileri PostGIS üzerinde saklanır.");
        await h.Index.AddAsync("blade-fem",
            "blade-fem C++17 ile yazılmış bir sonlu eleman çözücüsüdür. Yakınsama analitik çözümle doğrulanır.");
        return h;
    }

    [Fact]
    public async Task Stream_EmitsSourcesTokensVerdictDone_InOrder()
    {
        var h = await Seeded();
        var events = await ChatHarness.Collect(h.Build().StreamAsync("u1", new ChatRequest("Konum verileri nerede saklanır?"), default));

        Assert.IsType<SourcesEvent>(events[0]);
        Assert.IsType<DoneEvent>(events[^1]);
        Assert.IsType<VerdictEvent>(events[^2]);
        var tokens = events.OfType<TokenEvent>().ToList();
        Assert.True(tokens.Count > 3);
        Assert.Contains("PostGIS", string.Concat(tokens.Select(t => t.Text)));

        var done = (DoneEvent)events[^1];
        Assert.False(done.Metrics.AnswerFromCache);
        Assert.True(done.Metrics.FirstTokenMs <= done.Metrics.TotalMs);
        Assert.Single(h.Audit.Entries);
        Assert.Equal(VerdictLabel.Grounded, ((VerdictEvent)events[^2]).Verdict.Label);
    }

    [Fact]
    public async Task SameFirstQuestion_IsServedFromAnswerCache()
    {
        var h = await Seeded();
        var orchestrator = h.Build();
        var first = await ChatHarness.Collect(orchestrator.StreamAsync("u1", new ChatRequest("Konum verileri nerede saklanır?"), default));
        var second = await ChatHarness.Collect(orchestrator.StreamAsync("u2", new ChatRequest("konum verileri nerede saklanir?"), default));

        Assert.Single(h.Cache.Items);
        Assert.True(((DoneEvent)second[^1]).Metrics.AnswerFromCache);
        Assert.Equal(
            string.Concat(first.OfType<TokenEvent>().Select(t => t.Text)),
            string.Concat(second.OfType<TokenEvent>().Select(t => t.Text)));
    }

    [Fact]
    public async Task FollowUp_UsesHistory_AndBypassesCache()
    {
        var h = await Seeded();
        var orchestrator = h.Build();
        var first = await ChatHarness.Collect(orchestrator.StreamAsync("u1", new ChatRequest("blade-fem hangi dilde yazılmış?", "s1"), default));
        var followUp = await ChatHarness.Collect(orchestrator.StreamAsync("u1", new ChatRequest("yakınsama?", "s1"), default));

        var done = (DoneEvent)followUp[^1];
        Assert.Equal(1, done.Metrics.HistoryTurnsUsed);
        Assert.False(done.Metrics.AnswerFromCache);
        Assert.Contains("blade-fem", done.Metrics.StandaloneQuery); // kısa takip sorusu bağlamla genişletildi
        Assert.Equal(2, h.History.Turns["u1/s1"].Count);
        Assert.Equal("s1", ((SourcesEvent)first[0]).SessionId);
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotBreakStream()
    {
        var h = await Seeded();
        h.History.FailOnAppend = true;
        var events = await ChatHarness.Collect(h.Build().StreamAsync("u1", new ChatRequest("Konum verileri nerede saklanır?"), default));
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Cancellation_StopsTheStream()
    {
        var h = await Seeded();
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in h.Build().StreamAsync("u1", new ChatRequest("Konum verileri nerede saklanır?"), cts.Token))
            {
                if (e is TokenEvent && ++seen == 2) cts.Cancel();
            }
        });
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public void AnswerCacheKey_IgnoresCaseAndTurkishFolding_ButNotChunks()
    {
        var c = new[] { Guid.NewGuid() };
        Assert.Equal(
            RagChatOrchestrator.AnswerCacheKey("m", "İzmir Şubesi", c),
            RagChatOrchestrator.AnswerCacheKey("m", "izmir subesi", c));
        Assert.NotEqual(
            RagChatOrchestrator.AnswerCacheKey("m", "q", c),
            RagChatOrchestrator.AnswerCacheKey("m", "q", [Guid.NewGuid()]));
    }

    [Fact]
    public async Task RetrieverAgent_RunsSearchThroughKernelPlugin()
    {
        var h = await Seeded();
        var kernel = new Kernel();
        var search = new HybridSearchService(h.Index, h.Embeddings, new LexicalReranker(), new HybridSearchOptions());
        var agent = new RetrieverAgent(kernel, new KnowledgeBasePlugin(search), new AgentOptions());

        var (q, result) = await agent.RetrieveAsync("PostGIS", [], 1, default);

        Assert.Equal("PostGIS", q);
        Assert.Single(result.Hits);
        Assert.True(kernel.Plugins.TryGetFunction(KnowledgeBasePlugin.PluginName, "search_documents", out _));
    }
}
