using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    public class LocationAndStatusModel
    {
        public string GpsState { get; set; }
        public int Course { get; set; }
        public int Speed { get; set; }
        public DecimalLocation Location { get; set; }
        public string Status { get; set; }
        public string Mask { get; set; }
        public decimal DevTemp { get; set; }
        public decimal EngineTemp { get; set; }
        public decimal VehTemp { get; set; }
    }
}
