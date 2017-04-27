using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class DmsLocation
    {
        public DmsPoint Latitude { get; set; }
        public DmsPoint Longitude { get; set; }

        public DmsLocation()
        {
            this.Latitude = new DmsPoint();
            this.Longitude = new DmsPoint();
        }
    }

    public class DecimalLocation
    {
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
    }

    public class DmsPoint
    {
        public int Degrees { get; set; }
        public int Minutes { get; set; }
        public int Seconds { get; set; }
    }
}
