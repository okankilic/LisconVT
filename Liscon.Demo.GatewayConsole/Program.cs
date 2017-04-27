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
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace Liscon.Demo.GatewayConsole
{
    class Program
    {
        static ConcurrentDictionary<string, DeviceClient> Clients = new ConcurrentDictionary<string, DeviceClient>();
        static MdvrMessageReader MessageReader = null;
        static OragonUdpListener GatewayUdpServer = null;

        static int TxNo = 0;

        static void Main(string[] args)
        {
            string cmd = null;
            while(true)
            {
                var line = Console.ReadLine();

                args = line.Trim().Split(' ');

                cmd = args[0].ToLowerInvariant();

                if(cmd == "start")
                {
                    var timer = new Timer(10000);
                    timer.Elapsed += Timer_Elapsed;
                    timer.AutoReset = true;
                    timer.Start();

                    if (GatewayUdpServer == null)
                    {
                        GatewayUdpServer = new OragonUdpListener(6608);
                        GatewayUdpServer.DataReceived += GatewayUdpServer_DataReceived;
                    }

                    GatewayUdpServer.Start();
                }
                else if(cmd == "stop")
                {
                    if (GatewayUdpServer != null)
                        GatewayUdpServer.Stop();
                }
                //else if(cmd == "write")
                //{
                //    var clientID = args[1];

                //    int messageDataIndex = 2;
                //    var messageArgs = new string[args.Length - messageDataIndex];
                //    for (int i = messageDataIndex; i < args.Length; i++)
                //    {
                //        messageArgs[i - messageDataIndex] = args[i];
                //    }

                //    var messageWriter = new MDVRMessageWriter(messageArgs.ToList());
                //    var bytes = messageWriter.WriteToArray();

                //    GatewayServer.Send(clientID, bytes);
                //}
            }
        }

        private static void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (Clients.Count == 0)
                return;

            foreach (var client in Clients.Values)
            {
                var timeout = (DateTime.Now - client.Runtime.GpsTime).TotalSeconds;
                if(timeout > 120)
                {
                    DeviceClient clientToRemove;
                    if(Clients.TryRemove(client.DevIDNO, out clientToRemove) == true)
                    {
                        RefreshConsole();
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

                SendMessage(client, argList);
            }
        }

        private static void GatewayUdpServer_DataReceived(string ip, int port, byte[] bytes)
        {
            if (MessageReader == null)
            {
                MessageReader = new MdvrMessageReader();
                MessageReader.MessageReceived += MessageReader_MessageReceived;
            }

            MessageReader.Read(ip, port, bytes);
        }

        private static void MessageReader_MessageReceived(MdvrMessageReceivedArgs args)
        {
            DeviceClient client = null;

            if (Clients.ContainsKey(args.DevIDNO) == true)
                client = Clients[args.DevIDNO];
            else
            {
                client = new DeviceClient(args.DevIDNO, args.IpAddress, args.Port);
                if (Clients.TryAdd(args.DevIDNO, client) == true)
                    RefreshConsole();
            }

            var db = new LisconDbEntities();

            if (args.Message is MdvrMessageBase == false)
                return;

            var msgBase = args.Message as MdvrMessageBase;

            client.Runtime.GpsTime = msgBase.MessageTime;
            client.Runtime.Latitude = msgBase.LocationAndStatus.Location.Latitude;
            client.Runtime.Longitude = msgBase.LocationAndStatus.Location.Longitude;

            if (args.Message is V101Message)
            {
                var msg = args.Message as V101Message;

                var device = DeviceBL.TryGet(msg.DevIDNO, db);
                if (device == null)
                    DeviceBL.Register(args.DevIDNO, client.Runtime, db);

                var argList = new List<string>()
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

                SendMessage(client, argList);
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

            DeviceRuntimeBL.Update(client.Runtime, db);
            RefreshConsole();
        }

        private static void RefreshConsole()
        {
            Console.Clear();
            Console.WriteLine("{0,-8} {1,-20:yyyy-MM-dd HH:mm:ss} {2,-10:F6} {3,-10:F6} {4, -16} {5, -6}", "IDNO", "Datetime", "Latitude", "Longitude", "Ip Address", "Port");
            foreach (var client in Clients.Values)
            {
                Console.WriteLine("{0,-8} {1,-20:yyyy-MM-dd HH:mm:ss} {2,-10:F6} {3,-10:F6} {4, -16} {5, -6}", client.DevIDNO, client.Runtime.GpsTime, client.Runtime.Latitude, client.Runtime.Longitude, client.Ip, client.Port);
            }
        }

        private static void SendMessage(OragonUdpClient client, List<string> argList)
        {
            System.Threading.Interlocked.Increment(ref TxNo);

            argList.Insert(0, (TxNo % 10000).ToString());

            var writer = new MDVRMessageWriter(argList);
            client.Send(writer.WriteToArray());
        }
    }

    public class DeviceClient : OragonUdpClient
    {
        public DeviceRuntime Runtime { get; set; }

        public DeviceClient(string devIDNO, string ip, int port) : base(devIDNO, ip, port)
        {
            Runtime = new DeviceRuntime()
            {
                DevIDNO = devIDNO
            };
        }
    }
}
