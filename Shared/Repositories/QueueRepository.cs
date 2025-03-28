using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Helpers;
using Shared.Models;
using System;
using System.Threading;
using System.Threading.Tasks;


namespace Shared.Repositories;

public class QueueRepository : IQueueRepository
{
    private readonly string _clientQueueName = "ClientQueue";
    private readonly string _serverQueueName = "ServerQueue";


    public async Task<QueueEntity> ConsumeSingleMessageAsync(string queueName, TimeSpan timeout, Guid? correlationId=null,  CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<QueueEntity>();

        var connection = await RabbitMqHelper.CreateConnection();
        var channel = await RabbitMqHelper.CreateChannelAsync(connection);

        try
        {
            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = System.Text.Json.JsonSerializer.Deserialize<QueueEntity>(body);

                    if (message != null && ((correlationId == null) || message.CorrelationId == correlationId))
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

            // Start consuming
            await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

            // Wait for message, timeout, or cancellation
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == tcs.Task)
                return await tcs.Task;

            return null; // timeout or cancelled
        }
        finally
        {
            // This is NOT Dispose, only closing the channel
            try { if (channel.IsOpen) await channel.CloseAsync(); } catch { }
            try { if (connection.IsOpen) await connection.CloseAsync(); } catch { }
        }
    }


    public async Task<QueueEntity> GetMessageFromClientQueueAsync()
    {
        return await ConsumeSingleMessageAsync(_clientQueueName, TimeSpan.FromSeconds(10));
    }

    public async Task<int> AddClientQueueItemAsync(QueueEntity entity)
    {
        return await AddQueueItemAsync(_clientQueueName, entity);
    }

    public async Task<QueueEntity> GetMessageFromServerByCorrelationIdAsync(Guid correlationId)
    {
        return await ConsumeSingleMessageAsync(_serverQueueName, TimeSpan.FromSeconds(10), correlationId);
    }

    public async Task<int> AddServerQueueItemAsync(QueueEntity entity)
    {
        return await AddQueueItemAsync(_serverQueueName, entity);
    }

    public async Task<int> AddQueueItemAsync<T>(string queueName, T entity)
    {
        try
        {
            // Use the RabbitMqHelper to create a connection asynchronously
            using (var connection = await RabbitMqHelper.CreateConnection())
            using (var channel = await RabbitMqHelper.CreateChannelAsync(connection))
            {
                // Declare the queue if not declared
                await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);

                // Serialize the entity to JSON
                var json = System.Text.Json.JsonSerializer.Serialize(entity);

                // Create BasicProperties (IBasicProperties)
                var properties = new BasicProperties
                {
                    CorrelationId = "your-correlation-id",
                    // Other properties like MessageId, ContentType, etc.
                };

                // Set the CorrelationId if the entity has it
                var correlationIdProperty = typeof(T).GetProperty("CorrelationId");
                if (correlationIdProperty != null)
                {
                    var correlationIdValue = correlationIdProperty.GetValue(entity)?.ToString();
                    if (!string.IsNullOrEmpty(correlationIdValue))
                    {
                        properties.CorrelationId = correlationIdValue;
                    }
                }

                // Convert the serialized entity to byte[] (message body)
                byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

                // Publish the message
                await channel.BasicPublishAsync(
                    exchange: "",                      // Default exchange (empty string)
                    routingKey: queueName,             // Your queue name as the routing key
                    mandatory: true,                   // You can set this to true or false depending on your needs
                    basicProperties: properties,       // The BasicProperties containing your message properties
                    body: new ReadOnlyMemory<byte>(body),  // Convert your message body to ReadOnlyMemory<byte>
                    cancellationToken: CancellationToken.None  // Optionally pass a cancellation token
                );

                return 1; // Return 1 to indicate success
            }
        }
        catch (Exception ex)
        {
            // Optionally log the error
            Console.WriteLine($"An error occurred: {ex.Message}");
            return 0; // Return 0 to indicate failure
        }
    }
}