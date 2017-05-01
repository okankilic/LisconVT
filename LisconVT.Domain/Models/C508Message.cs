using LisconVT.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class C508Message: MdvrMessageBase
    {
        public string UID { get; set; }
        public bool IsStart { get; set; }
        public int ChannelNo { get; set; }
        public int FlowType { get; set; }
        public int ConnectionType { get; set; }
        public string MediaServerIp { get; set; }
        public int MediaServerPort { get; set; }

        public C508Message()
        {
            var guid = Guid.NewGuid();
            UID = MdvrMessageHelper.GetString(guid.ToByteArray());
        }
    }
}
