using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Utils.Network
{
    public class OragonUdpClient
    {
        static Logger _logger = LogManager.GetCurrentClassLogger();

        public string DevIDNO { get; private set; }
        public IPAddress Ip { get; private set; }
        public int Port { get; private set; }

        UdpClient _client = null;

        public OragonUdpClient(string devIDNO, string ip, int port)
        {
            DevIDNO = devIDNO;
            Ip = IPAddress.Parse(ip);
            Port = port;

            _client = new UdpClient();
        }

        public void Send(byte[] bytes)
        {
            try
            {
                var ep = new IPEndPoint(Ip, Port);
                _client.Send(bytes, bytes.Length, ep);
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
        }
    }
}
