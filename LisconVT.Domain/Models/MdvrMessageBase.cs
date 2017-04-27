using LisconVT.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class MdvrMessageBase
    {
        public string MessageKey { get; set; }
        public DateTime MessageTime { get; set; }
        public string DevIDNO { get; set; }

        public LocationAndStatusModel LocationAndStatus { get; set; }

        public MdvrMessageBase()
        {
            this.LocationAndStatus = new LocationAndStatusModel();
        }
    }
}
