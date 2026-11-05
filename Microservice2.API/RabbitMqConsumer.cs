using System.Collections.Concurrent;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Microservice2.API;

// Alınan son 50 mesajı bellekte tutar.
public sealed class ReceivedMessageStore
{
    private readonly ConcurrentQueue<ReceivedMessage> _messages = new();

    public void Add(string body)
    {
        _messages.Enqueue(new ReceivedMessage(DateTimeOffset.UtcNow, body, Environment.MachineName));
        while (_messages.Count > 50) _messages.TryDequeue(out _);
    }

    public IReadOnlyCollection<ReceivedMessage> GetAll() => _messages.ToArray();
}

public record ReceivedMessage(DateTimeOffset ReceivedAt, string Body, string ReceivedBy);

// Microservice1'in gönderdiği kuyruğu dinler. RabbitMQ henüz hazır değilse bağlanana kadar yeniden dener.
public sealed class RabbitMqConsumer(IConfiguration config, ReceivedMessageStore store, ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private const string QueueName = "microservice1-messages";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var uri = config.GetConnectionString("RabbitMQ")
            ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ tanımlı değil");
        var factory = new ConnectionFactory { Uri = new Uri(uri) };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    var body = Encoding.UTF8.GetString(ea.Body.Span);
                    store.Add(body);
                    logger.LogInformation("Mesaj alındı: {Body}", body);
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                };
                await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("{Queue} kuyruğu dinleniyor", QueueName);

                // Bağlantı kopana veya uygulama kapanana kadar bekle
                var closed = new TaskCompletionSource();
                connection.ConnectionShutdownAsync += (_, _) =>
                {
                    closed.TrySetResult();
                    return Task.CompletedTask;
                };
                await using (stoppingToken.Register(() => closed.TrySetResult()))
                {
                    await closed.Task;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "RabbitMQ'ya bağlanılamadı, 5 sn sonra tekrar denenecek");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
