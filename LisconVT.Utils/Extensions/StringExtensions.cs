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
            var datetime = DateTime.MinValue;

            try
            {
                datetime = DateTime.ParseExact(str, "yyMMdd HHmmss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {

            }

            return datetime;
        }
    }
}
