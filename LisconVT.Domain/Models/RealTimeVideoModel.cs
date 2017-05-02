using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class RealTimeVideoModel
    {
        public int FrameType { get; set; }
        public int DataLength { get; set; }
        public long Timestamp { get; set; }
        public byte[] Buffer { get; set; }

        public RealTimeVideoModel()
        {

        }
    }
}
