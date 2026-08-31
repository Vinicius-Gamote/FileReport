using FileReport.Application.Configuration;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
namespace FileReport.Infrastructure.Messaging;

public sealed class Broker(IConfiguration config, ProcessingSettings settings)
{
    public const string Exchange = "filereport.commands", Queue = "filereport.comparisons.process",
        RoutingKey = "comparison.requested.v1", DeadExchange = "filereport.deadletter",
        DeadQueue = "filereport.comparisons.dlq", DeadKey = "comparison.failed.v1";

    public async Task<IConnection> Connect(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = config["RabbitMq:Host"] ?? "localhost",
            Port = config.GetValue("RabbitMq:Port", 5672),
            UserName = config["RabbitMq:User"] ?? "",
            Password = config["RabbitMq:Password"] ?? "",
            VirtualHost = config["RabbitMq:VirtualHost"] ?? "/",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
            ConsumerDispatchConcurrency = (ushort)settings.MaxConcurrentJobsPerWorker
        };
        return await factory.CreateConnectionAsync(ct);
    }
    public async Task Declare(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue, true, false, false, new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-max-length-bytes"] = 67108864L,
            ["x-overflow"] = "reject-publish"
        }, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, DeadKey, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, true, false, false, new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = DeadExchange,
            ["x-dead-letter-routing-key"] = DeadKey,
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
            ["x-delivery-limit"] = 20,
            ["x-max-length-bytes"] = 67108864L,
            ["x-consumer-timeout"] = settings.ConsumerAcknowledgmentTimeoutSeconds * 1000L
        }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: ct);
    }
}
