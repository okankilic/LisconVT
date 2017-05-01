using LisconVT.Domain.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Helpers
{
    public delegate void OnMediaMessageReceived(MdvrMediaMessage message);

    public class MdvrMediaMessageReader
    {
        MediaMessageFields _field = MediaMessageFields.Start;
        MdvrMediaMessage _msg = null;

        public event OnMediaMessageReceived MessageReceived = null;

        public void Read(byte[] bytes, int byteCnt)
        {
            using (var ms = new MemoryStream(bytes))
            {
                using (var br = new BinaryReader(ms))
                {
                    while (ms.Position < byteCnt)
                    {
                        switch (_field)
                        {
                            case MediaMessageFields.Start:
                                if (MdvrMessageHelper.GetString(br.ReadBytes(8)) == MdvrMessageHelper.MediaMessageStart)
                                {
                                    _msg = new MdvrMediaMessage();
                                    _field = MediaMessageFields.MessageLength;
                                }
                                break;

                            case MediaMessageFields.MessageLength:
                                _msg.MessageLength = br.ReadInt32();
                                _field = MediaMessageFields.MessageType;
                                break;

                            case MediaMessageFields.MessageType:
                                _msg.MessageType = br.ReadInt16();
                                _field = MediaMessageFields.SerialNo;
                                break;

                            case MediaMessageFields.SerialNo:
                                _msg.SerialNo = br.ReadInt16();
                                _field = MediaMessageFields.Time;
                                break;

                            case MediaMessageFields.Time:
                                _msg.Tick = br.ReadInt32();
                                _field = MediaMessageFields.Data;
                                break;

                            case MediaMessageFields.Data:
                                if (_msg.ByteList.Count < _msg.DataLength)
                                {
                                    _msg.ByteList.Add(br.ReadByte());
                                    if (_msg.ByteList.Count == _msg.DataLength)
                                        _field = MediaMessageFields.End;
                                }
                                break;

                            case MediaMessageFields.End:
                                if (MdvrMessageHelper.GetString(br.ReadBytes(4)) == MdvrMessageHelper.MediaMessageEnd)
                                {
                                    MessageReceived?.Invoke(_msg);
                                }
                                _field = MediaMessageFields.Start;
                                break;
                        }
                    }
                }
            }
        }
    }

    enum MediaMessageFields
    {
        Start,
        MessageLength,
        MessageType,
        SerialNo,
        Time,
        Data,
        End
    }
}
