using LisconVT.BL;
using LisconVT.Domain.Enums;
using LisconVT.Domain.Helpers;
using LisconVT.Domain.Models;
using LisconVT.Entities;
using LisconVT.Utils.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Liscon.Demo.GatewayConsole.Models
{
    public delegate void OnClientConnected();
    public delegate void OnClientDisconnected();
    public delegate void OnClientRuntimeUpdated();

    public class GatewayUdpServer : OragonUdpListener
    {
        int TxNo = 0;
        public ConcurrentDictionary<string, DeviceClient> Clients { get; set; }
        public ConcurrentDictionary<string, DeviceAlarm> Alarms { get; set; }

        public event OnClientConnected ClientConnected = null;
        public event OnClientDisconnected ClientDisconnected = null;
        public event OnClientRuntimeUpdated ClientRuntimeUpdated = null;

        public GatewayUdpServer(int port, double timerInterval) : base(port, timerInterval)
        {
            Clients = new ConcurrentDictionary<string, DeviceClient>();
            Alarms = new ConcurrentDictionary<string, DeviceAlarm>();
        }

        void ValidateMessage(MdvrMessageReceivedArgs args)
        {
            if (args.Message is MdvrMessageBase == false)
                throw new Exception("Not Mdvr");
        }

        byte[] GetResponse(MdvrMessageReceivedArgs args, DeviceClient client)
        {
            var db = new LisconDbEntities();

            var msgBase = args.Message as MdvrMessageBase;

            client.Runtime.GpsTime = msgBase.MessageTime;
            client.Runtime.Latitude = msgBase.LocationAndStatus.Location.Latitude;
            client.Runtime.Longitude = msgBase.LocationAndStatus.Location.Longitude;

            List<string> responseArgList = new List<string>();

            if (args.Message is V101Message)
            {
                var msg = args.Message as V101Message;

                var device = DeviceBL.TryGet(msg.DevIDNO, db);
                if (device == null)
                    DeviceBL.Register(args.DevIDNO, client.Runtime, db);

                responseArgList = new List<string>()
                {
                    MdvrMessageKeys.C100,
                    client.DevIDNO,
                    null,
                    DateTime.UtcNow.ToString("yyMMdd HHmmss"),
                    msg.MessageKey,
                    msg.MessageTime.ToString("yyMMdd HHmmss"),
                    MdvrResponseTypes.Auto,
                    "1"
                };
            }
            else if (args.Message is V114Message)
            {
                var msg = args.Message as V114Message;

                var log = new DeviceLog()
                {
                    DevIDNO = client.DevIDNO,
                    LogType = (int)LogTypes.Gps,
                    LogTime = client.Runtime.GpsTime,
                    Latitude = client.Runtime.Latitude,
                    Longitude = client.Runtime.Longitude
                };

                DeviceLogBL.Add(log, db);
            }
            else if (args.Message is V201Message)
            {
                var msg = args.Message as V201Message;

                responseArgList = new List<string>()
                {
                    MdvrMessageKeys.C100,
                    client.DevIDNO,
                    null,
                    DateTime.UtcNow.ToString("yyMMdd HHmmss"),
                    msg.MessageKey,
                    msg.MessageTime.ToString("yyMMdd HHmmss"),
                    MdvrResponseTypes.Auto,
                    msg.AlarmUID
                };

                if (Alarms.ContainsKey(msg.AlarmUID) == false)
                {
                    Alarms.TryAdd(msg.AlarmUID, new DeviceAlarm()
                    {
                        Name = msg.AlarmName,
                        StartTime = msg.AlarmTime
                    });
                }
            }
            else if (args.Message is V251Message)
            {
                var msg = args.Message as V251Message;

                responseArgList = new List<string>()
                {
                    MdvrMessageKeys.C100,
                    client.DevIDNO,
                    null,
                    DateTime.UtcNow.ToString("yyMMdd HHmmss"),
                    msg.MessageKey,
                    msg.MessageTime.ToString("yyMMdd HHmmss"),
                    MdvrResponseTypes.Auto,
                    msg.AlarmUID
                };

                if (Alarms.ContainsKey(msg.AlarmUID) == true)
                {
                    Alarms[msg.AlarmUID].EndTime = msg.AlarmTime;
                }
            }

            DeviceRuntimeBL.Update(client.Runtime, db);
            ClientRuntimeUpdated?.Invoke();

            return GetSendMessageBytes(responseArgList);
        }

        void SendMessage(IPEndPoint ep, List<string> argList)
        {
            var bytes = GetSendMessageBytes(argList);
            Send(ep, bytes);
        }

        byte[] GetSendMessageBytes(List<string> argList)
        {
            var bytes = new byte[] { };
            if (argList.Count == 0)
                return bytes;

            System.Threading.Interlocked.Increment(ref TxNo);
            argList.Insert(0, (TxNo % 10000).ToString());

            var writer = new MDVRMessageWriter(argList);

            bytes = writer.WriteToArray();

            return bytes;
        }

        public override byte[] Parse(UdpReceiveResult result)
        {
            if (result.Buffer == null)
                return result.Buffer;

            if (result.Buffer.Length == 0)
                return result.Buffer;

            var message = MdvrMessageHelper.Parse(result.Buffer);

            MdvrMessageReceivedArgs args = new MdvrMessageReceivedArgs()
            {
                DevIDNO = (message as MdvrMessageBase).DevIDNO,
                IpAddress = result.RemoteEndPoint.Address,
                Port = result.RemoteEndPoint.Port,
                Message = message
            };

            ValidateMessage(args);

            var client = new DeviceClient(args.DevIDNO, args.IpAddress, args.Port);

            if (Clients.ContainsKey(args.DevIDNO) == true)
                client = Clients[args.DevIDNO];
            else
            {
                if (Clients.TryAdd(args.DevIDNO, client) == true)
                {
                    ClientConnected?.Invoke();
                }
            }

            return GetResponse(args, client);
        }

        public override void OnTimerElapsed()
        {
            if (Clients.Count == 0)
                return;

            foreach (var client in Clients.Values)
            {
                var timeout = (DateTime.Now - client.Runtime.GpsTime).TotalSeconds;
                if (timeout > 120)
                {
                    DeviceClient clientToRemove;
                    if (Clients.TryRemove(client.DevIDNO, out clientToRemove) == true)
                    {
                        ClientDisconnected?.Invoke();
                    }
                    continue;
                }

                var argList = new List<string>()
                {
                    MdvrMessageKeys.C501,
                    client.DevIDNO,
                    null,
                    DateTime.UtcNow.ToString("yyMMdd HHmmss")
                };

                SendMessage(client.EndPoint, argList);
            }
        }
    }
}
