using Liscon.Demo.GatewayConsole.Models;
using Liscon.Demo.GatewayConsole.Servers;
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
using System.Timers;

namespace Liscon.Demo.GatewayConsole
{
    class Program
    {
        static string ServerIp = "176.240.208.9";

        static GatewayServer GatewayServer = null;
        static MediaServer MediaServer = null;

        static void Main(string[] args)
        {
            while(true)
            {
                var line = Console.ReadLine();

                args = line.Trim().Split(' ');

                string cmd = args[0].ToLowerInvariant();

                if(cmd == "start")
                {
                    if (GatewayServer == null)
                    {
                        GatewayServer = new GatewayServer(6608, 10000);
                        GatewayServer.ClientConnected += GatewayServer_ClientConnected;
                        GatewayServer.ClientDisconnected += GatewayServer_ClientDisconnected;
                        GatewayServer.ClientRuntimeUpdated += GatewayServer_ClientRuntimeUpdated;
                    }
                    GatewayServer.Start();

                    if (MediaServer == null)
                    {
                        MediaServer = new MediaServer("Media Server", 6602);
                        MediaServer.ClientConnected += MediaServer_ClientConnected;
                    }
                    MediaServer.Start();
                }
                else if(cmd == "stop")
                {
                    if (GatewayServer != null)
                        GatewayServer.Stop();

                    if (MediaServer != null)
                        MediaServer.Stop();
                }
                else if(cmd == "open")
                {
                    var devIDNO = args[1];
                    var channelNo = int.Parse(args[2]);

                    GatewayServer.SendStartVideo(devIDNO, channelNo, ServerIp, 6602);
                }
                else if (cmd == "close")
                {
                    var devIDNO = args[1];
                    var channelNo = int.Parse(args[2]);

                    GatewayServer.SendStopVideo(devIDNO, channelNo, ServerIp, 6602);
                }
            }
        }

        private static void MediaServer_ClientConnected(string devIDNO)
        {
            RefreshConsole();
        }

        private static void GatewayServer_ClientRuntimeUpdated()
        {
            RefreshConsole();
        }

        private static void GatewayServer_ClientDisconnected()
        {
            RefreshConsole();
        }

        private static void GatewayServer_ClientConnected(string devIDNO)
        {
            RefreshConsole();
        }

        private static void RefreshConsole()
        {
            Console.Clear();

            Console.WriteLine("Gateway Server Clients");
            var gatewaySvcFormat = "{0,-8} {1,-20:yyyy-MM-dd HH:mm:ss} {2,-10:F6} {3,-10:F6} {4, -16} {5, -6}";
            Console.WriteLine(gatewaySvcFormat, "IDNO", "Datetime", "Latitude", "Longitude", "Ip Address", "Port");
            foreach (var client in GatewayServer.Clients.Values)
            {
                Console.WriteLine(gatewaySvcFormat, client.DevIDNO, client.Runtime.GpsTime, client.Runtime.Latitude, client.Runtime.Longitude, client.Ip, client.Port);
            }

            Console.WriteLine("\r\n");
            Console.WriteLine("Media Server Clients");
            var mediaSvcFormat = "{0,-8} {1, -16} {2, -6}";
            Console.WriteLine(mediaSvcFormat, "IDNO", "Ip Address", "Port");
            foreach (var client in MediaServer.Clients.Values)
            {
                Console.WriteLine(mediaSvcFormat, client.DevIDNO, "", "");
            }

            Console.WriteLine("\r\n");
            Console.WriteLine("Alarms");
            var alarmLineFormat = "{0, -32} {1, -20} {2,-20:yyyy-MM-dd HH:mm:ss}";
            Console.WriteLine(alarmLineFormat, "Key", "Name", "Start");
            foreach (var alarm in GatewayServer.Alarms)
            {
                Console.WriteLine(alarmLineFormat, alarm.Key, alarm.Value.Name, alarm.Value.AlarmTime);
            }
        }
    }
}
