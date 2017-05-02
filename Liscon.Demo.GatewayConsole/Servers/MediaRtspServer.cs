using Oragon.Extensions;
using Oragon.Media.Rtsp;
using Oragon.Media.RtspServer;
using Oragon.Media.RtspServer.MediaTypes;
using System.Net;

namespace Liscon.Demo.GatewayConsole.Servers
{
    public class MediaRtspServer
    {
        RtspServer _server;

        public MediaRtspServer(int port)
        {
            IPAddress serverIp = SocketExtensions.GetFirstUnicastIPAddress(System.Net.Sockets.AddressFamily.InterNetwork);

            _server = new RtspServer(serverIp, 6601)
            {
                Logger = new RtspServerConsoleLogger(),
                //ClientSessionLogger = new Media.Rtsp.Server.RtspServerDebugLogger()
            };


        }

        public void Start()
        {
            RtspSource rtspSrc = new RtspSource("Zeta", "rtsp://rtsp-v3-spbtv.msk.spbtv.com:554/spbtv_v3_1/332_110.sdp", RtspClient.ClientProtocolType.Tcp);

            _server.TryAddMedia(rtspSrc);
            _server.Start();
        }

        public void AddMedia(string devIDNO, int channelNo)
        {

        }

        public void OnDataReceived(string devIDNO, byte[] receivedBytes)
        {

        }
    }
}
