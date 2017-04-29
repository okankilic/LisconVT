using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Utils.Extensions
{
    public static class StringExtensions
    {
        public static bool ToBool(this string str)
        {
            return str == "1";
        }

        public static DateTime ToDateTime(this string str)
        {
            return DateTime.ParseExact(str, "yyMMdd HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
