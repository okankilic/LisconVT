using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LisconVT.Utils.Network
{
    public delegate void OnDataReceived(string id, byte[] bytes);

    public class OragonTcpClient
    {
        TcpClient _client = null;
        NetworkStream _stream = null;
        static Logger _logger = LogManager.GetCurrentClassLogger();

        const int MaxDataBufferLength = 256;

        public Guid Guid { get; private set; }
        public string ID { get; private set; }

        public event OnDataReceived DataReceived = null;

        public OragonTcpClient(TcpClient client)
        {
            Guid = Guid.NewGuid();
            ID = "0";

            _client = client;
            _stream = _client.GetStream();
        }

        public void ReadAsync()
        {
            ThreadPool.QueueUserWorkItem(Read);
        }

        private async void Read(object x)
        {
            try
            {
                while (true)
                {
                    var bytes = new Byte[MaxDataBufferLength];

                    int byteCnt = await _stream.ReadAsync(bytes, 0, MaxDataBufferLength);
                    if (byteCnt > 0)
                        DataReceived?.Invoke(ID, bytes);

#if DEBUG
                    string msg = Encoding.ASCII.GetString(bytes, 0, bytes.Length);
                    _logger.Info(msg);
#endif

                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
        }

        public async void Write(byte[] buffer)
        {
            await _stream.WriteAsync(buffer, 0, buffer.Length);

            _logger.Info(Encoding.ASCII.GetString(buffer));
        }
    }
}
