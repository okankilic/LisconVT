using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    /// <summary>
    /// $$dc0165,205,V101,34561,,170427 162322,A,+29,2,509033216,+41,1,457910144,0,0,0001000000107780,0000000000000000,0.00,0.00,0.00,,-1,0,0,0,
    /// V0.00.01,
    /// 2,
    /// 52.170.27.175,
    /// 6608,
    /// ,
    /// ,
    /// 00000#
    /// </summary>
    public class V101Message: MdvrMessageBase
    {
        public string ProtocolVersion { get; set; }
        public int DeviceType { get; set; }
        public string ServerIpAddress { get; set; }
        public int ServerPort { get; set; }
        public int RestartCnt { get; set; }
        public int ConnectionCnt { get; set; }
        public string Plate { get; set; }

        public V101Message(string messageKey, string devIDNO, DateTime messageTime): base()
        {
            MessageKey = messageKey;
            DevIDNO = devIDNO;
            MessageTime = messageTime;
        }
    }
}
