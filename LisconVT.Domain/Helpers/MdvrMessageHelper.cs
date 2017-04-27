using LisconVT.Domain.Delegates;
using LisconVT.Domain.Enums;
using LisconVT.Domain.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Helpers
{
    enum MDVRMessageFields
    {
        Start,
        Length,
        Data,
        End
    }

    public class MdvrMessageHelper
    {
        public const string MessageStart = "$$dc";
        public const string MessageEnd = "#";
        public const char Delimeter = ',';

        public static readonly Dictionary<byte, short> EscapeChars = new Dictionary<byte, short>()
        {
            { 0x2c, 0x0f00 },
            { 0x23, 0x0f01 },
            { 0x0f, 0x0f02 }
        };

        public static byte[] Escape(byte[] bytes, bool isReceived = true)
        {
            List<byte> byteList = new List<byte>();

            using (MemoryStream ms = new MemoryStream(bytes))
            {
                using (var br = new BinaryReader(ms))
                {
                    while (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        long remainingByteCnt = br.BaseStream.Length - br.BaseStream.Position;

                        if (isReceived == true)
                        {
                            if (remainingByteCnt == 1)
                                byteList.Add(br.ReadByte());
                            else
                            {
                                short escapeValue = BitConverter.ToInt16(br.ReadBytes(2), 0);
                                if (EscapeChars.ContainsValue(escapeValue) == true)
                                {
                                    var escapeChar = EscapeChars.First(q => q.Value == escapeValue).Key;
                                    byteList.Add(escapeChar);
                                }
                                else
                                    byteList.AddRange(BitConverter.GetBytes(escapeValue));
                            }
                        }
                        else
                        {
                            var currByte = br.ReadByte();
                            if(EscapeChars.ContainsKey(currByte) == true)
                            {
                                var escapeValue = EscapeChars[currByte];
                                var escapeValueBytes = BitConverter.GetBytes(escapeValue);
                                byteList.AddRange(escapeValueBytes);
                            }
                            else
                                byteList.Add(currByte);
                        }
                    }
                }
            }

            return byteList.ToArray();
        }

        public static string GetString(byte[] bytes)
        {
            return Encoding.ASCII.GetString(bytes);
        }

        public static byte[] GetBytes(string message)
        {
            return Encoding.ASCII.GetBytes(message);
        }

        public static object Parse(IEnumerable<byte> bytes)
        {
            var messageString = GetString(bytes.ToArray());
            var args = messageString.Split(Delimeter);

            string messageKey = args[2];

            if (messageKey == MdvrMessageKeys.V101)
            {
                var message = new V101Message();

                //message.TransmissionNo = args[1];
                message.MessageKey = messageKey;
                message.DevIDNO = args[3];
                //message.StationNo = args[4];
                message.MessageTime = DateTime.ParseExact(args[5], "yyMMdd HHmmss", CultureInfo.InvariantCulture);

                message.LocationAndStatus = ParseLocationAndStatus(args);

                message.ProtocolVersion = args[25];
                message.DeviceType = int.Parse(args[26]);
                message.ServerIpAddress = args[27];
                message.ServerPort = int.Parse(args[28]);
                
                // todo: parse the rest 

                return message;
            }
            else if (messageKey == MdvrMessageKeys.V114)
            {
                var message = new V114Message();

                //message.TransmissionNo = args[1];
                message.MessageKey = messageKey;
                message.DevIDNO = args[3];
                //message.StationNo = args[4];
                message.MessageTime = DateTime.ParseExact(args[5], "yyMMdd HHmmss", CultureInfo.InvariantCulture);

                message.LocationAndStatus = ParseLocationAndStatus(args);

                return message;
            }

            return null;
        }

        public static LocationAndStatusModel ParseLocationAndStatus(string[] args)
        {
            var locAndStatus = new LocationAndStatusModel();

            locAndStatus.GpsState = args[6];

            DmsLocation dmsLocation = new DmsLocation();

            dmsLocation.Longitude.Degrees = int.Parse(args[7]);
            dmsLocation.Longitude.Minutes = int.Parse(args[8]);
            dmsLocation.Longitude.Seconds = (int)(double.Parse(args[9]) / Math.Pow(10, 7));

            dmsLocation.Latitude.Degrees = int.Parse(args[10]);
            dmsLocation.Latitude.Minutes = int.Parse(args[11]);
            dmsLocation.Latitude.Seconds = (int)(double.Parse(args[12]) / Math.Pow(10, 7));

            locAndStatus.Location = LocationHelper.Convert(dmsLocation);

            locAndStatus.Speed = int.Parse(args[13]) / 100;
            locAndStatus.Course = int.Parse(args[14]) / 100;

            locAndStatus.Status = args[15];
            locAndStatus.Mask = args[16];

            locAndStatus.DevTemp = decimal.Parse(args[17]);
            locAndStatus.EngineTemp = decimal.Parse(args[18]);
            locAndStatus.VehTemp = decimal.Parse(args[19]);

            return locAndStatus;
        }
    }
}
