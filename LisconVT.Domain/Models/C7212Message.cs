using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class C7212Message: MdvrMessageBase
    {
        public int TxNo { get; set; }

        public string AlarmServerIp { get; set; }
        public int AlarmServerPort { get; set; }
        public int ConnectionType { get; set; }

        public C7212Message(string messageKey, string devIDNO, DateTime messageTime) : base()
        {
            MessageKey = messageKey;
            DevIDNO = devIDNO;
            MessageTime = messageTime;
        }
    }
}
