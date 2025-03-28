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

    public async Task<QueueEntity> GetMessageFromClientQueueAsync()
    {
        var connection = await RabbitMqHelper.CreateConnection();
        var channel = await RabbitMqHelper.CreateChannelAsync(connection);

        await channel.QueueDeclareAsync(_clientQueueName, durable: true, exclusive: false, autoDelete: false);

        var tcs = new TaskCompletionSource<QueueEntity>();

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var message = System.Text.Json.JsonSerializer.Deserialize<QueueEntity>(body);

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);

                tcs.TrySetResult(message);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        await channel.BasicConsumeAsync(queue: _clientQueueName, autoAck: false, consumer: consumer);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        // cleanup
        await channel.CloseAsync();
        await connection.CloseAsync();

        return completedTask == tcs.Task ? await tcs.Task : null;
    }



    public async Task<int> AddClientQueueItemAsync(QueueEntity entity)
    {
        return await AddQueueItemAsync(_clientQueueName, entity);
    }

    public async Task<QueueEntity> GetMessageFromServerByCorrelationIdAsync(Guid correlationId)
    {
        var connection = await RabbitMqHelper.CreateConnection();
        var channel = await RabbitMqHelper.CreateChannelAsync(connection);
        // Declare the queue before consuming messages from it
        await channel.QueueDeclareAsync(_serverQueueName, durable: true, exclusive: false, autoDelete: false);

        // Define the selector based on the CorrelationId
        string selector = $"CorrelationId = '{correlationId}'";

        // Create a TaskCompletionSource to complete when a message is received
        var tcs = new TaskCompletionSource<QueueEntity>();

        // Create the consumer for the queue
        var consumer = new AsyncEventingBasicConsumer(channel);

        // Set up the consumer received event handler
        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                // Deserialize the message
                var body = ea.Body.ToArray();
                var message = System.Text.Json.JsonSerializer.Deserialize<QueueEntity>(body);

                // Check if the correlationId matches
                if (message.CorrelationId == correlationId)
                {
                    // Acknowledge the message
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);

                    // Set the result in the TaskCompletionSource to complete the method
                    tcs.TrySetResult(message);
                }
                else
                {
                    // Optionally, reject or ignore message if correlationId doesn't match
                    await channel.BasicRejectAsync(ea.DeliveryTag, requeue: true);
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions during message processing
                tcs.TrySetException(ex);
            }
        };

        // Start consuming messages asynchronously with the selector (filter)
        await channel.BasicConsumeAsync(queue: _serverQueueName, autoAck: false, consumer: consumer);

        // Wait for either the message or timeout
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        // cleanup
        await channel.CloseAsync();
        await connection.CloseAsync();

        return completedTask == tcs.Task ? await tcs.Task : null;
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