using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    /// <summary>
    /// $$dc0184,0,V201,34561,,170428 141247,A,+28,59,101953128,+41,3,32519532,0,0,0001000000105780,0000000000000000,0.00,0.00,0.00,,-1,0,0,0,
    /// 170428 141246,
    /// 2200719705,
    /// 1,
    /// /sd/chn0.jpg,
    /// 0,
    /// ,
    /// 1,
    /// input1,
    /// input1#
    /// </summary>
    public class V201Message: MdvrMessageBase
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

        public V201Message(string key, string devIDNO, DateTime time): base()
        {
            MessageKey = key;
            DevIDNO = devIDNO;
            MessageTime = time;
        }
    }
}
