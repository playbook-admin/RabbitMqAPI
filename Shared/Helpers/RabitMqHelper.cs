using RabbitMQ.Client;
using System.Threading.Tasks;

namespace Shared.Helpers
{
    public static class RabbitMqHelper
    {
        public static async Task<IConnection> CreateConnection()
        {
            var factory = new ConnectionFactory() { 
             UserName ="guest",Password="guest", VirtualHost="/",HostName="localhost"           };


            return await factory.CreateConnectionAsync();
        }

        public static async Task<IChannel> CreateChannelAsync(IConnection connection)
        {
            return await connection.CreateChannelAsync();
        }
    }
}
