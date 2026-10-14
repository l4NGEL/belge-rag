using System.Text.Json;
using BelgeRag.Core.Ingestion;
using BelgeRag.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BelgeRag.Worker;

/// <summary>
/// Kuyruk tüketicisi (BackgroundService = Singleton). Scoped servisleri doğrudan enjekte etmez;
/// her mesaj için <see cref="IServiceScopeFactory"/> ile yeni kapsam açar. Böylece DB bağlantıları
/// mesaj bitince havuza döner ve mesajlar arasında durum sızmaz.
/// Hata durumunda mesaj x-retry başlığı artırılarak yeniden yayınlanır; MaxRetries aşılınca
/// nack(requeue:false) ile dead-letter kuyruğuna düşer ve belge 'failed' olarak işaretlenir.
/// </summary>
public sealed class IngestConsumer(
    RabbitMqConnection connection,
    RabbitMqIngestQueue publisher,
    IServiceScopeFactory scopes,
    ILogger<IngestConsumer> logger) : BackgroundService
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = connection.Options;
        var conn = await connection.GetAsync(stoppingToken);
        _channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await _channel.BasicQosAsync(0, o.Prefetch, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(_channel, ea, stoppingToken);
        await _channel.BasicConsumeAsync(o.Queue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("{Queue} kuyruğu dinleniyor (prefetch {Prefetch}, eşzamanlılık {Concurrency})",
            o.Queue, o.Prefetch, o.ConsumerConcurrency);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        IngestMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<IngestMessage>(ea.Body.Span)
                      ?? throw new JsonException("boş mesaj");
            await using var scope = scopes.CreateAsyncScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<DocumentIngestionService>();
            await ingestion.IngestAsync(message.DocumentId, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (JsonException e)
        {
            // Zehirli mesaj: tekrar denemenin anlamı yok, doğrudan DLQ.
            logger.LogError(e, "Çözümlenemeyen mesaj DLQ'ya gönderiliyor");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var retry = RetryCount(ea.BasicProperties);
            if (retry < connection.Options.MaxRetries)
            {
                logger.LogWarning(e, "Belge {DocumentId} işlenemedi, yeniden deneniyor ({Retry}/{Max})",
                    message!.DocumentId, retry + 1, connection.Options.MaxRetries);
                await publisher.PublishAsync(message, retry + 1, ct);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
            }
            else
            {
                logger.LogError(e, "Belge {DocumentId} {Max} denemede işlenemedi, DLQ'ya gidiyor",
                    message!.DocumentId, connection.Options.MaxRetries);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDocumentRepository>()
                    .MarkFailedAsync(message.DocumentId, e.Message, ct);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            }
        }
    }

    internal static int RetryCount(IReadOnlyBasicProperties props) =>
        props.Headers?.TryGetValue(RabbitMqIngestQueue.RetryHeader, out var v) == true
            ? v switch { int i => i, long l => (int)l, byte b => b, short s => s, _ => 0 }
            : 0;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync(cancellationToken);
                await _channel.DisposeAsync();
            }
            catch (Exception e) when (e is OperationCanceledException or RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                logger.LogDebug("Kanal kapanışı atlandı: {Error}", e.Message);
            }
        }
    }
}
