﻿using Client.Interfaces;
using Shared.Helpers;
using Shared.Interfaces;
using Shared.Models;
using System;
using System.Threading.Tasks;

namespace Client.Hub
{
    public class ClientMessageHub : IClientMessageHub
    {       
        private readonly IClientQueueRepository _clientQueueRepository;

        public ClientMessageHub(IClientQueueRepository clientQueueRepository)
        {
            _clientQueueRepository = clientQueueRepository;
        }
        public async Task SendMessageToServerAsync(object message, Guid correlationId)
        {
            // Serialize the message
            var result = Helpers.ConvertObjectToJson(message);

            // Create a client queue entity
            var entity = new QueueEntity
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Content = result.Item1,
                TypeName = result.Item2.FullName,
                Created = DateTime.Now,
                StatusDate = DateTime.Now,
            };

            // Add the entity to the server queue
            await _clientQueueRepository.AddClientQueueItemAsync(entity);
        }

        public async Task<TResponse> ListenForMessageFromServerAsync<TResponse>(Guid correlationId)
        {
            while (true)
            {
                // Poll the server queue for a message with the matching correlation ID
                var response = await _clientQueueRepository.GetMessageFromServerByCorrelationIdAsync(correlationId);

                if (response != null)
                {
                    return (TResponse)Helpers.ConvertJsonToObject(response.Content, Helpers.GetType(response.TypeName));
                }
            }
        }
    }
}
