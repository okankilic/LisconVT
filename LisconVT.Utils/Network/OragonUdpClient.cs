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
        public string Ip { get; private set; }
        public int Port { get; private set; }

        UdpClient _client = null;

        public OragonUdpClient(string devIDNO, string ip, int port)
        {
            DevIDNO = devIDNO;
            Ip = ip;
            Port = port;

            _client = new UdpClient();
        }
    }
}
