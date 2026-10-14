using System.Text.Json;
using BelgeRag.Core.Ingestion;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BelgeRag.Infrastructure.Messaging;

/// <summary>
/// Tek, uzun ömürlü AMQP bağlantısı (Singleton). RabbitMQ bağlantıları pahalıdır ve
/// çoklanmak üzere tasarlanmıştır; kanal ise iş başına açılır. İlk kullanımda tembel bağlanır,
/// böylece DI doğrulaması ve testler broker olmadan da kapsayıcıyı kurabilir.
/// </summary>
public sealed class RabbitMqConnection(RabbitMqOptions options, string clientName) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options => options;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Uri),
                ClientProvidedName = clientName,
                AutomaticRecoveryEnabled = true,
                ConsumerDispatchConcurrency = options.ConsumerConcurrency,
            };
            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Exchange, kuyruk ve dead-letter topolojisini idempotent olarak kurar.</summary>
    public async Task DeclareTopologyAsync(CancellationToken ct)
    {
        var conn = await GetAsync(ct);
        await using var ch = await conn.CreateChannelAsync(cancellationToken: ct);
        await ch.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await ch.QueueDeclareAsync(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await ch.QueueBindAsync(options.DeadLetterQueue, options.DeadLetterExchange, "", cancellationToken: ct);

        await ch.ExchangeDeclareAsync(options.Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await ch.QueueDeclareAsync(options.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = options.DeadLetterExchange },
            cancellationToken: ct);
        await ch.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            // Kapanış en iyi çaba ile: broker zaten gitmişse ya da kapanış zaman aşımına uğrarsa süreç kapanışını engelleme.
            try
            {
                await _connection.CloseAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e) when (e is OperationCanceledException or RabbitMQ.Client.Exceptions.AlreadyClosedException
                                          or IOException or TimeoutException)
            {
            }
            await _connection.DisposeAsync();
        }
        _gate.Dispose();
    }
}

/// <summary>
/// Yayıncı onaylı (publisher confirms) kalıcı mesaj gönderimi. Tek kanal paylaşılır;
/// IChannel eşzamanlı yayına uygun olmadığından yayınlar bir semafor ile sıraya girer.
/// </summary>
public sealed class RabbitMqIngestQueue(RabbitMqConnection connection, ILogger<RabbitMqIngestQueue> logger)
    : IIngestQueue, IAsyncDisposable
{
    public const string RetryHeader = "x-retry";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task EnqueueAsync(IngestMessage message, CancellationToken ct) =>
        await PublishAsync(message, retry: 0, ct);

    public async Task PublishAsync(IngestMessage message, int retry, CancellationToken ct)
    {
        var o = connection.Options;
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = $"{message.DocumentId:N}-{retry}",
            Headers = new Dictionary<string, object?> { [RetryHeader] = retry },
        };
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is not { IsOpen: true })
            {
                var conn = await connection.GetAsync(ct);
                _channel = await conn.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            }
            // Onay izleme açıkken bu çağrı broker 'ack' verene kadar bekler; 'nack' istisna fırlatır.
            await _channel.BasicPublishAsync(o.Exchange, o.RoutingKey, mandatory: true, props, body, ct);
        }
        finally
        {
            _gate.Release();
        }
        logger.LogInformation("Belge {DocumentId} kuyruğa yazıldı (deneme {Retry})", message.DocumentId, retry);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            try { await _channel.DisposeAsync(); }
            catch (Exception e) when (e is OperationCanceledException or RabbitMQ.Client.Exceptions.AlreadyClosedException) { }
        }
        _gate.Dispose();
    }
}
