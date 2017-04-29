using LisconVT.Domain.Delegates;
using LisconVT.Domain.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Helpers
{
    public class MdvrMessageReader
    {
        MDVRMessageFields Field { get; set; }

        List<byte> ByteList { get; set; }
        int DataLength { get; set; }
        int DataIndex { get; set; }

        public event OnMdvrMessageReceived MessageReceived = null;

        public void Read(IPAddress ip, int port, byte[] bytes)
        {
            bytes = MdvrMessageHelper.Escape(bytes);

            using (var ms = new MemoryStream(bytes))
            {
                using (var br = new BinaryReader(ms))
                {
                    while (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        switch (Field)
                        {
                            case MDVRMessageFields.Start:
                                {
                                    var fieldString = MdvrMessageHelper.GetString(br.ReadBytes(4));
                                    if (fieldString == MdvrMessageHelper.MessageStart)
                                        Field = MDVRMessageFields.Length;
                                }
                                break;

                            case MDVRMessageFields.Length:
                                {
                                    var fieldString = MdvrMessageHelper.GetString(br.ReadBytes(4));
                                    DataLength = int.Parse(fieldString);
                                    if (DataLength > 0)
                                    {
                                        ByteList = new List<byte>();
                                        DataIndex = 0;
                                        Field = MDVRMessageFields.Data;
                                    }
                                    else
                                        Field = MDVRMessageFields.End;
                                }
                                break;

                            case MDVRMessageFields.Data:
                                {
                                    if (DataIndex < DataLength)
                                    {
                                        ByteList.Add(br.ReadByte());
                                        DataIndex++;
                                        if (DataIndex == DataLength)
                                            Field = MDVRMessageFields.End;
                                    }
                                }
                                break;

                            case MDVRMessageFields.End:
                                {
                                    var fieldString = MdvrMessageHelper.GetString(br.ReadBytes(1));
                                    if (fieldString == MdvrMessageHelper.MessageEnd)
                                    {
                                        var message = MdvrMessageHelper.Parse(ByteList);
                                        if(message is MdvrMessageBase)
                                        {
                                            MdvrMessageReceivedArgs args = new MdvrMessageReceivedArgs()
                                            {
                                                DevIDNO = (message as MdvrMessageBase).DevIDNO,
                                                IpAddress = ip,
                                                Port = port,
                                                Message = message
                                            };

                                            MessageReceived?.Invoke(args);
                                        }
                                    }
                                    Field = MDVRMessageFields.Start;
                                }
                                break;
                        }
                    }
                }
            }
        }
    }
}
