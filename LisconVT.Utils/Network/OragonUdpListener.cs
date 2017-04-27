using NLog;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace LisconVT.Utils.Network
{
    public delegate void OnUdpMessageReceived(string ip, int port, byte[] bytes);

    public class OragonUdpListener
    {
        static Logger _logger = LogManager.GetCurrentClassLogger();
        
        int _port;

        bool _isRunning = false;

        public event OnUdpMessageReceived DataReceived = null;

        public OragonUdpListener(int port)
        {
            _port = port;
        }

        void Listen(object args)
        {
            var server = new UdpClient(_port);
            var ep = new IPEndPoint(IPAddress.Any, _port);

            try
            {
                while (_isRunning)
                {
                    if (server.Available == 0)
                        continue;

                    var bytes = server.Receive(ref ep);
                    DataReceived?.Invoke(ep.Address.ToString(), ep.Port, bytes);

                    Thread.Sleep(10);
                }
            }
            catch(Exception ex)
            {
                _logger.Error(ex);
            }
            finally
            {
                server.Close();
            }
        }

        public void Start()
        {
            _isRunning = true;
            ThreadPool.QueueUserWorkItem(Listen);
        }

        public void Stop()
        {
            _isRunning = false;
        }
    }
}
