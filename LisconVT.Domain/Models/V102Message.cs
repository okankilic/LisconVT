using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    /// <summary>
    /// $$dc0180,1d,V102,34561,,170429 133652,A,+28,59,102539064,+41,3,32226562,0,0,0001000000105780,0000000000000000,0.00,0.00,0.00,,-1,0,0,0,
    /// V0.00.01,
    /// 2,
    /// 176.240.208.9,
    /// 6602,
    /// 26636840,
    /// C508,
    /// 0,
    /// 1,
    /// 00000#
    /// </summary>
    public class V102Message: MdvrMessageBase
    {
        public string ProtocolVersion { get; set; }
        public string DeviceType { get; set; }
        public string MediaServerIp { get; set; }
        public int MediaServerPort { get; set; }
        public string SessionID { get; set; }
        public string MediaCommand { get; set; }
        public int ChannelNo { get; set; }
        public string FlowType { get; set; }
        public string Plate { get; set; }

        public V102Message(string messageKey, string devIDNO, DateTime messageTime): base()
        {
            MessageKey = messageKey;
            DevIDNO = devIDNO;
            MessageTime = messageTime;
        }
    }
}
