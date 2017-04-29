using LisconVT.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Liscon.Demo.GatewayConsole.Models
{
    public class DeviceClient
    {
        public IPAddress Ip { get; private set; }
        public int Port { get; private set; }
        public string DevIDNO { get; private set; }
        public DeviceRuntime Runtime { get; set; }
        public IPEndPoint EndPoint
        {
            get
            {
                return new IPEndPoint(Ip, Port);
            }
        }

        public DeviceClient(string devIDNO, IPAddress ip, int port)
        {
            DevIDNO = devIDNO;
            Ip = ip;
            Port = port;
            Runtime = new DeviceRuntime()
            {
                DevIDNO = devIDNO
            };
        }
    }
}
