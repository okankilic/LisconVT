using LisconVT.Domain.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Helpers
{
    public class LocationHelper
    {
        public static DecimalLocation Convert(DmsLocation dmsLocation)
        {
            if (dmsLocation == null)
            {
                return null;
            }

            return new DecimalLocation
            {
                Latitude = CalculateDecimal(dmsLocation.Latitude),
                Longitude = CalculateDecimal(dmsLocation.Longitude)
            };
        }

        public static decimal CalculateDecimal(DmsPoint point)
        {
            if (point == null)
            {
                return default(decimal);
            }

            return Math.Round(point.Degrees + (decimal)point.Minutes / 60 + (decimal)point.Seconds / 3600, 6);
        }
    }
}
