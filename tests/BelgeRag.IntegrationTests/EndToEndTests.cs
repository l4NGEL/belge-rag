using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BelgeRag.Infrastructure;
using BelgeRag.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace BelgeRag.IntegrationTests;

/// <summary>
/// API (WebApplicationFactory) + Worker (ayrı generic host) + gerçek RabbitMQ/PostgreSQL/Redis/MongoDB:
/// belge yükle → kuyruk → worker indeksler → arama → SSE sohbet.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class EndToEndTests(InfraFixture infra) : IAsyncLifetime
{
    private ApiFactory _api = null!;
    private IHost _worker = null!;

    private const string Doc =
        """
        # Kuyruk mimarisi
        Belge alımı RabbitMQ üzerinden yapılır. API belgeyi PostgreSQL'e kaydeder ve yalnızca belge kimliğini kuyruğa yazar.
        Worker servisi mesajı alır, metni parçalara böler ve her parçanın embedding vektörünü pgvector sütununa yazar.

        # Hata yönetimi
        İşlenemeyen mesajlar belirli sayıda yeniden denenir. Deneme hakkı biten mesajlar dead-letter kuyruğuna taşınır ve belge failed olarak işaretlenir.
        """;

    public async Task InitializeAsync()
    {
        _api = new ApiFactory(infra);
        _ = _api.Server; // API'yi (ve initializer'ı) başlat
        _worker = await infra.StartWorkerAsync();
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync();
        await ((IAsyncDisposable)_worker).DisposeAsync();
        await _api.DisposeAsync();
    }

    private async Task<HttpClient> ClientAs(string user, string password)
    {
        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/token", new { username = user, password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> WaitIndexedAsync(HttpClient client, string statusUrl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>(statusUrl);
            var s = status.GetProperty("status").GetString();
            if (s is "indexed" or "failed" || DateTime.UtcNow > deadline) return status;
            await Task.Delay(100);
        }
    }

    private static async Task<List<(string Event, JsonElement Data)>> ReadSseAsync(HttpResponseMessage res)
    {
        var events = new List<(string, JsonElement)>();
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(), Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) data.Append(line[6..]);
            else if (line.Length == 0 && name is not null)
            {
                events.Add((name, JsonDocument.Parse(data.ToString()).RootElement.Clone()));
                name = null;
                data.Clear();
            }
        }
        return events;
    }

    [Fact]
    public async Task UploadIndexSearchAndStreamChat()
    {
        var admin = await ClientAs("admin", "admin-pw");
        var title = $"mimari-{Guid.NewGuid():N}"[..16];

        var created = await admin.PostAsJsonAsync("/api/documents", new { title, content = Doc });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var statusUrl = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusUrl").GetString()!;

        var status = await WaitIndexedAsync(admin, statusUrl);
        Assert.Equal("indexed", status.GetProperty("status").GetString());
        Assert.True(status.GetProperty("chunkCount").GetInt32() >= 2);

        var search = await admin.GetFromJsonAsync<JsonElement>("/api/search?q=dead-letter%20kuyruğu&topK=3");
        var top = search.GetProperty("hits")[0];
        Assert.Equal(title, top.GetProperty("documentTitle").GetString());
        Assert.Contains("dead-letter", top.GetProperty("text").GetString());

        var reader = await ClientAs("reader", "reader-pw");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(new { question = "Deneme hakkı biten mesajlara ne olur?", topK = 3 }),
        };
        using var res = await reader.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);

        var events = await ReadSseAsync(res);
        Assert.Equal("sources", events[0].Event);
        Assert.Equal("done", events[^1].Event);
        Assert.Equal("verdict", events[^2].Event);
        var answer = string.Concat(events.Where(e => e.Event == "token").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Contains("dead-letter", answer);
        Assert.Contains("[", answer);

        // Sohbet geçmişi MongoDB'ye yazıldı ve oturum sahibi tarafından okunabiliyor.
        var sessionId = events[0].Data.GetProperty("sessionId").GetString();
        var history = await reader.GetFromJsonAsync<JsonElement>($"/api/chat/sessions/{sessionId}");
        Assert.Equal(1, history.GetArrayLength());
        var other = await admin.GetFromJsonAsync<JsonElement>($"/api/chat/sessions/{sessionId}");
        Assert.Equal(0, other.GetArrayLength());
    }

    [Fact]
    public async Task Authorization_IsEnforced()
    {
        var anon = _api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/token", new { username = "admin", password = "nope" })).StatusCode);

        var reader = await ClientAs("reader", "reader-pw");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/documents", new { title = "x", content = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/audit")).StatusCode);
    }

    [Fact]
    public async Task InvalidChatRequest_Returns400_BeforeStreaming()
    {
        var reader = await ClientAs("reader", "reader-pw");
        var res = await reader.PostAsJsonAsync("/api/chat/stream", new { question = "", topK = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PoisonMessage_GoesToDeadLetterQueue()
    {
        var conn = _worker.Services.GetRequiredService<RabbitMqConnection>();
        var o = conn.Options;
        var c = await conn.GetAsync(default);
        await using var ch = await c.CreateChannelAsync();
        var before = (await ch.QueueDeclarePassiveAsync(o.DeadLetterQueue)).MessageCount;

        await ch.BasicPublishAsync(o.Exchange, o.RoutingKey, Encoding.UTF8.GetBytes("{bozuk json"));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        uint after;
        do
        {
            await Task.Delay(100);
            after = (await ch.QueueDeclarePassiveAsync(o.DeadLetterQueue)).MessageCount;
        } while (after == before && DateTime.UtcNow < deadline);
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task HealthEndpoint_ReportsAllDependenciesHealthy()
    {
        var res = await _api.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("Healthy", res.GetProperty("status").GetString());
        foreach (var dep in new[] { "postgres", "redis", "mongo", "rabbitmq" })
            Assert.Equal("Healthy", res.GetProperty("checks").GetProperty(dep).GetProperty("status").GetString());
    }

    [Fact]
    public async Task OpenApiDocument_DescribesEndpointsAndBearerScheme()
    {
        var doc = await _api.CreateClient().GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/chat/stream", out _));
        Assert.True(paths.TryGetProperty("/api/documents", out _));
        Assert.True(doc.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }
}
