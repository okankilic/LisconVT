using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class MdvrMediaMessage
    {
        public int MessageLength { get; set; }
        public short MessageType { get; set; }
        public short SerialNo { get; set; }
        public int Tick { get; set; }
        public List<byte> ByteList { get; set; }

        public int DataLength { get
            {
                return MessageLength - 8;
            }
        }

        public MdvrMediaMessage()
        {
            ByteList = new List<byte>();
        }
    }
}
