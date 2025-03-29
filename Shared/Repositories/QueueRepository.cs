using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Helpers;
using Shared.Models;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace Shared.Repositories;

/// <summary>
/// Handles publishing and consuming messages from RabbitMQ queues.
/// </summary>
public class QueueRepository : IQueueRepository
{
    private readonly string _clientQueueName = "ClientQueue";
    private readonly string _serverQueueName = "ServerQueue";

    /// <summary>
    /// Consumes a single message from the specified queue.
    /// </summary>
    public async Task<QueueEntity> ConsumeSingleMessageAsync(string queueName, TimeSpan timeout, Guid? correlationId=null,  CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<QueueEntity>();

        var connection = await RabbitMqHelper.CreateConnection();
        var channel = await RabbitMqHelper.CreateChannelAsync(connection);

        try
        {
            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = JsonSerializer.Deserialize<QueueEntity>(body);

                    if (message != null && (!correlationId.HasValue || message.CorrelationId == correlationId))
                    {
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
                        tcs.TrySetResult(message);
                    }
                    else
                    {
                        await channel.BasicRejectAsync(ea.DeliveryTag, requeue: true, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));

            return completedTask == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            // This is NOT Dispose, only closing the channel
            try { if (channel.IsOpen) await channel.CloseAsync(); } catch { }
            try { if (connection.IsOpen) await connection.CloseAsync(); } catch { }
        }
    }

    /// <summary>
    /// Adds a message to the specified queue.
    /// </summary>
    public async Task<int> AddQueueItemAsync<T>(string queueName, T entity)
    {
        try
        {
            using var connection = await RabbitMqHelper.CreateConnection();
            using var channel = await RabbitMqHelper.CreateChannelAsync(connection);

            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);

            var json = JsonSerializer.Serialize(entity);
            var body = Encoding.UTF8.GetBytes(json);

            var properties = new BasicProperties
            {
                CorrelationId = entity?.GetType().GetProperty("CorrelationId")?.GetValue(entity)?.ToString() ?? string.Empty
            };

            await channel.BasicPublishAsync(string.Empty, queueName, mandatory: true, properties, new ReadOnlyMemory<byte>(body));

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Publish failed: {ex.Message}");
            return 0;
        }
    }

    // --- High-level convenience methods ---

    public Task<QueueEntity> GetMessageFromClientQueueAsync() =>
        ConsumeSingleMessageAsync(_clientQueueName, TimeSpan.FromSeconds(10));

    public Task<int> AddClientQueueItemAsync(QueueEntity entity) =>
        AddQueueItemAsync(_clientQueueName, entity);

    public Task<QueueEntity> GetMessageFromServerByCorrelationIdAsync(Guid correlationId) =>
        ConsumeSingleMessageAsync(_serverQueueName, TimeSpan.FromSeconds(10), correlationId);

    public Task<int> AddServerQueueItemAsync(QueueEntity entity) =>
        AddQueueItemAsync(_serverQueueName, entity);
}