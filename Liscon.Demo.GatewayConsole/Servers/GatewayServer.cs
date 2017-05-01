using Liscon.Demo.GatewayConsole.Models;
using LisconVT.BL;
using LisconVT.Domain.Enums;
using LisconVT.Domain.Helpers;
using LisconVT.Domain.Models;
using LisconVT.Entities;
using LisconVT.Utils.Extensions;
using LisconVT.Utils.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Liscon.Demo.GatewayConsole.Servers
{
    public delegate void OnClientConnected(string devIDNO);
    public delegate void OnClientDisconnected();
    public delegate void OnClientRuntimeUpdated();

    public class GatewayServer : OragonUdpListener
    {
        int TxNo = 0;

        public ConcurrentDictionary<string, DeviceClient> Clients { get; set; }
        public ConcurrentDictionary<string, DeviceAlarm> Alarms { get; set; }

        public event OnClientConnected ClientConnected = null;
        public event OnClientDisconnected ClientDisconnected = null;
        public event OnClientRuntimeUpdated ClientRuntimeUpdated = null;

        public GatewayServer(int port, double timerInterval) : base(port, timerInterval)
        {
            Clients = new ConcurrentDictionary<string, DeviceClient>();
            Alarms = new ConcurrentDictionary<string, DeviceAlarm>();
        }

        public void SendMessage(IPEndPoint ep, List<string> argList)
        {
            if (argList.Count == 0)
                throw new Exception("No argument(s) for message");

            System.Threading.Interlocked.Increment(ref TxNo);
            argList.Insert(0, (TxNo % 10000).ToString());

            var writer = new MDVRMessageWriter(argList);

            Send(ep, writer.WriteToArray());
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

                SendHeartbeat(client.DevIDNO);
            }
        }

        public override void OnMessageReceived(UdpReceiveResult result)
        {
            if (result.Buffer == null)
                return;

            if (result.Buffer.Length == 0)
                return;

            var message = MdvrMessageHelper.Parse(result.Buffer);
            if (message == null)
                return;

            var client = new DeviceClient(message.DevIDNO, result.RemoteEndPoint.Address, result.RemoteEndPoint.Port);

            if (Clients.ContainsKey(message.DevIDNO) == true)
                client = Clients[message.DevIDNO];
            else
            {
                if (Clients.TryAdd(message.DevIDNO, client) == true)
                {
                    ClientConnected?.Invoke(message.DevIDNO);
                }
            }

            HandleMessage(message, client);
        }

        void SendHeartbeat(string devIDNO)
        {
            Task.Run(() =>
            {
                var client = Clients[devIDNO];
                List<string> argList = CreateBaseCommandArgList(MdvrMessageTypes.C501, devIDNO);

                SendMessage(client.EndPoint, argList);
            });
        }

        void HandleMessage(MdvrMessageBase message, DeviceClient client)
        {
            var db = new LisconDbEntities();

            client.Runtime.GpsTime = message.MessageTime;
            client.Runtime.Latitude = message.LocationAndStatus.Location.Latitude;
            client.Runtime.Longitude = message.LocationAndStatus.Location.Longitude;

            if (message is V101Message)
            {
                HandleV101Message((V101Message)message, client, db);
            }
            else if (message is V114Message)
            {
                HandleV114Message((V114Message)message, client, db);
            }
            else if (message is V201Message)
            {
                HandleV201Message((V201Message)message);
            }
            else if (message is V251Message)
            {
                HandleV251Message((V251Message)message);
            }

            DeviceRuntimeBL.Update(client.Runtime, db);

            ClientRuntimeUpdated?.Invoke();
        }

        void HandleV251Message(V251Message message)
        {
            if (Alarms.ContainsKey(message.AlarmUID) == false)
            {
                Alarms.TryAdd(message.AlarmUID, new DeviceAlarm()
                {
                    Name = message.AlarmName,
                    AlarmTime = message.AlarmTime
                });
            }

            SendResponse(message, MdvrResponseTypes.Auto, message.AlarmUID);
        }

        void HandleV201Message(V201Message message)
        {
            if (Alarms.ContainsKey(message.AlarmUID) == false)
            {
                Alarms.TryAdd(message.AlarmUID, new DeviceAlarm()
                {
                    Name = message.AlarmName,
                    AlarmTime = message.AlarmTime
                });
            }

            SendResponse(message, MdvrResponseTypes.Auto, message.AlarmUID);
        }

        static void HandleV114Message(V114Message message, DeviceClient client, LisconDbEntities db)
        {
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

        void HandleV101Message(V101Message message, DeviceClient client, LisconDbEntities db)
        {
            var device = DeviceBL.TryGet(message.DevIDNO, db);
            if (device == null)
                DeviceBL.Register(message.DevIDNO, client.Runtime, db);

            SendResponse(message, MdvrResponseTypes.Auto, "1");
            SendAlarmServerConfig(client.DevIDNO, "", 6602, (int)ConnectionTypes.Udp);
        }

        void SendResponse(MdvrMessageBase message, params string[] responseArgs)
        {

            if (Clients.ContainsKey(message.DevIDNO) == false)
                return;

            Task.Run(() =>
            {
                var client = Clients[message.DevIDNO];
                var argList = CreateBaseCommandArgList(MdvrMessageTypes.C100,
                    message.DevIDNO,
                    message.MessageKey,
                    message.MessageTime.ToString(MdvrMessageHelper.DateTimeStringFormat));

                if (responseArgs != null)
                {
                    for (int i = 0; i < responseArgs.Length; i++)
                    {
                        argList.Add(responseArgs[i]);
                    }
                }

                SendMessage(client.EndPoint, argList);
            });
        }

        public void SendAlarmServerConfig(string devIDNO, string alarmServerIp, int alarmServerPort, int connectionType)
        {
            if (Clients.ContainsKey(devIDNO) == false)
                return;

            Task.Run(() =>
            {
                var client = Clients[devIDNO];
                var argList = CreateBaseCommandArgList(MdvrMessageTypes.C7212,
                    devIDNO,
                    alarmServerIp, 
                    alarmServerPort.ToString(), 
                    connectionType.ToString());

                SendMessage(client.EndPoint, argList);
            });
        }

        public void SendStartVideo(string devIDNO, int channelNo, string mediaServerIp, int mediaServerPort)
        {
            if (Clients.ContainsKey(devIDNO) == false)
                return;

            Task.Run(() =>
            {
                var client = Clients[devIDNO];
                var argList = CreateBaseCommandArgList(MdvrMessageTypes.C508, 
                    devIDNO,
                    BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0).ToString("X4"),
                    "1",
                    channelNo.ToString(),
                    "1",
                    "0",
                    mediaServerIp,
                    mediaServerPort.ToString());

                SendMessage(client.EndPoint, argList);
            });
        }

        public void SendStopVideo(string devIDNO, int channelNo, string mediaServerIp, int mediaServerPort)
        {
            if (Clients.ContainsKey(devIDNO) == false)
                return;

            Task.Run(() =>
            {
                var client = Clients[devIDNO];
                var argList = CreateBaseCommandArgList(MdvrMessageTypes.C508,
                    devIDNO,
                    BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0).ToString("X4"),
                    "0",
                    channelNo.ToString(),
                    "1",
                    "0",
                    mediaServerIp,
                    mediaServerPort.ToString());

                SendMessage(client.EndPoint, argList);
            });
        }

        static List<string> CreateBaseCommandArgList(string command, string devIDNO, params string[] commandParams)
        {
            var argList = new List<string>()
            {
                command,
                devIDNO,
                null,
                DateTime.Now.ToString("yyMMdd HHmmss")
            };

            if (commandParams != null)
            {
                for (int i = 0; i < commandParams.Length; i++)
                {
                    argList.Add(commandParams[i]);
                }
            }

            return argList;
        }
    }
}
