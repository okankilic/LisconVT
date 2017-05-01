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
    public delegate void OnMessageRead( MdvrMessageBase message);

    public class MdvrMessageReader
    {
        MessageFields _field = MessageFields.Start;
        MdvrMessage _msg = null;

        public event OnMessageRead MessageReceived = null;

        public void Read(byte[] bytes, int byteCnt)
        {
            //bytes = MdvrMessageHelper.Escape(bytes);

            using (var ms = new MemoryStream(bytes))
            {
                using (var br = new BinaryReader(ms))
                {
                    while (ms.Position < byteCnt)
                    {
                        switch (_field)
                        {
                            case MessageFields.Start:
                                {
                                    if (MdvrMessageHelper.GetString(br.ReadBytes(4)) == MdvrMessageHelper.MessageStart)
                                    {
                                        _msg = new MdvrMessage();
                                        _field = MessageFields.Length;
                                    }
                                }
                                break;

                            case MessageFields.Length:
                                {
                                    var fieldString = MdvrMessageHelper.GetString(br.ReadBytes(4));
                                    _msg.MessageLength = int.Parse(fieldString);

                                    if (_msg.MessageLength > 0)
                                        _field = MessageFields.Data;
                                    else
                                        _field = MessageFields.End;
                                }
                                break;

                            case MessageFields.Data:
                                {
                                    if (_msg.ByteList.Count < _msg.MessageLength)
                                    {
                                        _msg.ByteList.Add(br.ReadByte());
                                        if (_msg.ByteList.Count == _msg.MessageLength)
                                            _field = MessageFields.End;
                                    }
                                }
                                break;

                            case MessageFields.End:
                                {
                                    if (MdvrMessageHelper.GetString(br.ReadBytes(1)) == MdvrMessageHelper.MessageEnd)
                                    {
                                        var mdvrMessage = MdvrMessageHelper.Parse(_msg.ByteList);
                                        MessageReceived?.Invoke(mdvrMessage);
                                        //var message = MdvrMessageHelper.Parse(_msg.ByteList);
                                        //if(message is MdvrMessageBase)
                                        //{
                                        //    MdvrMessageReceivedArgs args = new MdvrMessageReceivedArgs()
                                        //    {
                                        //        DevIDNO = (message as MdvrMessageBase).DevIDNO,
                                        //        IpAddress = ip,
                                        //        Port = port,
                                        //        Message = message
                                        //    };

                                        //    MessageReceived?.Invoke(args);
                                        //}
                                    }
                                    _field = MessageFields.Start;
                                }
                                break;
                        }
                    }
                }
            }
        }
    }

    enum MessageFields
    {
        Start,
        Length,
        Data,
        End
    }
}
