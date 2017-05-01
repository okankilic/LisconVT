using LisconVT.Domain.Enums;
using LisconVT.Domain.Helpers;
using LisconVT.Domain.Models;
using LisconVT.Utils.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Liscon.Demo.GatewayConsole.Models
{
    public delegate void OnMessageReceived(MediaClient client, MdvrMessageBase message);

    public class MediaClient : OragonTcpClient
    {
        short TxNo = 0;

        MdvrMessageReader _messageReader = null;
        MdvrMediaMessageReader _mediaMessageReader = null;

        bool _isInMediaMessageMode = false;

        public string DevIDNO { get; set; }

        public event OnMessageReceived MessageReceived = null;

        public MediaClient(Socket socket) : base(socket)
        {
            _messageReader = new MdvrMessageReader();
            _messageReader.MessageReceived += _messageReader_MessageReceived;

            _mediaMessageReader = new MdvrMediaMessageReader();
            _mediaMessageReader.MessageReceived += _mediaMessageReader_MessageReceived;
        }

        private void _mediaMessageReader_MessageReceived(MdvrMediaMessage message)
        {
            if(message.MessageType == MdvrMediaMessageTypes.RealTimeVideo)
            {
                using(var fs = File.Create(string.Format("{0}.h264", Guid.NewGuid())))
                {
                    using(var br = new BinaryWriter(fs))
                    {
                        br.Write(message.ByteList.ToArray());
                    }
                }
            }
        }

        private void _messageReader_MessageReceived(MdvrMessageBase message)
        {
            if (message.MessageKey == MdvrMessageTypes.V102)
                HandleV102Message((V102Message)message);

            MessageReceived?.Invoke(this, message);
        }

        private void HandleV102Message(V102Message message)
        {
            DevIDNO = message.DevIDNO;
            _isInMediaMessageMode = true;

            SendRegisterFeedback();
            SendFrameRequest();
        }

        //private void SendVoiceRequest()
        //{
        //    var byteList = GetBaseMediaMessageByteList(MdvrMediaMessageTypes.VoiceRequest);

        //    int isStreaming = 1;
        //    byteList.AddRange(BitConverter.GetBytes(isStreaming));

        //    SendMessage(byteList);
        //}

        private void SendRegisterFeedback()
        {
            var byteList = GetBaseMediaMessageByteList(MdvrMediaMessageTypes.RegisterFeedback);

            int response = 1;
            byteList.AddRange(BitConverter.GetBytes(response));

            int reason = 0;
            byteList.AddRange(BitConverter.GetBytes(reason));

            SendMessage(byteList);
        }

        private void SendFrameRequest()
        {
            var byteList = GetBaseMediaMessageByteList(MdvrMediaMessageTypes.FrameRequest);
            SendMessage(byteList);
        }

        private List<byte> GetBaseMediaMessageByteList(short mediaMessageType)
        {
            var byteList = new List<byte>();

            byteList.AddRange(BitConverter.GetBytes(mediaMessageType));
            byteList.AddRange(BitConverter.GetBytes(TxNo));
            TxNo++;

            int time = DateTime.Now.Hour * 3600 + DateTime.Now.Minute * 60 + DateTime.Now.Second;
            byteList.AddRange(BitConverter.GetBytes(time));

            return byteList;
        }

        private void SendMessage(List<byte> byteList)
        {
            using (var ms = new MemoryStream())
            {
                using (var br = new BinaryWriter(ms))
                {
                    br.Write(MdvrMessageHelper.GetBytes(MdvrMessageHelper.MediaMessageStart));
                    br.Write(byteList.Count);
                    br.Write(byteList.ToArray());
                    br.Write(MdvrMessageHelper.GetBytes(MdvrMessageHelper.MediaMessageEnd));

                    var bytes = ms.ToArray();

                    Send(bytes);
                }
            }
        }

        public override byte[] GetResponse(byte[] bytes, int byteCnt)
        {
            if (_isInMediaMessageMode == true)
                _mediaMessageReader.Read(bytes, byteCnt);
            else
                _messageReader.Read(bytes, byteCnt);

            return null;
        }
    }
}
