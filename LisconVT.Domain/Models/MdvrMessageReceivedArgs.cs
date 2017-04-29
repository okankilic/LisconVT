using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class MdvrMessageReceivedArgs
    {
        public string DevIDNO { get; set; }
        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        public object Message { get; set; }
    }
}
