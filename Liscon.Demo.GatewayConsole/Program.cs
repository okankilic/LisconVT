using Liscon.Demo.GatewayConsole.Models;
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
        static GatewayUdpServer GatewayUdpServer = null;

        static void Main(string[] args)
        {
            while(true)
            {
                var line = Console.ReadLine();

                args = line.Trim().Split(' ');

                string cmd = args[0].ToLowerInvariant();

                if(cmd == "start")
                {
                    if (GatewayUdpServer == null)
                    {
                        GatewayUdpServer = new GatewayUdpServer(6608, 10000);
                        GatewayUdpServer.ClientConnected += GatewayUdpServer_ClientConnected;
                        GatewayUdpServer.ClientDisconnected += GatewayUdpServer_ClientDisconnected;
                        GatewayUdpServer.ClientRuntimeUpdated += GatewayUdpServer_ClientRuntimeUpdated;
                    }

                    GatewayUdpServer.Start();
                }
                else if(cmd == "stop")
                {
                    if (GatewayUdpServer != null)
                        GatewayUdpServer.Stop();
                }
            }
        }

        private static void GatewayUdpServer_ClientRuntimeUpdated()
        {
            RefreshConsole();
        }

        private static void GatewayUdpServer_ClientDisconnected()
        {
            RefreshConsole();
        }

        private static void GatewayUdpServer_ClientConnected()
        {
            RefreshConsole();
        }

        private static void RefreshConsole()
        {
            Console.Clear();

            Console.WriteLine("Clients");
            var clientLineFormat = "{0,-8} {1,-20:yyyy-MM-dd HH:mm:ss} {2,-10:F6} {3,-10:F6} {4, -16} {5, -6}";
            Console.WriteLine(clientLineFormat, "IDNO", "Datetime", "Latitude", "Longitude", "Ip Address", "Port");
            foreach (var client in GatewayUdpServer.Clients.Values)
            {
                Console.WriteLine(clientLineFormat, client.DevIDNO, client.Runtime.GpsTime, client.Runtime.Latitude, client.Runtime.Longitude, client.Ip, client.Port);
            }

            Console.WriteLine("Alarms");
            var alarmLineFormat = "{0, -32} {1, -20} {2,-20:yyyy-MM-dd HH:mm:ss} {3,-20:yyyy-MM-dd HH:mm:ss}";
            Console.WriteLine(alarmLineFormat, "Key", "Name", "Start", "End");
            foreach (var alarm in GatewayUdpServer.Alarms)
            {
                Console.WriteLine(alarmLineFormat, alarm.Key, alarm.Value.Name, alarm.Value.StartTime, alarm.Value.EndTime);
            }
        }
    }
}
