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
    public abstract class OragonTcpClient
    {
        public abstract byte[] GetResponse(byte[] bytes, int byteCnt);

        static Logger _logger = LogManager.GetCurrentClassLogger();

        TcpClient _client = null;
        NetworkStream _stream = null;
        bool _isRunning = false;

        const int MaxDataBufferLength = 2048;

        public OragonTcpClient(Socket socket)
        {
            _client = new TcpClient();
            _client.Client = socket;
            _stream = _client.GetStream();
        }

        public void Start()
        {
            _isRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    while (_isRunning)
                    {
                        var receiveBytes = new Byte[MaxDataBufferLength];
                        int receiveByteCnt = await _stream.ReadAsync(receiveBytes, 0, MaxDataBufferLength);

                        if (receiveByteCnt == 0)
                            continue;

                        var responseBytes = GetResponse(receiveBytes, receiveByteCnt);
                        Send(responseBytes);

                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
                finally
                {
                    if(_isRunning == true)
                    {
                        _isRunning = false;
                        _client.Close();
                    }
                }
            });
        }

        public void Stop()
        {
            _isRunning = false;

            try
            {
                _client.Close();
            }
            catch (Exception)
            {

                throw;
            }
        }

        public void Send(byte[] bytes)
        {
            if (bytes == null)
                return;

            if (bytes.Length == 0)
                return;

            Task.Run(async () =>
            {
                await _stream.WriteAsync(bytes, 0, bytes.Length);
            });
        }
    }
}
