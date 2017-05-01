using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Enums
{
    public class MdvrMediaMessageTypes
    {
        /// <summary>
        /// From Server
        /// </summary>
        public const short RegisterFeedback = 0x6000;

        /// <summary>
        /// From Device
        /// </summary>
        public const short RealTimeVideo = 0x6001;

        /// <summary>
        /// From Server
        /// </summary>
        public const short FrameRequest = 0x6002;

        /// <summary>
        /// From Server
        /// </summary>
        public const short Report = 0x6003;

        /// <summary>
        /// From Server
        /// </summary>
        public const short VoiceRequest = 0x6004;

        /// <summary>
        /// From Server
        /// </summary>
        public const short Heartbeat = 0x6005;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VideoSearch = 0x6101;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VideoDownload = 0x6102;

        /// <summary>
        /// From Server
        /// </summary>
        public const short VideoDownloadControl = 0x6103;

        /// <summary>
        /// From Server
        /// </summary>
        public const short VideoDownloadReport = 0x6104;

        /// <summary>
        /// From Server
        /// </summary>
        public const short EndSession = 0x6105;

        /// <summary>
        /// From Device
        /// </summary>
        public const short ImageCapture = 0x6201;

        /// <summary>
        /// From Server
        /// </summary>
        public const short ConfigDownload = 0x6301;

        /// <summary>
        /// From Device
        /// </summary>
        public const short ConfigUpload = 0x6302;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VoiceType = 0x6401;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VoiceData = 0x6402;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VoiceReport = 0x6403;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VoiceEnd = 0x6404;

        /// <summary>
        /// From Device
        /// </summary>
        public const short UpgradeChecksum = 0x6501;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VideoStart = 0x6602;

        /// <summary>
        /// From Device
        /// </summary>
        public const short VideoStartStopResponse = 0x6601;
    }
}
