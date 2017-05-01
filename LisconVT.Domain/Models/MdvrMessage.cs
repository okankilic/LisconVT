using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class MdvrMessage
    {
        public int MessageLength { get; set; }
        public string MessageType { get; set; }
        public string DevIDNO { get; set; }
        public List<byte> ByteList { get; set; }

        public MdvrMessage()
        {
            this.ByteList = new List<byte>();
        }
    }
}
