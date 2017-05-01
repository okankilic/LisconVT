using LisconVT.Domain.Enums;
using LisconVT.Domain.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LisconVT.Utils.Extensions;

namespace LisconVT.Domain.Helpers
{

    public class MdvrMessageHelper
    {
        public const string DateTimeStringFormat = "yyMMdd HHmmss";
        public const string MediaMessageStart = "$$$$$$dc";
        public const string MediaMessageEnd = "####";
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

        public static MdvrMessageBase Parse(IEnumerable<byte> bytes)
        {
            var messageString = GetString(bytes.ToArray());
            var args = messageString.Split(Delimeter);

            var messageKey = args[2];
            var devIDNO = args[3];
            var messageTime = args[5].ToDateTime();

            if (messageKey == MdvrMessageTypes.V100)
                return ReadV100Message(args, messageKey, devIDNO, messageTime);
            else if (messageKey == MdvrMessageTypes.V101)
                return ReadV101Message(args, messageKey, devIDNO, messageTime);
            else if (messageKey == MdvrMessageTypes.V102)
                return ReadV102Message(args, messageKey, devIDNO, messageTime);
            else if (messageKey == MdvrMessageTypes.V114)
                return ReadV114Message(args, messageKey, devIDNO, messageTime);
            else if (messageKey == MdvrMessageTypes.V201)
                return ReadV201Message(args, messageKey, devIDNO, messageTime);
            else if (messageKey == MdvrMessageTypes.V251)
                return ReadV251Message(args, messageKey, devIDNO, messageTime);
            else
                return null;
        }

        private static MdvrMessageBase ReadV102Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V102Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);

            message.ProtocolVersion = args[25];
            message.DeviceType = args[26];
            message.MediaServerIp = args[27];
            message.MediaServerPort = int.Parse(args[28]);
            message.SessionID = args[29];
            message.MediaCommand = args[30];
            message.ChannelNo = int.Parse(args[31]);
            message.FlowType = args[32];
            message.Plate = args[33];

            return message;
        }

        private static V100Message ReadV100Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V100Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);

            message.Command = args[25];
            message.CommandTime = args[26].ToDateTime();
            message.ResponseMode = int.Parse(args[27]);
            
            for(int i = 26; i < args.Length; i++)
            {
                message.ResponseArgs.Add(args[i]);
            }

            return message;
        }

        private static V251Message ReadV251Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V251Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);

            message.AlarmTime = args[25].ToDateTime();
            message.AlarmUID = args[26];
            message.IsImageCaptureEnabled = args[27].ToBool();
            message.ImagePath = args[28];
            message.IsVideoRecordEnabled = args[29].ToBool();
            message.VideoPath = args[30];
            message.AlarmSource = args[31];
            message.AlarmName = args[32];

            return message;
        }

        private static V201Message ReadV201Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V201Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);

            message.AlarmTime = args[25].ToDateTime();
            message.AlarmUID = args[26];
            message.IsImageCaptureEnabled = args[27].ToBool();
            message.ImagePath = args[28];
            message.IsVideoRecordEnabled = args[29].ToBool();
            message.VideoPath = args[30];
            message.AlarmSource = args[31];
            message.AlarmName = args[32];

            return message;
        }

        private static V114Message ReadV114Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V114Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);

            return message;
        }

        private static V101Message ReadV101Message(string[] args, string messageKey, string devIDNO, DateTime messageTime)
        {
            var message = new V101Message(messageKey, devIDNO, messageTime);

            message.LocationAndStatus = ParseLocationAndStatus(args);
            message.ProtocolVersion = args[25];
            message.DeviceType = int.Parse(args[26]);
            message.ServerIpAddress = args[27];
            message.ServerPort = int.Parse(args[28]);

            // todo: parse the rest 

            return message;
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
