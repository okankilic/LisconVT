using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Helpers
{
    public class MDVRMessageWriter
    {
        List<string> _argList;

        public MDVRMessageWriter(List<string> argList)
        {
            this._argList = argList;
        }

        public byte[] WriteToArray()
        {
            var byteList = new List<byte>();

            var startBytes = MdvrMessageHelper.GetBytes(MdvrMessageHelper.MessageStart);
            byteList.AddRange(startBytes);

            var dataString = String.Join(MdvrMessageHelper.Delimeter.ToString(), _argList);
            var dataBytes = MdvrMessageHelper.GetBytes(dataString);
            //dataBytes = MdvrMessageHelper.Escape(dataBytes, false);

            var dataLenString = (dataBytes.Length + 1).ToString().PadLeft(4, '0');
            var dataLenBytes = MdvrMessageHelper.GetBytes(dataLenString);

            byteList.AddRange(dataLenBytes);
            byteList.Add((byte)MdvrMessageHelper.Delimeter);
            byteList.AddRange(dataBytes);

            var endBytes = MdvrMessageHelper.GetBytes(MdvrMessageHelper.MessageEnd);
            byteList.AddRange(endBytes);

            return byteList.ToArray();
        }
    }
}
