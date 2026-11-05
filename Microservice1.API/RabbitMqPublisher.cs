using System.Text;
using RabbitMQ.Client;

namespace Microservice1.API;

// RabbitMQ'ya mesaj gönderir. Bağlantı ilk kullanımda açılır ve uygulama boyunca paylaşılır.
// Bağlantı adresi ConnectionStrings:RabbitMQ ayarından okunur (amqp://kullanici:sifre@host:port).
public sealed class RabbitMqPublisher(IConfiguration config) : IAsyncDisposable
{
    public const string QueueName = "microservice1-messages";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(string message, CancellationToken cancellationToken = default)
    {
        var channel = await GetChannelAsync(cancellationToken);
        var properties = new BasicProperties { Persistent = true };
        await channel.BasicPublishAsync(
            exchange: string.Empty,                 // Default exchange: routing key = kuyruk adı
            routingKey: QueueName,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message),
            cancellationToken: cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var uri = config.GetConnectionString("RabbitMQ")
                ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ tanımlı değil");
            var factory = new ConnectionFactory { Uri = new Uri(uri) };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            // Kuyruk yoksa oluşturur. Publisher ve consumer aynı tanımı kullanmalı.
            await _channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken);
            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
