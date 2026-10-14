using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BelgeRag.Core.Chat;
using Microsoft.AspNetCore.Http.Features;

namespace BelgeRag.Api.Sse;

/// <summary>
/// text/event-stream çerçeveleyici. .NET 8'de yerleşik SSE sonucu olmadığı için (TypedResults.ServerSentEvents
/// .NET 10'da geldi) yanıt gövdesine doğrudan yazar ve her olaydan sonra flush eder.
/// </summary>
public static class SseWriter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Prepare(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Connection = "keep-alive";
        // nginx gibi ters vekillerin yanıtı tamponlamasını engelle; aksi halde tokenlar toplu gelir.
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public static string Format(ChatStreamEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, Json);
        var sb = new StringBuilder();
        sb.Append("event: ").Append(evt.EventName).Append('\n');
        // SSE'de veri satırı yeni satır içeremez; JSON zaten tek satır ama savunmacı olarak böl.
        foreach (var line in json.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    public static async Task WriteAsync(HttpResponse response, ChatStreamEvent evt, CancellationToken ct)
    {
        await response.WriteAsync(Format(evt), ct);
        await response.Body.FlushAsync(ct);
    }
}
