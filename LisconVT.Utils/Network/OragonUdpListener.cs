﻿using NLog;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LisconVT.Utils.Network
{
    public abstract class OragonUdpListener
    {

        abstract public void OnMessageReceived(UdpReceiveResult result);

        //abstract public byte[] Parse(UdpReceiveResult result);
        abstract public void OnTimerElapsed();

        static Logger _logger = LogManager.GetCurrentClassLogger();
        
        int _port;
        bool _isRunning = false;
        UdpClient _server;

        System.Timers.Timer _timer;

        public OragonUdpListener(int port, double timerInterval)
        {
            _port = port;

            if(timerInterval > 0)
            {
                _timer = new System.Timers.Timer(timerInterval);

                _timer.Elapsed += _timer_Elapsed;
                _timer.AutoReset = true;
                _timer.Start();
            }
        }

        private void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Task.Run(() =>
            {
                OnTimerElapsed();
            });
        }

        public void Start()
        {
            _isRunning = true;

            _server = new UdpClient(_port);

            Task.Run(async () =>
            {
                await ReceiveMsgAsync();
            });
        }

        private async Task ReceiveMsgAsync()
        {
            try
            {
                while (_isRunning)
                {
                    var receiveResult = await _server.ReceiveAsync();
                    OnMessageReceived(receiveResult);
                    //var sendBytes = Parse(receiveResult);
                    //Send(receiveResult.RemoteEndPoint, sendBytes);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
            finally
            {
                if (_isRunning == true)
                {
                    _isRunning = false;
                    _server.Close();
                }
            }
        }

        public void Send(IPEndPoint ep, byte[] bytes)
        {
            if (bytes == null)
                return;

            if (bytes.Length == 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    await _server.SendAsync(bytes, bytes.Length, ep);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            });
        }

        //public async Task<int> SendAsync(IPEndPoint ep, byte[] bytes)
        //{
        //    int sentByteCnt = 0;

        //    if (bytes == null)
        //        return sentByteCnt;

        //    if (bytes.Length == 0)
        //        return sentByteCnt;
            
        //    try
        //    {
        //        sentByteCnt = await _server.SendAsync(bytes, bytes.Length, ep);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.Error(ex);
        //    }

        //    return sentByteCnt;
        //}

        public void Stop()
        {
            _isRunning = false;

            try
            {
                _server.Close();
            }
            catch (Exception)
            {
                throw;
            }
        }

        public void TriggerEvent()
        {

        }
    }
}
