using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    /// <summary>
    /// $$dc0160,0,V251,34561,,170428 141247,A,+28,59,101953128,+41,3,32519532,0,0,0001000000105780,0000000000000000,0.00,0.00,0.00,,-1,0,0,0,170428 141246,2200719706,0,,0,,1,0#
    /// </summary>
    public class V251Message: MdvrMessageBase
    {
        public DateTime AlarmTime { get; set; }
        public string AlarmUID { get; set; }
        public bool IsImageCaptureEnabled { get; set; }
        public string ImagePath { get; set; }
        public bool IsVideoRecordEnabled { get; set; }
        public string VideoPath { get; set; }
        public int AlarmNo { get; set; }
        public string AlarmSource { get; set; }
        public string AlarmName { get; set; }

        public V251Message(string messageKey, string devIDNO, DateTime messageTime)
        {
            MessageKey = messageKey;
            DevIDNO = devIDNO;
            MessageTime = messageTime;
        }
    }
}
