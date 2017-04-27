using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class MdvrMessageReceivedArgs
    {
        public string DevIDNO { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public object Message { get; set; }
    }
}
