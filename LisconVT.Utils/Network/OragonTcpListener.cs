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
    public abstract class OragonTcpListener
    {
        public abstract void OnClientConnected(Socket client);

        static Logger _logger = LogManager.GetCurrentClassLogger();

        string _name;
        bool _isRunning = false;
        TcpListener _server = null;

        public int Port { get; private set; }

        public OragonTcpListener(string name, int port)
        {
            _name = name;
            Port = port;
        }

        public void Start()
        {
            _isRunning = true;

            _server = new TcpListener(IPAddress.Any, Port);

            Task.Run(async () =>
            {
                try
                {
                    _server.Start();
                    _logger.Info("Server: {0} started listening port: {1}", _name, Port);

                    while(_isRunning)
                    {
                        var client = await _server.AcceptSocketAsync();
                        OnClientConnected(client);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
                finally
                {
                    if(_isRunning == true)
                    {
                        _isRunning = false;
                        _server.Stop();
                    }
                }
            });
        }

        public void Stop()
        {
            _isRunning = false;

            try
            {
                _server.Stop();
            }
            catch (Exception)
            {
                throw;
            }
        }

    }
}
