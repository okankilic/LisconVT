using LisconVT.Utils.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;
using LisconVT.Domain.Helpers;
using LisconVT.Domain.Models;
using LisconVT.Domain.Enums;
using Liscon.Demo.GatewayConsole.Models;

namespace Liscon.Demo.GatewayConsole.Servers
{
    public class MediaServer : OragonTcpListener
    {
        int TxNo = 0;

        public ConcurrentDictionary<string, MediaClient> Clients { get; set; }

        public event OnClientConnected ClientConnected = null;
        public event OnClientDisconnected ClientDisconnected = null;
        public event OnClientRuntimeUpdated ClientRuntimeUpdated = null;

        public MediaServer(string name, int port) : base(name, port)
        {
            Clients = new ConcurrentDictionary<string, MediaClient>();
        }

        public override void OnClientConnected(Socket socket)
        {
            var client = new MediaClient(socket);
            client.MessageReceived += Client_MessageReceived;
            client.Start();
        }

        private void Client_MessageReceived(object sender, MdvrMessageBase message)
        {
            if (message.MessageKey == MdvrMessageTypes.V102)
            {
                var client = sender as MediaClient;
                if (Clients.TryAdd(message.DevIDNO, client) == true)
                    ClientConnected?.Invoke(message.DevIDNO);
            }
            else
                throw new NotImplementedException();
        }
    }
}
