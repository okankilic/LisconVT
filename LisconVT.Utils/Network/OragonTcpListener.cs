using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LisconVT.Utils.Network
{
    public class OragonTcpListener
    {
        IPAddress _ip = IPAddress.Parse("127.0.0.1");
        TcpListener _server = null;
        static Logger _logger = LogManager.GetCurrentClassLogger();

        public string Name { get; private set; }
        public int Port { get; private set; }
        
        public ConcurrentDictionary<Guid, string> ClientIDs { get; private set; }
        public ConcurrentDictionary<Guid, OragonTcpClient> Clients { get; private set; }

        public event OnDataReceived DataReceived = null;

        public OragonTcpListener(string name, int port)
        {
            this.ClientIDs = new ConcurrentDictionary<Guid, string>();
            this.Clients = new ConcurrentDictionary<Guid, OragonTcpClient>();

            Name = name;
            Port = port;
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(Listen);
        }

        private void Listen(Object x)
        {
            if (_server == null)
                _server = new TcpListener(_ip, Port);

            _server.Start();
            _logger.Info("Server: {0} started listening port: {1}", Name, Port);

            while (true)
            {
                try 
                {
                    var tcpClient = _server.AcceptTcpClient();

                    var client = new OragonTcpClient(tcpClient);
                    client.DataReceived += Client_DataReceived;

                    if (ClientIDs.TryAdd(client.Guid, client.ID))
                    {
                        if (Clients.TryAdd(client.Guid, client) == true)
                        {
                            _logger.Info("Client {0} connected", client.Guid);
                            client.ReadAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            }
        }

        public void Send(string clientID, byte[] bytes)
        {
            var clientKey = ClientIDs.Single(q => q.Value == clientID).Key;
            Clients[clientKey].Write(bytes);
        }

        private void Client_DataReceived(string clientID, byte[] bytes)
        {
            DataReceived?.Invoke(clientID, bytes);
        }

        public void Stop()
        {
            try
            {
                _server.Stop();
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
        }

    }
}
