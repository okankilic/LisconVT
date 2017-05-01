using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class V100Message: MdvrMessageBase
    {
        public string Command { get; set; }
        public DateTime CommandTime { get; set; }
        public int ResponseMode { get; set; }
        public List<string> ResponseArgs { get; set; }

        public V100Message(string messageKey, string devIDNO, DateTime messageTime) : base()
        {
            MessageKey = messageKey;
            DevIDNO = devIDNO;
            MessageTime = messageTime;

            ResponseArgs = new List<string>();
        }
    }
}
