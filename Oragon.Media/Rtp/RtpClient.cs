using Oragon.Classes;
using Oragon.Classes.Binary;
using Oragon.Classes.Disposables;
using Oragon.Classes.Text;
using Oragon.Collections.Generic;
using Oragon.Extensions;
using Oragon.Interfaces;
using Oragon.Media.Common;
using Oragon.Media.Common.Extensions;
using Oragon.Media.Ntp;
using Oragon.Media.Rtcp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oragon.Media.Rtp
{
    public class RtpClient: BaseDisposable, ILoggingReference, IThreadReference
    {

        #region Fields

        //Buffer for data
        //Used in ReceiveData, Each TransportContext gets a chance to receive into the buffer, when the recieve completes the data is parsed if there is any then the next TransportContext goes.
        //Doing this in parallel doesn't really offset much because the decoder must be able to handle the data and if you back log the decoder you are just wasting cycles.        
        internal MemorySegment m_Buffer;

        //Todo, ThreadPriorityInformation

        //Each session gets its own thread to send and recieve
        internal System.Threading.Thread m_WorkerThread, m_EventThread; // and possibly another for events.

        //This signal determines if the workers will continue each iteration, it may be possible to use int to signal various other states.
        internal bool m_StopRequested, m_ThreadEvents, //on or off right now, int could allow levels of threading..
            m_IListSockets; //Indicates if to use the IList send overloads.

        //Collection to handle the dispatch of events.
        //Notes that Collections.Concurrent.Queue may be better suited for this in production until the ConcurrentLinkedQueue has been thoroughly engineered and tested.
        //The context, the item, final, recieved
        readonly ConcurrentLinkedQueueSlim<System.Tuple<RtpClient.TransportContext, BaseDisposable, bool, bool>> m_EventData = new ConcurrentLinkedQueueSlim<System.Tuple<RtpClient.TransportContext, BaseDisposable, bool, bool>>();

        //Todo, LinkedQueue and Clock.
        readonly System.Threading.ManualResetEventSlim m_EventReady = new System.Threading.ManualResetEventSlim(false, 100); //should be caluclated based on memory and speed. SpinWait uses 10 as a default.

        //Outgoing Packets, Not a Queue because you cant re-order a Queue (in place) and you can't take a range from the Queue (in a single operation)
        //Those things aside, ordering is not performed here and only single packets are iterated and would eliminate the need for removing after the operation.
        //Benchmark with Queue and ConcurrentQueue and a custom impl.
        //IPacket could also work in an implementaiton which sends evertyhing in the outgoing list at one time.
        internal readonly List<RtpPacket> m_OutgoingRtpPackets = new List<RtpPacket>();
        internal readonly List<RtcpPacket> m_OutgoingRtcpPackets = new List<RtcpPacket>();

        /// <summary>
        /// Any TransportContext's which are added go here for removal. This list can never be null.
        /// </summary>
        /// <notes>This possibly should be sorted but sorted lists cannot contain duplicates.</notes>
        internal readonly List<TransportContext> TransportContexts = new List<TransportContext>();

        /// <summary>
        /// Unique id assigned to each RtpClient instance. (16 byte overhead)
        /// </summary>
        internal readonly System.Guid InternalId = System.Guid.NewGuid();

        /// <summary>
        /// An implementation of ILogging which can be null if unassigned.
        /// </summary>
        public ILogging Logger;

        #endregion

        #region Constants / Statics

        /// <summary>
        /// The default amount which is used a multiplier to set the ReceiveBufferSize
        /// </summary>
        const int DefaultRecieveBufferSizeMultiplier = 100;

        internal static void ConfigureRtpThread(System.Threading.Thread thread)//,ILogging = null
        {
            thread.TrySetApartmentState(System.Threading.ApartmentState.MTA);
        }

        //Possibly should be moved to RFC3550

        public const string RtpProtcolScheme = "rtp", AvpProfileIdentifier = "avp", RtpAvpProfileIdentifier = "RTP/AVP";

        //Udp Hole Punch
        //Might want a seperate method for this... (WakeupRemote)
        //Most routers / firewalls will let traffic back through if the person from behind initiated the traffic.
        //Send some bytes to ensure the reciever is awake and ready... (SIP / RELOAD / ICE / STUN / TURN may have something specific and better)
        //e.g Port mapping request http://tools.ietf.org/html/rfc6284#section-4.2 
        static byte[] WakeUpBytes = new byte[] { 0x70, 0x70, 0x70, 0x70 };

        //Choose better name,,, 
        //And depending on how memory is aligned 36 may be a palindrome
        //FrameControl
        internal const byte BigEndianFrameControl = 36;//, // ASCII => $,  Hex => 24  Binary => (00)100100
        //LittleEndianFrameControl = 9;                   //                                        001001(00)

        //The point at which rollover occurs on the SequenceNumber

        /// <summary>
        /// Describes the size (in bytes) of the 
        /// [MAGIC , CHANNEL, {LENGTH}] octets which preceed any TCP RTP / RTCP data When multiplexing data on a single TCP port over RTSP.
        /// </summary>
        internal const int InterleavedOverhead = 4;
        //RTP/AVP/TCP Specifies only the Length bytes in network byte order. e.g. 2 bytes

        /// <summary>
        /// The default time assocaited with Rtcp report intervals for RtpClients. (Almost 5 seconds)
        /// </summary>
        public static readonly System.TimeSpan DefaultReportInterval = System.TimeSpan.FromSeconds(4.96);

        //Todo have a Context method which passes the necessary params to this function for reading various different types of framing

        /// <summary>
        /// Read the RFC2326 amd RFC4751 Frame header.
        /// Returns the amount of bytes in the frame.
        /// Outputs the channel of the frame in the channel variable.
        /// </summary>
        /// <param name="buffer">The data containing the RFC4751 frame</param>
        /// <param name="offset">The offset in the </param>
        /// <param name="channel">The byte which will contain the channel if the reading succeeded</param>
        /// <param name="readFrameByte">Indicates if the frameByte should be read (RFC2326)</param>
        /// <param name="frameByte">Indicates the frameByte to read</param>
        /// <returns> -1 If the buffer does not contain a RFC2326 / RFC4751 frame at the offset given</returns>
        internal static int TryReadFrameHeader(byte[] buffer, int offset, out byte channel, byte? frameByte = BigEndianFrameControl, bool readChannel = true)
        {
            //Must be assigned
            channel = default(byte);

            if (ArrayExtensions.IsNullOrEmpty(buffer)) return -1;

            //https://www.ietf.org/rfc/rfc2326.txt

            //10.12 Embedded (Interleaved) Binary Data

            //Todo, Native, Unsafe
            //If the buffer does not start with the magic byte this is not a RFC2326 frame, it could be a RFC4571 frame
            if (frameByte.HasValue && buffer[offset++].Equals(frameByte).Equals(false)) return -1; //goto ReadLengthOnly;

            /*
             Stream data such as RTP packets is encapsulated by an ASCII dollar
            sign (24 hexadecimal), followed by a one-byte channel identifier,
            followed by the length of the encapsulated binary data as a binary,
            two-byte integer in network byte order. The stream data follows
            immediately afterwards, without a CRLF, but including the upper-layer
            protocol headers. Each $ block contains exactly one upper-layer
            protocol data unit, e.g., one RTP packet.

            The channel identifier is defined in the Transport header with the
            interleaved parameter(Section 12.39).

            When the transport choice is RTP, RTCP messages are also interleaved
            by the server over the TCP connection. As a default, RTCP packets are
            sent on the first available channel higher than the RTP channel. The
            client MAY explicitly request RTCP packets on another channel. This
            is done by specifying two channels in the interleaved parameter of
            the Transport header(Section 12.39).
             */


            //Todo, Native, Unsafe
            //Assign the channel if reading framed.
            if (readChannel) channel = buffer[offset++];

            #region Babble

            //In stand alone operation mode the RtpClient should read only the length of the frame and decypher the contents based on the Payload.

            //SEE [COMEDIA]ly
            //http://tools.ietf.org/search/rfc4571#section-3

            //The ssrc may be useless due to middle boxes which have re-compresssed the data and sent it along with a new identifier....
            //The new identifier would be valid but would imply that the packet came from a different source (which since it was re-sampled it should)...
            //However
            //Based on my interpreatation the SSRC doesn't need to change @ all.
            //The packet's ContribuingSourceCount could be incremented by 1 (By the middle box who would subsequently add it's OWN identifier to the ContributingSourceList.)
            //The packet should become 4 bytes larger per hop that it is compressed or altered in due to the added entry.
            //If CC = 15 then no compression should be performed and the packet may need to be dropped if the destination is not within the next hop...

            //This would allow a receiver to dictate that middle boxes are causing unwanted compression or delay in the stream and subsequenlty the ability drop all packets from that middle box if required by iterating the contributing source list.
            //It would also allow such a receiver to either expediate or change the packet in another such way
            //IMHO IF THE ORIGINAL SSRC CHANGES before the packet reached the application THIS HAS DIRE CONSEQUENCES when the IDENTITY IS EXPECTED to be a particular value...
            //The application would have no way to verify that the data is indeed from Middle box X, Y or Z without using some form of verification i.e encryption.

            //Last but not least using 2 tcp sockets would be more performant but would require double the overhead from the provider, almost double the bandwidth (in protocol overhead) and definitely double the security issues.

            //RFC4571 - Out-of-band semantics
            //Section 2 does not define the RTP or RTCP semantics for closing a
            //TCP socket, or of any other "out of band" signal for the
            //connection.

            //With respect to Rtcp the sender should eventually timeout in the application, but the problem here lies in the fact the middle box has no control over that.
            //Thus the middle box it self will become conjested waiting for the timeout...
            //Additionally RTCP may not be enabled... if this is the case there would be no `Goodbye`
            //If RTCP was enabled then
            //Since the return route may not involve the same middle box which 'helped' it[the middlebox] may not get the `Goodbye` indication from the application participant,
            //Thus they would only timeout with respect to their own implementation rules for such,
            //BUT COULD ALSO receive another packet from another session which just happens to have the same SSRC
            //I / We would hope in such a case that the EndPoint would be different FROM the application's EndPoint because if it was not then that packet would subsequently routed to the application....

            //Lastly if the middle box compressed the data in any such way the payload indication would possibly be modified (and should be if the format changed)... thus breaking the compatability with the receiving application.
            //This implies that the Payload indication cannot change but the timestamp possibly could to reflect more delay if required but that should be handled by the application anyway.... not a middle box

            //Thus RTCP may be better suited for this type of 'change' e.g. each middle box could handle RtcpPackets to reflect the delay without changing the data within the rtp packet at all
            //upon receving a RtcpPacket The BlockCount could be incremented and an additional block could be added to indicate the metrics e.g. delay and jitter introduced by said middle box.
            //This would allow the receiving application to essentially ask that theat middle box not route packets any more or ask that it expedite routing et al.

            #endregion

            //Return the result of reversing the Unsigned 16 bit integer at the offset
            return Binary.ReadU16(buffer, offset, Binary.IsLittleEndian);
        }

        //Todo, cleanup and allow existing Rtp and Rtcp socket.

        /// <summary>
        /// Will create a <see cref="RtpClient"/> based on the given parameters
        /// </summary>
        /// <param name="sessionDescription"></param>
        /// <param name="sharedMemory"></param>
        /// <param name="incomingEvents"></param>
        /// <param name="rtcpEnabled"></param>
        /// <returns></returns>
        public static RtpClient FromSessionDescription(Sdp.SessionDescription sessionDescription, MemorySegment sharedMemory = null, bool incomingEvents = true, bool rtcpEnabled = true, System.Net.Sockets.Socket existingSocket = null, int? rtpPort = null, int? rtcpPort = null, int remoteSsrc = 0, int minimumSequentialRtpPackets = 2, bool connect = true, System.Action<System.Net.Sockets.Socket> configure = null)
        {
            if (IDisposedExtensions.IsNullOrDisposed(sessionDescription)) throw new System.ArgumentNullException("sessionDescription");

            Sdp.Lines.SessionConnectionLine connectionLine = new Sdp.Lines.SessionConnectionLine(sessionDescription.ConnectionLine);

            System.Net.IPAddress remoteIp = System.Net.IPAddress.Parse(connectionLine.Host), localIp;

            System.Net.NetworkInformation.NetworkInterface localInterface;

            //If the socket is NOT null and IS BOUND use the localIp of the same address family
            if (object.ReferenceEquals(existingSocket, null).Equals(false) && existingSocket.IsBound)
            {
                //If the socket is IP based
                if (existingSocket.LocalEndPoint is System.Net.IPEndPoint)
                {
                    //Take the localIp from the LocalEndPoint
                    localIp = (existingSocket.LocalEndPoint as System.Net.IPEndPoint).Address;
                }
                else
                {
                    throw new System.NotSupportedException("Please create an issue for your use case.");
                }
            }
            else // There is no socket existing.
            {
                //If the remote address is the broadcast address or the remote address is multicast
                if (System.Net.IPAddress.Broadcast.Equals(remoteIp) || IPAddressExtensions.IsMulticast(remoteIp))
                {
                    //This interface should be the interface you plan on using for the Rtp communication
                    localIp = SocketExtensions.GetFirstMulticastIPAddress(remoteIp.AddressFamily, out localInterface);
                }
                else
                {
                    //This interface should be the interface you plan on using for the Rtp communication
                    localIp = SocketExtensions.GetFirstUnicastIPAddress(remoteIp.AddressFamily, out localInterface);
                }
            }

            RtpClient client = new RtpClient(sharedMemory, incomingEvents);

            byte lastChannel = 0;

            //Todo, check for session level ssrc 
            //if (remoteSsrc.Equals(0))
            //{
            //    //Sdp.SessionDescriptionLine ssrcLine = sessionDescription.SsrcGroupLine; // SsrcLine @ the session level could imply Group
            //}

            //For each MediaDescription in the SessionDescription
            foreach (Media.Sdp.MediaDescription md in sessionDescription.MediaDescriptions)
            {
                //Make a RtpClient.TransportContext from the MediaDescription being parsed.
                TransportContext tc = TransportContext.FromMediaDescription(sessionDescription, lastChannel++, lastChannel++, md,
                    rtcpEnabled, remoteSsrc, minimumSequentialRtpPackets,
                    localIp, remoteIp, //The localIp and remoteIp
                    rtpPort, rtcpPort, //The remote ports to receive data from
                    connect, existingSocket, configure);

                //Try to add the context
                try
                {
                    client.AddContext(tc);
                }
                catch (System.Exception ex)
                {
                    TaggedExceptionExtensions.RaiseTaggedException(tc, "See Tag, Could not add the created TransportContext.", ex);
                }
            }

            //Return the participant
            return client;
        }

        #endregion

        #region Events        

        /// <summary>
        /// Provides a function signature which is used to process data at a given offset and length.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        public delegate void InterleavedDataHandler(object sender, byte[] data, int offset, int length);

        /// <summary>
        /// Provides a funtion signature which is used to process RtpPacket's
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="packet"></param>
        /// <param name="tc"></param>
        public delegate void RtpPacketHandler(object sender, RtpPacket packet = null, TransportContext tc = null);

        /// <summary>
        /// Provides a function signature which is used to process RtcpPacket's
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="packet"></param>
        /// <param name="tc"></param>
        public delegate void RtcpPacketHandler(object sender, RtcpPacket packet = null, TransportContext tc = null);

        /// <summary>
        /// Provides a function signature which is used to process RtpFrame's
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="frame"></param>
        /// <param name="tc"></param>
        /// <param name="final"></param>
        public delegate void RtpFrameHandler(object sender, RtpFrame frame = null, TransportContext tc = null, bool final = false);

        /// <summary>
        /// Provides a function signature which is used to provide status of the RtpClient
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        public delegate void RtpClientAction(RtpClient sender, object args);

        //Todo, Determine if events for unknown ssrc and version are helpful

        //OnUnknownIdentify(object sender, IPacket packet)

        //Combine with 
        //=> bool AllocateContextForUnknownParticipants
        //To Create a new TransportContext for this participant.

        //OnUnknownVersion(object sender, IPacket packet)

        //Determine if events for loss of packets is useful and what to provide

        //If RFC3550 had a Source class then the source would be provided here, otherwise the TransportContext
        //To have another type of class PacketLossInformation would require more logic for no purpose at this level.
        //Furthermore PacketLossInformation can be determined from the TransportContext.
        //Thus the TransportContext should have a structure which can represent this information.

        //OnPacketLoss(object sender, PacketLossInformation info)

        /// <summary>
        /// Raised when non rtp protocol data is recieved.
        /// </summary>
        public event InterleavedDataHandler OutOfBandData;

        //Todo, Add and Remove pattern would allow ThreadEvents to be automatically turned on and off based on amount of connected handlers..

        /// <summary>
        /// Raised when a RtpPacket is received
        /// </summary>
        public event RtpPacketHandler RtpPacketReceieved;

        /// <summary>
        /// Raised when a RtcpPacket is received
        /// </summary>
        public event RtcpPacketHandler RtcpPacketReceieved;

        /// <summary>
        /// Raised when a RtpPacket has been sent
        /// </summary>
        public event RtpPacketHandler RtpPacketSent;

        /// <summary>
        /// Raised when a RtcpPacket has been sent
        /// </summary>
        public event RtcpPacketHandler RtcpPacketSent;

        /// <summary>
        /// Raised when a complete RtpFrame was changed due to a packet being added, removed or updated.
        /// </summary>
        public event RtpFrameHandler RtpFrameChanged;

        #region Table of Participants

        //6.3.2 Initialization....
        //I will do no such thing, I will no have no table when no table is required such as be the case when no expectance is put on the identity of the recipient.
        //All such packets should be considered equal unless specifically negioated by means provided by an alternate mechanism such as SDP or the RTP-Info header and is beyond the scope of the RtpClient implementation [based on my interpretation that is.]
        //I could go on and on about this but I think we all get the point

        //In most cases this table would only contain 1 entry anyway...

        #endregion

        //6.3.3 Rtp or Rtcp
        protected internal virtual void HandleIncomingRtcpPacket(object rtpClient, RtcpPacket packet, RtpClient.TransportContext transportContext = null)
        {
            //Determine if the packet can be handled
            if (false.Equals(RtcpEnabled) || IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;

            int packetLength = packet.Length;

            //Cache the ssrc of the packet's sender.
            int partyId = packet.SynchronizationSourceIdentifier,
                packetVersion = packet.Version;

            //See if there is a context for the remote party. (Allows 0)
            transportContext = transportContext ?? GetContextBySourceId(partyId);

            //Todo, if PersistIncomingRtcpReports reports then Clone the packet and store it on the context.

            //Raise an event for the rtcp packet received.
            OnRtcpPacketReceieved(packet, transportContext);

            //Compressed or no ssrc Return
            if (HandleIncomingRtcpPackets.Equals(false)) return;
            else if (packet.IsCompressed || packetLength < Binary.BytesPerLong)
            {
                ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtcpPacket Compression or Length @ Version, Found =>" + packetVersion + ", Pt =>" + packet.PayloadType + " , Bc => " + packet.BlockCount);

                return;

            }//else if there is a context and the version doesn't match.
            else if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) && false.Equals(transportContext.Version.Equals(packetVersion)))
            {
                ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtcpPacket Invalid Version, Found =>" + packetVersion + ", Expected =>" + transportContext.Version);

                //Do nothing else.
                return;
            }

            //Only if the packet was not addressed to a unique party with the id of 0 and there is a null context or the context is in discovery.
            if (false.Equals(partyId.Equals(0)) && false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) && transportContext.InDiscovery)
            {
                //Cache the payloadType and blockCount
                int blockCount = packet.BlockCount;

                //Before checking the type ensure there is a party id and block count
                if (blockCount.Equals(0))
                {
                    //If there was a context and the remote party has not yet been identified.
                    if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) &&
                        transportContext.InDiscovery &&
                        transportContext.Version.Equals(packetVersion))
                    {
                        //Identify the remote party by this id.
                        transportContext.RemoteSynchronizationSourceIdentifier = partyId;

                        //Check packet loss...

                        ILoggingExtensions.Log(Logger, ToString() + "@HandleIncomingRtcpPacket Set RemoteSynchronizationSourceIdentifier @ " + transportContext.SynchronizationSourceIdentifier + " to=" + transportContext.RemoteSynchronizationSourceIdentifier + "RR blockId=" + partyId);
                    }

                    return;
                }

                //Check the type because there is at least 1 block
                int payloadType = packet.PayloadType;

                if (payloadType == ReceiversReport.PayloadType)
                {
                    //Create a wrapper around the packet to access the ReportBlocks
                    using (ReceiversReport rr = new ReceiversReport(packet, false))
                    {
                        //Iterate each contained ReportBlock
                        foreach (IReportBlock reportBlock in rr)
                        {
                            int blockId = reportBlock.BlockIdentifier;

                            if (blockCount.Equals(0)) continue;

                            //Attempt to obtain a context by the identifier in the report block
                            transportContext = GetContextBySourceId(blockId);

                            //If there was a context and the remote party has not yet been identified.
                            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) &&
                                transportContext.InDiscovery &&
                                transportContext.Version.Equals(packetVersion))
                            {
                                //Identify the remote party by this id.
                                transportContext.RemoteSynchronizationSourceIdentifier = blockId;

                                //Check packet loss...

                                ILoggingExtensions.Log(Logger, ToString() + "@HandleIncomingRtcpPacket Set RemoteSynchronizationSourceIdentifier @ RR " + transportContext.SynchronizationSourceIdentifier + " to=" + transportContext.RemoteSynchronizationSourceIdentifier + "RR blockId=" + blockId);

                                //Stop looking for a context.
                                break;
                            }
                        }
                    }
                }
                else if (payloadType == GoodbyeReport.PayloadType) //The GoodbyeReport report from a remote party
                {
                    //Create a wrapper around the packet to access the source list
                    using (GoodbyeReport gb = new GoodbyeReport(packet, false))
                    {
                        using (RFC3550.SourceList sourceList = gb.GetSourceList())
                        {
                            //Iterate each party leaving
                            foreach (int party in sourceList)
                            {
                                if (party.Equals(0)) continue;

                                //Attempt to obtain a context by the identifier in the report block
                                transportContext = GetContextBySourceId(party);

                                //If there was a context
                                if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) &&
                                    transportContext.Version.Equals(packetVersion))
                                {
                                    //Send report now if possible.
                                    bool reportsSent = SendReports(transportContext);

                                    ILoggingExtensions.Log(Logger, ToString() + "@HandleIncomingRtcpPacket Recieved Goodbye @ " + transportContext.SynchronizationSourceIdentifier + " from=" + partyId + " reportSent=" + reportsSent);

                                    transportContext.ResetRtpValidationCounters(transportContext.m_SequenceNumber);

                                    //Stop looking for a context.
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (payloadType == SendersReport.PayloadType) //The senders report from a remote party                    
                {
                    //Create a wrapper around the packet to access the ReportBlocks
                    using (SendersReport sr = new SendersReport(packet, false))
                    {
                        //Iterate each contained ReportBlock
                        foreach (IReportBlock reportBlock in sr)
                        {
                            int blockId = reportBlock.BlockIdentifier;

                            if (blockCount.Equals(0)) continue;

                            //Attempt to obtain a context by the identifier in the report block
                            transportContext = GetContextBySourceId(blockId);

                            //If there was a context and the remote party has not yet been identified.
                            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext)) &&
                                transportContext.Version == packetVersion &&
                                transportContext.RemoteSynchronizationSourceIdentifier.Equals(0))
                            {

                                //Identify the remote party by this id.
                                transportContext.RemoteSynchronizationSourceIdentifier = blockId;

                                //Check packet loss...

                                ILoggingExtensions.Log(Logger, ToString() + "@HandleIncomingRtcpPacket Set RemoteSynchronizationSourceIdentifier @ SR " + transportContext.SynchronizationSourceIdentifier + " to=" + transportContext.RemoteSynchronizationSourceIdentifier + "RR blockId=" + blockId);

                                //Stop looking for a context.
                                break;
                            }
                        }
                    }
                }
            }

            //Handle Goodbyes with a positive blockcount but no  sourcelist...?

            //NoContext:

            //If no transportContext could be found
            if (IDisposedExtensions.IsNullOrDisposed(transportContext))
            {
                //Attempt to see if this was a rtp packet by using the RtpPayloadType
                int rtpPayloadType = packet.Header.First16Bits.RtpPayloadType;

                //Todo, make constant 13 Silence...
                if (rtpPayloadType.Equals(13) || false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext = GetContextByPayloadType(rtpPayloadType))))
                {
                    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtcpPacket - Incoming RtcpPacket actually was Rtp. Ssrc= " + partyId + " Type=" + rtpPayloadType + " Len=" + packet.Length);

                    //Raise an event for the 'RtpPacket' received. 
                    //Todo Use the existing reference / memory of the RtcpPacket) or provide an implicit way to cast 
                    using (RtpPacket rtp = new RtpPacket(packet.Prepare().ToArray(), 0))
                    {
                        OnRtpPacketReceieved(rtp, transportContext);
                    }

                    //Don't do anything else
                    return;
                }

                //Could attempt to find the context in which this packet is trying to communicate with if we had a RemoteEndPoint indicating where the packet was received from...
                //Cannot find a context because there may be more then one context which has not yet been identified
                //Could attempt to check that there is only 1 context and then if not yet valid assign the identity...
                //if(TransportContexts.Count == 1) ...

                //ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtcpPacket - No Context for packet " + partyId + "@" + packet.PayloadType);

                //Could create context for partyId here.

                //Todo, OutOfBand(RtcpPacket)

                //Don't do anything else.
                return;
            }

            //There is a transportContext

            //If there is a collision in the unique identifiers
            if (transportContext.SynchronizationSourceIdentifier.Equals(partyId))
            {
                //Handle it.
                HandleIdentityCollision(transportContext);
            }

            #region Unused [Packet Completion]

            //Complete the RtcpPacket if required.
            //while (!localPacket.IsComplete)
            //{
            //    //Complete the packet.
            //    int received = localPacket.CompleteFrom(transportContext.RtcpSocket, localPacket.Payload);
            //}

            #endregion

            //Last Rtcp packet was received right now now.
            transportContext.m_LastRtcpIn = packet.Created;

            //The context is active.
            transportContext.m_InactiveTime = TimeSpanExtensions.InfiniteTimeSpan;

            //Don't worry about overflow
            unchecked
            {
                //Increment packets received for the valid context.
                ++transportContext.RtcpPacketsReceived;

                //Keep track of the the bytes sent in the context
                transportContext.RtcpBytesRecieved += packet.Length;

                //Set the time when the first rtcp packet was recieved
                if (transportContext.m_FirstPacketReceived == DateTime.MinValue) transportContext.m_FirstPacketReceived = packet.Created;
            }

            #region Unused [Handle if packet was Goodbye]

            //bool goodBye = packet.PayloadType == Rtcp.GoodbyeReport.PayloadType;

            ////If the context is valid, AND the remote identify has a value and the packet identity is not the same then reset the state and account for the new identity
            //if (transportContext.IsValid && transportContext.RemoteSynchronizationSourceIdentifier.HasValue && localPacket.SynchronizationSourceIdentifier != transportContext.RemoteSynchronizationSourceIdentifier)
            //{
            //    //Tell the source we are no longer listening to the old identity
            //    //SendGoodbye(transportContext);

            //    //Reset state for the counters
            //    //transportContext.ResetState();

            //    //Assign the new remote ID (EVENT?)
            //    transportContext.RemoteSynchronizationSourceIdentifier = localPacket.SynchronizationSourceIdentifier;

            //    //Send reports if we can unless this is a Goodbye
            //    /*if (!goodBye) */SendReports(transportContext);                
            //}

            //if (goodBye && packet.BlockCount > 0) transportContext.m_SendInterval = TimeSpanExtensions.InfiniteTimeSpan; //Then never send reports again?

            #endregion       

            //OnRtcpPacketProcessed(this, packet, transportContext);
        }

        protected internal virtual void HandleIdentityCollision(TransportContext transportContext)
        {

            if (transportContext == null) throw new ArgumentNullException("transportContext");

            if (transportContext.IsDisposed) throw new ObjectDisposedException("transportContext");

            ILoggingExtensions.Log(Logger, InternalId + "HandleCollision - Ssrc=" + transportContext.SynchronizationSourceIdentifier + " - RSsrc=" + transportContext.RemoteSynchronizationSourceIdentifier);

            //Send a goodbye and indicate why.
            SendGoodbye(transportContext, System.Text.Encoding.UTF8.GetBytes("ssrc"));

            //Assign a new random ssrc which is not equal to the remote parties.
            //Noting that you could use the same ssrc +/-N here also or a base from the number of parties etc.

            //This may deserve an event, 'OnCollision'

            do transportContext.SynchronizationSourceIdentifier = RFC3550.Random32(transportContext.SynchronizationSourceIdentifier);
            while (transportContext.SynchronizationSourceIdentifier == transportContext.RemoteSynchronizationSourceIdentifier);

            //Reset counters from this point forward
            transportContext.ResetState();

            //reset counters?
        }

        //Reserved, possibly would be used for seperate finalization
        protected internal virtual void HandleFrameChange(object /*RtpClient*/ sender, RtpFrame frame = null, TransportContext tc = null)
        {
            //TransportContext context = tc ?? GetContextByPayloadType(frame.PayloadTypeByte);
            ////If there is a context
            //if (context == null) return;
        }

        /// <summary>
        /// Updates counters and fires a FrameChanged event if required.
        /// </summary>
        /// <param name="sender">The object which raised the event</param>
        /// <param name="packet">The RtpPacket to handle</param>
        protected internal virtual void HandleIncomingRtpPacket(object/*RtpClient*/ sender, RtpPacket packet, RtpClient.TransportContext transportContext = null)
        {
            //sender maybe not this
            //if (false == this.Equals(sender)) return;

            //Determine if the incoming packet CAN be handled
            if (false.Equals(RtpEnabled) || IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;

            //Should check right here incase the packet was incorrectly mapped to rtp from rtcp by checking the payload type to be in the reserved range for rtcp conflict avoidance.

            //Get the transportContext for the packet by the sourceId then by the payload type of the RtpPacket, not the SSRC alone because it may have not yet been defined.
            //Noting that this is not per RFC3550
            //This is because this implementation allows for the value 0 to be used as a discovery mechanism.

            //Notes that sometimes multiple payload types are being sent from a sender, in such cases the transportContext may incorrectly be selected here.
            //E.g.if payloadTypes of two contexts overlap and the ssrc is not well defined for each.
            transportContext = transportContext ?? GetContextForPacket(packet);

            //Raise the event for the packet. (Could add valid here)
            OnRtpPacketReceieved(packet, transportContext);

            //If the client shouldn't handle the packet then return.            
            if (false.Equals(HandleIncomingRtpPackets) || packet.IsCompressed)
            {
                return;
            }

            #region TransportContext Handles Packet

            //If the context is still null
            if (IDisposedExtensions.IsNullOrDisposed(transportContext))
            {
                ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket Unaddressed RTP Packet " + packet.SynchronizationSourceIdentifier + " PT =" + packet.PayloadType + " len =" + packet.Length);

                //Do nothing else.
                return;
            }

            #region Unused [Handles packet version validation

            //Already checked in ValidatePacket
            //int packetVersion = packet.Version;

            ////If the version doesn't match.
            //if (transportContext.Version != packetVersion)
            //{
            //    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket Invalid Version, Found =>" + packetVersion + ", Expected =>" + transportContext.Version);

            //    //Do nothing else.
            //    return;
            //}

            #endregion

            #region Unused [Handles PayloadType validation]

            //Cache the payload type of the packet being handled
            //int payloadType = packet.PayloadType;

            //Checked in ValidatePacketAndUpdateSequenceNumber
            ////If the packet payload type has not been defined in the MediaDescription
            //if (false == transportContext.MediaDescription.PayloadTypes.Contains(payloadType))
            //{
            //    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket RTP Packet PT =" + packet.PayloadType + " is not in Media Description. (" + transportContext.MediaDescription.MediaDescriptionLine + ") ");

            //    //Do nothing else.
            //    return;
            //}

            #endregion

            //Cache the ssrc
            int partyId = packet.SynchronizationSourceIdentifier;

            //Check for a collision
            if (partyId.Equals(transportContext.SynchronizationSourceIdentifier))
            {
                //Handle it
                HandleIdentityCollision(transportContext);
            }

            #region Unused [Handles TransportContext.InDiscovery When TransportContext.IsValid is false]

            //////If the context is NOT valid AND the context is in discovery mode of the remote party
            ////if (false == transportContext.IsValid && transportContext.InDiscovery)
            ////{
            ////    //Assign an id at this time
            ////    transportContext.RemoteSynchronizationSourceIdentifier = partyId;
            ////}

            #endregion

            #region Unused [Handles remote identify switching]

            //////If the packet was not addressed to the context AND the context is valid
            ////if (partyId != transportContext.RemoteSynchronizationSourceIdentifier
            ////    &&
            ////    transportContext.IsValid)
            ////{

            ////    //Reset the state if not discovering
            ////    if (false == transportContext.InDiscovery)
            ////    {
            ////        ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket SSRC Mismatch @ " + transportContext.SynchronizationSourceIdentifier + "<->" + transportContext.RemoteSynchronizationSourceIdentifier + "||" + partyId + ". ResetState");

            ////        transportContext.ResetState();
            ////    }

            ////    //Assign the id of the remote party.
            ////    transportContext.RemoteSynchronizationSourceIdentifier = partyId;

            ////    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket Set RemoteSynchronizationSourceIdentifier @ " + transportContext.SynchronizationSourceIdentifier + " to=" + transportContext.RemoteSynchronizationSourceIdentifier);
            ////}    

            #endregion           

            //Don't worry about overflow.
            unchecked
            {
                int packetLength = packet.Length;

                //if (packetLength <= RtpHeader.Length)
                //{
                //    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket Header Only " +
                //             " Context seq=" + transportContext.RecieveSequenceNumber +
                //             " Packet pt= (" + transportContext.MediaDescription.MediaType + ")" + packet.PayloadType +
                //            " seq=" + packet.SequenceNumber +
                //            " len= " + packetLength);

                //    //return;
                //}

                int pt = packet.PayloadType;

                //Todo, offer a reason via out why the packet is not valid to reduce overhead of checking from the Validate function

                //If the packet sequence number is not valid
                if (false.Equals(transportContext.ValidatePacketAndUpdateSequenceNumber(packet)))
                {

                    //If the pt is not in the media description this is out of band data and the packet was already evented.
                    if (false.Equals(transportContext.AllowsMultiplePayloadTypes) && false.Equals(transportContext.MediaDescription.PayloadTypes.Contains(pt))) return;
                    //If duplicate packets are not allowed
                    //else if (false.Equals(transportContext.AllowDuplicatePackets) && transportContext.RecieveSequenceNumber >= packet.SequenceNumber) return;
                    //If the context does not allow out of order packets return.
                    else if (false.Equals(transportContext.AllowOutOfOrderPackets)) return;

                    //Increment for a failed reception, possibly rename
                    ++transportContext.m_FailedRtpReceptions;

                    ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket Failed Reception " +
                             "(= " + transportContext.m_FailedRtpReceptions + ") @" + transportContext.SynchronizationSourceIdentifier +
                             " Context seq=" + transportContext.RecieveSequenceNumber +
                             " Packet pt= (" + transportContext.MediaDescription.MediaType + ")" + pt +
                            " seq=" + packet.SequenceNumber +
                            " len= " + packetLength);

                    //Todo, Event for discontuity... (see above notes on could)

                    //If TCP then one would enable congection control or swap the method etc,
                    //Modify Re-Transmission time.

                    //Caulcate loss.

                    //If Udp would change ttl and possibly DNS resolver,
                    //Send reports with loss or culumate for next report based on bandwidth and application requirements
                }
                else ++transportContext.ValidRtpPacketsReceived; //Increase the amount of valid rtp packets recieved when ValidatePacketAndUpdateSequenceNumber is true

                #region Identity and version Seperation

                //If IsValid then ensure the RemoteSynchronizationSourceIdentifier is set.
                //if (transportContext.InDiscovery || transportContext.IsValid && false.Equals(partyId.Equals(transportContext.RemoteSynchronizationSourceIdentifier)))
                //{
                //    //If not yet set, set the remote id 
                //    if (transportContext.RemoteSynchronizationSourceIdentifier.Equals(0))
                //    {
                //        transportContext.RemoteSynchronizationSourceIdentifier = partyId;

                //        ILoggingExtensions.Log(Logger, "HandleIncomingRtpPacket@ transportContext.IsValid, RemoteSynchronizationSourceIdentifier Initialized = " + partyId + "MediaType = " + transportContext.MediaDescription.MediaType);

                //        //SendReports(transportContext, true);
                //    }
                //    else
                //    {
                //        //Todo, this allows multiple different 'sending' identities to a single receiver.

                //        //There are multiple uses for this such as stream layering.
                //        //This would be more compliant by using the CSRC field and the result would be such that all complaint Mixers would be able to be used.
                //        //You would also more easily be able to extract the layered streams and provide the seperated streams.

                //        //switch (transportContext.MaximumRecievers)
                //        //{
                //        //    case 0:
                //        //        {
                //        //            AddContext(new TransportContext(transportContext.DataChannel, transportContext.ControlChannel, transportContext.SynchronizationSourceIdentifier, transportContext.IsRtcpEnabled, partyId, 0)
                //        //            {
                //        //                MediaDescription = transportContext.MediaDescription
                //        //            }, false, false, false, false);

                //        //            transportContext.Recievers.Add(partyId);

                //        //            ILoggingExtensions.Log(Logger, "HandleIncomingRtpPacket@ Added New Context Party = " + partyId + "PT = " + packet.PayloadType);

                //        //            break;
                //        //        }
                //        //    default:
                //        //        {
                //        //            if (transportContext.Recievers.Count < transportContext.MaximumRecievers)
                //        //            {
                //        //                goto case 0;
                //        //            }

                //        //            ILoggingExtensions.Log(Logger, "HandleIncomingRtpPacket@ Too Many Contexts (" + TransportContexts.Count + ")-(" + transportContext.MaximumRecievers + ") Party = " + partyId + "PT = " + packet.PayloadType);

                //        //            //Send a goodbye for now. //System.Text.Encoding.UTF8.GetBytes("b\\a\\ndwi\\d\\th")
                //        //            SendGoodbye(transportContext, System.Text.Encoding.UTF8.GetBytes("limit"), partyId, true, null, false);

                //        //            break;

                //        //        }
                //        //}

                //        ILoggingExtensions.Log(Logger, "HandleIncomingRtpPacket@ transportContext.IsValid@(" + transportContext.RemoteSynchronizationSourceIdentifier + "), Unknown Sender = " + partyId + " PT = " + packet.PayloadType);

                //        //SendGoodbye(transportContext, System.Text.Encoding.UTF8.GetBytes("ssrc"), transportContext.SynchronizationSourceIdentifier, true, null, false);

                //        //Option AssumeIdentity / CoalesceIdentities
                //        if(partyId.Equals(0)) packet.SynchronizationSourceIdentifier = transportContext.RemoteSynchronizationSourceIdentifier;
                //    }
                //}

                #endregion

                if (transportContext.InDiscovery)
                {
                    //Todo, should also check for CName with matching ssrc

                    if (IDisposedExtensions.IsNullOrDisposed(transportContext.ReceiversReport).Equals(false) &&
                        transportContext.ReceiversReport.SynchronizationSourceIdentifier.Equals(partyId))
                    {
                        transportContext.RemoteSynchronizationSourceIdentifier = partyId;
                    }
                    else if (IDisposedExtensions.IsNullOrDisposed(transportContext.SendersReport).Equals(false) &&
                        transportContext.SendersReport.SynchronizationSourceIdentifier.Equals(partyId))
                    {
                        transportContext.RemoteSynchronizationSourceIdentifier = partyId;
                    }
                }
                else if (transportContext.IsValid &&
                    transportContext.RemoteSynchronizationSourceIdentifier.Equals(partyId).Equals(false))
                {
                    transportContext.RemoteSynchronizationSourceIdentifier = partyId;
                }

                //Increment RtpPacketsReceived for the context relating to the packet.
                ++transportContext.RtpPacketsReceived;

                //The counters for the bytes will now be be updated (without the 12 octets of the header)
                //increment the counters (Only use the Payload.Count per the RFC) (new Erratta Submitted)
                //http://www.rfc-editor.org/errata_search.php?rfc=3550
                transportContext.RtpBytesRecieved += packet.Payload.Count;

                //Please note due to the 'consensus' achieved for this standard (RFC 1889 / RFC3550 / RFC3551)
                //The counters for the rtp bytes sent are specifically counted only to reveal average data rate...
                //A Senders report may only indicate the values which are allowed in the rfc. (Probably so middle boxes can't be detected)
                //Otherwise it's not complaint but no one will figure out how or why since its not supposed to effect annex calulcations...
                //Additionally the jitter caluclations would be messed up in most cases where a sourcelist or padding is used because it doesn't take those values into account
                //This implemenation doesn't suffer from this non-sense.

                transportContext.RfcRtpBytesRecieved += packetLength - (packet.Header.Size + packet.HeaderOctets + packet.PaddingOctets);

                //Set the time when the first RtpPacket was received if required
                if (transportContext.m_FirstPacketReceived == DateTime.MinValue) transportContext.m_FirstPacketReceived = packet.Created;

                //Update the SequenceNumber and Timestamp and calulcate Inter-Arrival (Mark the context as active)
                transportContext.UpdateJitterAndTimestamp(packet);

                //Set the last rtp in after inter-arrival has been calculated.
                transportContext.m_LastRtpIn = packet.Created;

                //If the instance does not handle frame changed events then return
                if (false.Equals(HandleFrameChanges)) return;

                #region HandleFrameChanges

                //Note
                //If the ssrc changed mid stream but the data is still somehow relevent to the lastFrame or currentFrame
                //Then the ssrc of the packet must be changed or the ssrc of the frame must be changed before adding the packet.

                //Todo, Add yet another Frame to increase the chances that late packets arrive

                //NextFrame, CurrentFrame, LastFrame

                int packetTimestamp = packet.Timestamp;

                //If a CurrentFrame was not allocated
                if (IDisposedExtensions.IsNullOrDisposed(transportContext.CurrentFrame))
                {
                    //make a frame with the copy of the packet
                    transportContext.CurrentFrame = new RtpFrame(packet.Clone(true, true, true, true, false, true))
                    {
                        AllowsMultiplePayloadTypes = transportContext.AllowsMultiplePayloadTypes
                    };

                    //The LastFrame changed
                    OnRtpFrameChanged(transportContext.CurrentFrame, transportContext);

                    //Nothing else to do
                    return;

                }//Check to see if the frame belongs to the last frame
                else if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext.LastFrame))
                    &&
                    packetTimestamp.Equals(transportContext.LastFrame.Timestamp))
                {
                    //If the packet was added to the frame
                    if (transportContext.LastFrame.TryAdd(packet.Clone(true, true, true, true, false, true), transportContext.AllowDuplicatePackets))
                    {
                        bool final = transportContext.LastFrame.Count >= transportContext.LastFrame.MaxPackets;

                        //The LastFrame changed so fire an event
                        OnRtpFrameChanged(transportContext.LastFrame, transportContext, final);

                        //Backup of frames in LastFrame.
                        if (final)
                        {
                            ILoggingExtensions.Log(Logger, InternalId + "HandleFrameChanges => LastFrame Disposing @ " + transportContext.LastFrame.Count);

                            transportContext.LastFrame.Dispose();

                            transportContext.LastFrame = null;
                        }

                    }
                    else
                    {
                        //Could jump to case log
                        ILoggingExtensions.Log(Logger, InternalId + "HandleFrameChanges => transportContext.LastFrame @ TryAdd failed, (" + packet.PayloadType + ") RecieveSequenceNumber = " + transportContext.RecieveSequenceNumber + ", PacketSequenceNumber = " + packet.SequenceNumber + " Timestamp = " + packetTimestamp + "=>" + packetTimestamp.Equals(transportContext.LastFrame.Timestamp) + "[" + transportContext.LastFrame.LowestSequenceNumber + "," + transportContext.LastFrame.HighestSequenceNumber + "]" + ". HasMarker = " + transportContext.LastFrame.HasMarker);
                    }

                    //Nothing else to do
                    return;

                }//Check to see if the frame belongs to a new frame
                else if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext.CurrentFrame))
                    &&
                    false.Equals(packetTimestamp.Equals(transportContext.CurrentFrame.Timestamp)))
                {
                    //////We already set to the value of packet.SequenceNumber in UpdateSequenceNumber.
                    //////Before cycling packets check the packets sequence number.
                    //int pseq = transportContext.RecieveSequenceNumber; //packet.SequenceNumber;

                    ////Only allow newer timestamps but wrapping should be allowed.
                    ////When the timestamp is lower and the sequence number is not in order this is a re-ordered packet. (needs to correctly check for wrapping sequence numbers)
                    //if (packetTimestamp < transportContext.CurrentFrame.Timestamp)
                    //{
                    //    //Could jump to case log
                    //    ILoggingExtensions.Log(Logger, InternalId + "HandleFrameChanges Ignored SequenceNumber " + pseq + " @ " + packetTimestamp + ". Current Timestamp =" + transportContext.CurrentFrame.Timestamp + ", Current LowestSequenceNumber = " + transportContext.CurrentFrame.LowestSequenceNumber);

                    //    return;
                    //}

                    //Dispose the last frame, it's going out of scope.
                    if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext.LastFrame)))
                    {
                        //Indicate the frame is going out of scope
                        OnRtpFrameChanged(transportContext.LastFrame, transportContext, true);

                        transportContext.LastFrame.Dispose();

                        transportContext.LastFrame = null;

                        //Move the frame to be finalized
                        //transportContext.Holding = transportContext.LastFrame;
                    }

                    //Move the current frame to the LastFrame
                    transportContext.LastFrame = transportContext.CurrentFrame;

                    //make a frame with the copy of the packet
                    transportContext.CurrentFrame = new RtpFrame(packet.Clone(true, true, true, true, false, true))
                    {
                        AllowsMultiplePayloadTypes = transportContext.AllowsMultiplePayloadTypes
                    };

                    //The current frame changed
                    OnRtpFrameChanged(transportContext.CurrentFrame, transportContext);

                    return;
                }//Check to see if the frame belongs to the current frame
                else if (false.Equals(IDisposedExtensions.IsNullOrDisposed(transportContext.CurrentFrame)) &&
                   packetTimestamp.Equals(transportContext.CurrentFrame.Timestamp))
                {
                    //If the packet was added to the frame
                    if (transportContext.CurrentFrame.TryAdd(packet.Clone(true, true, true, true, false, true), transportContext.AllowDuplicatePackets))
                    {
                        bool final = transportContext.CurrentFrame.Count >= transportContext.CurrentFrame.MaxPackets;

                        //The CurrentFrame changed
                        OnRtpFrameChanged(transportContext.CurrentFrame, transportContext, final);

                        //Backup of frames in CurrentFrame
                        if (final)
                        {
                            ILoggingExtensions.Log(Logger, InternalId + "HandleFrameChanges => CurrentFrame Disposing @ " + transportContext.CurrentFrame.Count);

                            transportContext.CurrentFrame.Dispose();

                            transportContext.CurrentFrame = null;
                        }
                    }
                    else
                    {
                        //Could jump to case log but would need to know current frame, nameof won't work here because the indirection
                        ILoggingExtensions.Log(Logger, InternalId + "HandleFrameChanges => transportContext.CurrentFrame@TryAdd failed, (" + packet.PayloadType + ") RecieveSequenceNumber = " + transportContext.RecieveSequenceNumber + ", PacketSequenceNumber = " + packet.SequenceNumber + ", Timestamp = " + packetTimestamp + "=>" + packetTimestamp.Equals(transportContext.CurrentFrame.Timestamp) + "[" + transportContext.CurrentFrame.LowestSequenceNumber + "," + transportContext.CurrentFrame.HighestSequenceNumber + "]" + ". HasMarker = " + transportContext.CurrentFrame.HasMarker);
                    }

                    return;
                }

                ILoggingExtensions.Log(Logger, InternalId + "HandleIncomingRtpPacket HandleFrameChanged (" + packet.PayloadType + ") @ " + packetTimestamp + " Does not belong to any frame.");

                #endregion
            }

            #endregion
        }

        /// <summary>
        /// Handles the logic of updating counters for the packet sent if <see cref="OutgoingRtpPacketEventsEnabled"/> is true.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="packet"></param>
        protected internal virtual void HandleOutgoingRtpPacket(object sender, RtpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false.Equals(HandleOutgoingRtpPackets) || IDisposedExtensions.IsNullOrDisposed(packet) || false.Equals(packet.Transferred.HasValue)) return;

            #region TransportContext Handles Packet

            TransportContext transportContext = tc ?? GetContextForPacket(packet);

            if (IDisposedExtensions.IsNullOrDisposed(transportContext)) return;

            unchecked
            {
                //This allows detection of middle boxes, make a seperate sample, default implementation should be 'compliant'...

                //increment the counters (Only use the Payload.Count per the RFC) (new Erratta Submitted)
                //http://www.rfc-editor.org/errata_search.php?rfc=3550
                transportContext.RtpBytesSent += packet.Payload.Count;


                //Please not due to the 'consensus' achieved for this standard (RFC 1889 / RFC3550 / RFC3551)
                //The counters for the rtp bytes sent are specifically counted only to reveal average data rate...
                //A Senders report may only indicate the values which are allowed in the rfc. (Probably so middle boxes can't be detected)
                //Otherwise it's not complaint but no one will figure out how or why since its not supposed to effect annex calulcations...
                //Additionally the jitter caluclations would be messed up in most cases where a sourcelist or padding is used because it doesn't take those values into account
                //This implemenation doesn't suffer from this non-sense.

                transportContext.RfcRtpBytesSent += packet.Length - (packet.Header.Size + packet.HeaderOctets + packet.PaddingOctets);

                ++transportContext.RtpPacketsSent;

                //Sample the clock for when the last rtp packet was sent
                DateTime sent = packet.Transferred.Value;

                //Set the time the first packet was sent.
                if (transportContext.m_FirstPacketSent == DateTime.MinValue) transportContext.m_FirstPacketSent = sent;

                //Just update the sequence number for the packet being sent
                transportContext.m_LastSentSequenceNumber = (ushort)packet.SequenceNumber;

                //If the packet was in sequence (does not really have to be checked, the jitter and timestamp should be updated anyway...
                //if (transportContext.UpdateSequenceNumber(packet.SequenceNumber))
                //{
                //Calculate inter-arrival and mark the context as active
                transportContext.UpdateJitterAndTimestamp(packet);
                //}

                //Store the time the last RtpPacket was sent.
                transportContext.m_LastRtpOut = sent;

                //Attempt to raise the event
                OnRtpPacketSent(packet, transportContext);
            }

            #endregion
        }

        /// <summary>
        /// Handles the logic of updating counters for the packet sent if <see cref="OutgoingRtcpPacketEventsEnabled"/> is true.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="packet"></param>OutgoingRtcpPacketEventsEnabled
        protected internal virtual void HandleOutgoingRtcpPacket(object sender, RtcpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || IDisposedExtensions.IsNullOrDisposed(packet) || false.Equals(HandleOutgoingRtcpPackets) || false.Equals(packet.Transferred.HasValue)) return;

            #region TransportContext Handles Packet

            TransportContext transportContext = tc ?? GetContextForPacket(packet);

            //if there is no context there is nothing to do.
            if (IDisposedExtensions.IsNullOrDisposed(transportContext)) return;

            unchecked
            {
                //Update the counters for the amount of bytes in the RtcpPacket including the header and any padding.
                transportContext.RtcpBytesSent += packet.Length;

                //Update the amount of packets sent
                ++transportContext.RtcpPacketsSent;

                //Mark the context as active immediately.
                transportContext.m_InactiveTime = TimeSpanExtensions.InfiniteTimeSpan;

                //Get the time the packet was sent
                DateTime sent = packet.Transferred.Value;

                //Store the last time a RtcpPacket was sent
                transportContext.m_LastRtcpOut = sent;

                //Set the time the first packet was sent.
                if (transportContext.m_FirstPacketSent == DateTime.MinValue) transportContext.m_FirstPacketSent = sent;

                //Attempt to raise the event
                OnRtcpPacketSent(packet, transportContext);
            }

            //Backoff based on ConverganceTime?

            #endregion
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected internal void OnOutOfBandData(byte[] data, int offset, int length)
        {
            if (IsDisposed) return;

            InterleavedDataHandler action = OutOfBandData;

            if (object.ReferenceEquals(action, null) || data == null || length.Equals(Binary.Zero)) return;

            if (m_ThreadEvents)
            {
                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(null, new PacketBase(data, offset, length, true, true), true, true));

                m_EventReady.Set();

                return;
            }

            foreach (InterleavedDataHandler handler in action.GetInvocationList())
            {
                try { handler(this, data, offset, length); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); return; }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelOutOfBandData(PacketBase packet = null)
        {
            if (IsDisposed) return;

            InterleavedDataHandler action = OutOfBandData;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet) || packet.Length.Equals(Binary.LongZero)) return;

            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;
                try { ((InterleavedDataHandler)(d))(this, packet.Data, 0, (int)packet.Length); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
            });

            //Don't have to waste cycles on this thread calling dispose...
            //Todo, check if ShouldDispose was set to false in event..
            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(packet))) BaseDisposable.SetShouldDispose(packet, true, false);
        }

        /// <summary>
        /// Raises the RtpPacket Handler for Recieving
        /// </summary>
        /// <param name="packet">The packet to handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected internal void OnRtpPacketReceieved(RtpPacket packet, TransportContext tc = null)
        {
            if (IsDisposed || false.Equals(IncomingRtpPacketEventsEnabled)) return;

            RtpPacketHandler action = RtpPacketReceieved;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            bool shouldDispose = packet.ShouldDispose;

            if (shouldDispose) SetShouldDispose(packet, false, false);

            if (m_ThreadEvents)
            {
                //Use a clone of the packet and data into a new reference so it can stay alive for the event.
                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(tc, packet.Clone(true, true, true, true, false, true), false, true));

                m_EventReady.Set();

                //todo, should call dispose is finalizer was missed...
                SetShouldDispose(packet, true, false);

                return;
            }

            foreach (RtpPacketHandler handler in action.GetInvocationList())
            {
                if (packet.IsDisposed || IsDisposed) break;
                try { handler(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); break; }
            }

            //Allow the packet to be destroyed if an event did not already change this.
            if (shouldDispose && packet.ShouldDispose.Equals(false) && false.Equals(IDisposedExtensions.IsNullOrDisposed(packet))) BaseDisposable.SetShouldDispose(packet, true, false);
        }

        /// <summary>
        /// Raises the RtcpPacketHandler for Recieving
        /// </summary>
        /// <param name="packet">The packet to handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected internal void OnRtcpPacketReceieved(RtcpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false.Equals(IncomingRtcpPacketEventsEnabled) || IsDisposed) return;

            RtcpPacketHandler action = RtcpPacketReceieved;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            bool shouldDispose = packet.ShouldDispose;

            if (shouldDispose) SetShouldDispose(packet, false, false);

            if (m_ThreadEvents)
            {
                //Todo, only clone if ShouldDispose is true.

                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(tc, packet.Clone(true, true, false), false, true));

                m_EventReady.Set();

                //todo, should call dispose is finalizer was missed...
                SetShouldDispose(packet, true, false);

                return;
            }

            foreach (RtcpPacketHandler handler in action.GetInvocationList())
            {
                if (packet.IsDisposed || IsDisposed) break;
                try { handler(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); break; }
            }

            //Allow the packet to be destroyed if an event did not already change this.
            if (shouldDispose && packet.ShouldDispose.Equals(false) && false.Equals(IDisposedExtensions.IsNullOrDisposed(packet))) BaseDisposable.SetShouldDispose(packet, true, false);
        }

        /// <summary>
        /// Raises the RtpFrameHandler for the given frame if FrameEvents are enabled
        /// </summary>
        /// <param name="frame">The frame to raise the RtpFrameHandler with</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected void OnRtpFrameChanged(RtpFrame frame = null, TransportContext tc = null, bool final = false)
        {
            if (IsDisposed || false.Equals(FrameChangedEventsEnabled) || IsDisposed) return;

            RtpFrameHandler action = RtpFrameChanged;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(frame) || frame.IsEmpty) return;

            bool shouldDispose = frame.ShouldDispose;

            if (shouldDispose) SetShouldDispose(frame, false, false);

            if (m_ThreadEvents)
            {
                //new RtpFrame(frame)
                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(tc, frame, final, true));

                m_EventReady.Set();

                return;
            }

            foreach (RtpFrameHandler handler in action.GetInvocationList())
            {
                if (IDisposedExtensions.IsNullOrDisposed(frame) || IsDisposed) break;
                try { handler(this, frame, tc, final); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); break; }
            }

            //On final events set ShouldDispose to true, do not call Dispose
            if (final && shouldDispose && IDisposedExtensions.IsNullOrDisposed(frame).Equals(false) && frame.ShouldDispose.Equals(false)) SetShouldDispose(frame, true, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelRtpFrameChanged(RtpFrame frame = null, TransportContext tc = null, bool final = false)
        {
            if (IsDisposed || false.Equals(FrameChangedEventsEnabled) || IsDisposed) return;

            RtpFrameHandler action = RtpFrameChanged;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(frame) || frame.IsEmpty) return;

            bool shouldDispose = frame.ShouldDispose;

            if (shouldDispose) SetShouldDispose(frame, false, false);

            //RtpFrameHandler would need the cast up front.
            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(frame) || IsDisposed) return;
                try { ((RtpFrameHandler)(d))(this, frame, tc, final); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
            });

            //On final events set ShouldDispose to true, do not call Dispose
            if (final && shouldDispose && IDisposedExtensions.IsNullOrDisposed(frame).Equals(false) && frame.ShouldDispose.Equals(false)) SetShouldDispose(frame, true, false);
        }

        //IPacket overload could reduce code but would cost time to check type.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelRtpPacketRecieved(RtpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false.Equals(HandleIncomingRtpPackets) || IsDisposed) return;

            RtpPacketHandler action = RtpPacketReceieved;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //RtpFrameHandler would need the cast up front.
            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;
                try { ((RtpPacketHandler)(d))(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
            });

            //Allow the packet to be disposed, do not call dispose now.
            //BaseDisposable.SetShouldDispose(packet, true, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelRtpPacketSent(RtpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false == HandleOutgoingRtpPackets || IsDisposed) return;

            RtpPacketHandler action = RtpPacketSent;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //RtpFrameHandler would need the cast up front.
            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;
                try { ((RtpPacketHandler)(d))(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
            });

            //allow packet to be disposed...
            //BaseDisposable.SetShouldDispose(packet, true, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelRtcpPacketRecieved(RtcpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false.Equals(HandleIncomingRtcpPackets) || IsDisposed) return;

            RtcpPacketHandler action = RtcpPacketReceieved;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //RtpFrameHandler would need the cast up front.
            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;
                try { ((RtcpPacketHandler)(d))(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
                //finally { packet.Dispose(); }
            });

            //Allow the packet to be disposed, do not call dispose now.
            //BaseDisposable.SetShouldDispose(packet, true, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParallelRtcpPacketSent(RtcpPacket packet = null, TransportContext tc = null)
        {
            if (IsDisposed || false == HandleOutgoingRtcpPackets || IsDisposed) return;

            RtcpPacketHandler action = RtcpPacketSent;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //RtpFrameHandler would need the cast up front.
            ParallelEnumerable.ForAll(action.GetInvocationList().AsParallel(), (d) =>
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;
                try { ((RtcpPacketHandler)(d))(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); }
                //finally { packet.Dispose(); }
            });

            //Allow the packet to be disposed, do not call dispose now.
            //BaseDisposable.SetShouldDispose(packet, true, false);
        }


        /// <summary>
        /// Raises the RtpPacket Handler for Sending
        /// </summary>
        /// <param name="packet">The packet to handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected void OnRtpPacketSent(RtpPacket packet, TransportContext tc = null)
        {
            if (IsDisposed || false == OutgoingRtpPacketEventsEnabled || IsDisposed) return;

            RtpPacketHandler action = RtpPacketSent;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) return;

            //bool shouldDispose = packet.ShouldDispose;

            //if (shouldDispose) SetShouldDispose(packet, false, false);

            if (m_ThreadEvents)
            {
                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(tc, packet, false, true));

                m_EventReady.Set();

                return;
            }

            foreach (RtpPacketHandler handler in action.GetInvocationList())
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) break;
                try { handler(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); break; }
            }

            //if(shouldDispose && false == packet.IsDisposed) BaseDisposable.SetShouldDispose(packet, true, false);
        }

        /// <summary>
        /// Raises the RtcpPacketHandler for Sending
        /// </summary>
        /// <param name="packet">The packet to handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected void OnRtcpPacketSent(RtcpPacket packet, TransportContext tc = null)
        {
            if (IsDisposed || false == OutgoingRtcpPacketEventsEnabled || IsDisposed) return;

            RtcpPacketHandler action = RtcpPacketSent;

            if (object.ReferenceEquals(action, null) || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //bool shouldDispose = packet.ShouldDispose;

            //if (shouldDispose) SetShouldDispose(packet, false, false);

            if (m_ThreadEvents)
            {
                m_EventData.Enqueue(new Tuple<TransportContext, BaseDisposable, bool, bool>(tc, packet, false, true));

                return;
            }

            foreach (RtcpPacketHandler handler in action.GetInvocationList())
            {
                if (IDisposedExtensions.IsNullOrDisposed(packet) || IsDisposed) break;
                try { handler(this, packet, tc); }
                catch (Exception ex) { ILoggingExtensions.LogException(Logger, ex); break; }
            }

            //if (shouldDispose) BaseDisposable.SetShouldDispose(packet, true, false);
        }

        //Frame sent.

        #endregion

        #region Properties

        //Todo, determine if packets should just not be enqueued anymore
        //Should also apply for Rtcp.       

        /// <summary>
        /// Used in applications to determine send thresholds.
        /// </summary>
        public int MaximumOutgoingPackets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Gets the number of RtpPacket instances queued to be sent.
        /// </summary>
        public int OutgoingRtpPacketCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_OutgoingRtpPackets.Count;
            }
        }

        /// <summary>
        /// Gets or sets a value which indicates if the socket operations for sending will use the IList overloads.
        /// </summary>
        public bool IListSockets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_IListSockets;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                //Todo, the objects may be in use on the curent call
                //if (m_ThreadEvents)
                //{

                //}

                m_IListSockets = value;
            }
        }

        /// <summary>
        /// Gets or sets a value which indicates if events will be threaded or not.
        /// If threading is enabled the call will block until the event thread has started.
        /// </summary>
        public bool ThreadEvents //Enable
        {
            [MethodImpl(MethodImplOptions.Synchronized | MethodImplOptions.AggressiveInlining)]
            get { return m_ThreadEvents; }

            [MethodImpl(MethodImplOptions.Synchronized | MethodImplOptions.AggressiveInlining)]
            set
            {
                if (false.Equals(IsActive)) return;

                if (value.Equals(m_ThreadEvents)) return;

                //Update the value.
                m_ThreadEvents = value;

                if (value == true)
                {
                    if (m_EventThread == null || EventsStarted.Equals(DateTime.MinValue))
                    {
                        //Create the event thread
                        m_EventThread = new Thread(new ThreadStart(HandleEvents), ThreadExtensions.MinimumStackSize);

                        //Configure
                        ConfigureThread(m_EventThread); //should pass name and logging.

                        //Assign name
                        m_EventThread.Name = "RtpClient-Events-" + InternalId;

                        //Start highest
                        m_EventThread.Priority = ThreadPriority.Highest;

                        //Start thread
                        m_EventThread.Start();
                    }

                    //Wait for the start while the value was not changed and the thread is not started.
                    while (m_ThreadEvents && EventsStarted == DateTime.MinValue && false.Equals(m_EventThread == null)) m_EventReady.Wait(TimeSpanExtensions.OneTick);
                }
                else
                {
                    //Not started
                    EventsStarted = DateTime.MinValue;

                    //Set lowest priority on event thread.
                    m_EventThread.Priority = ThreadPriority.Lowest;

                    //Abort and free the thread.
                    ThreadExtensions.AbortAndFree(ref m_EventThread);

                    //Handle any remaining events so the packets in Queue don't get disposed...
                    //while (m_ThreadEvents == false && EventsStarted == DateTime.MinValue && false == m_EventData.IsEmpty)
                    //{
                    //    HandleEvent();

                    //    //m_EventReady.Wait(m_EventReady.SpinCount >> 2);
                    //}

                    //Ensure Cleared
                    //if (m_ThreadEvents == false && EventsStarted == DateTime.MinValue) m_EventData.Clear();
                }
            }
        }

        public Action<Thread> ConfigureThread { get; set; }

        /// <summary>
        /// The maximum amount of bandwidth Rtcp can utilize (of the overall bandwidth available to the RtpClient) during reports
        /// </summary>
        public double AverageMaximumRtcpBandwidthPercentage
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        ///There should be a SendersPercentage and a ReceiversPercentage
        /// Per 6.2 RTCP Transmission Interval
        /// 
        ///The above property more accurately reflects the `limit called the "session bandwidth"`,
        ///and as is currently implemented when set to 0 allows unlimited reporting which is definitely not accurate to RFC3550 
        /// which does state that `Using two parameters allows RTCP reception reports to be turned off entirely for a particular session by setting the RTCP bandwidth for non-data-senders to zero while
        /// keeping the RTCP bandwidth for data senders non-zero so that sender reports can still be sent for inter-media synchronization.  Turning off RTCP reception reports is NOT RECOMMENDED...`
        /// 
        /// It is my interpretation and subsequently this implementation that setting the value to 0 currently causes unlimited reports to be sent.

        //It is also noted:
        //A higher-level session control protocol, which is beyond the scope of this document, may be needed.

        /// <summary>
        /// Gets or sets a value which allows the OnRtpPacketEvent to be raised.
        /// </summary>
        public bool IncomingRtpPacketEventsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the OnRtcpPacketEvent to be raised.
        /// </summary>
        public bool IncomingRtcpPacketEventsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the OnRtpPacketSent to be raised.
        /// </summary>
        public bool OutgoingRtpPacketEventsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the OnRtcpPacketSent to be raised.
        /// </summary>
        public bool OutgoingRtcpPacketEventsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the instance to handle any incoming RtpPackets
        /// </summary>
        public bool HandleIncomingRtpPackets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the instance to handle any incoming RtcpPackets
        /// </summary>
        public bool HandleIncomingRtcpPackets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the instance to handle any outgoing RtpPackets
        /// </summary>
        public bool HandleOutgoingRtpPackets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the instance to handle any outgoing RtcpPackets
        /// </summary>
        public bool HandleOutgoingRtcpPackets
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which prevents <see cref="RtpFrameChanged"/> from being fired.
        /// </summary>
        public bool FrameChangedEventsEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets a value which allows the instance to create a RtpFrame based on the incoming rtp packets.
        /// </summary>
        public bool HandleFrameChanges
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets the value will be used as the CName when creating RtcpReports
        /// </summary>
        public string ClientName
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set;
        }

        /// <summary>
        /// Gets or sets the list of additional items which will be sent with the SourceDescriptionReport if AverageRtcpBandwidthExceeded is not exceeded.
        /// </summary>
        public readonly List<SourceDescriptionReport.SourceDescriptionItem> AdditionalSourceDescriptionItems = new List<SourceDescriptionReport.SourceDescriptionItem>();

        /// <summary>
        /// Gets a value indicating if the RtpClient is not disposed and the WorkerThread is alive.
        /// </summary>
        public virtual bool IsActive
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return IsDisposed.Equals(false) &&
                    Started.Equals(DateTime.MinValue).Equals(false) &&
                    object.ReferenceEquals(m_WorkerThread, null).Equals(false) &&
                    (m_WorkerThread.IsAlive || m_StopRequested.Equals(false));
            }
        }

        /// <summary>
        /// Gets a value which indicates if any underlying <see cref="RtpClient.TransportContext"/> owned by this RtpClient instance utilizes Rtcp.
        /// </summary>
        public bool RtcpEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return TransportContexts.Any(c => c.IsRtcpEnabled); }
        }

        /// <summary>
        /// Gets a value which indicates if any underlying <see cref="RtpClient.TransportContext"/> owned by this RtpClient instance utilizes Rtcp.
        /// </summary>
        public bool RtpEnabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return TransportContexts.Any(c => c.IsRtpEnabled); }
        }

        /// <summary>
        /// Indicates if the amount of bandwith currently utilized for Rtcp reporting has exceeded the amount of bandwidth allowed by the <see cref="AverageMaximumRtcpBandwidthPercentage"/> property.
        /// </summary>
        public bool AverageRtcpBandwidthExceeded
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (false.Equals(RtcpEnabled) || IsDisposed) return true;

                //If disposed no limit is imposed do not check

                double averageMaximumRtcpBandwidthPercentage = AverageMaximumRtcpBandwidthPercentage;

                if (averageMaximumRtcpBandwidthPercentage.Equals(Binary.DoubleZero)) return false;

                int amountOfContexts = TransportContexts.Count;

                if (amountOfContexts.Equals(Binary.Zero)) return true;

                //Obtain the summation of the total bytes sent over the amount of context's
                long totalReceived = TotalBytesReceieved;

                if (totalReceived.Equals(Binary.LongZero)) return false;

                long totalRtcp = TotalRtcpBytesSent + TotalRtcpBytesReceieved;

                if (totalRtcp.Equals(Binary.LongZero)) return false;

                return totalRtcp >= totalReceived / averageMaximumRtcpBandwidthPercentage;
            }
        }

        #region Bandwidth and Uptime and Counters

        /// <summary>
        /// The Date and Time the RtpClient was Connected
        /// </summary>
        public DateTime Started { get; private set; }

        public DateTime EventsStarted { get; private set; }

        /// <summary>
        /// The amount of time the RtpClient has been recieving media
        /// </summary>
        public TimeSpan Uptime { get { return DateTime.UtcNow - Started; } }

        /// <summary>
        /// The total amount of RtpPackets sent of all contained TransportContexts
        /// </summary>
        public long TotalRtpPacketsSent { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtpPacketsSent); } }

        /// <summary>
        /// The total amount of Rtp bytes sent of all contained TransportContexts
        /// </summary>
        public long TotalRtpBytesSent { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.TotalRtpBytesSent); } }

        /// <summary>
        /// The total amount of Rtp bytes received of all contained TransportContexts
        /// </summary>
        public long TotalRtpBytesReceieved { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.TotalRtpBytesReceieved); } }

        /// <summary>
        /// The total amount of Rtp packets received of all contained TransportContexts
        /// </summary>
        public long TotalRtpPacketsReceieved { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtpPacketsReceived); } }

        /// <summary>
        /// The total amount of Rtcp packets sent of all contained TransportContexts
        /// </summary>
        public long TotalRtcpPacketsSent { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtcpPacketsSent); } }

        /// <summary>
        /// The total amount of Rtcp bytes sent of all contained TransportContexts
        /// </summary>
        public long TotalRtcpBytesSent { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtcpBytesSent); } }

        /// <summary>
        /// The total amount of bytes received of all contained TransportContexts
        /// </summary>
        public long TotalBytesReceieved { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.TotalBytesReceieved); } }

        /// <summary>
        /// The total amount of bytes sent of all contained TransportContexts
        /// </summary>
        public long TotalBytesSent { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.TotalBytesSent); } }

        /// <summary>
        /// The total amount of Rtcp packets received of all contained TransportContexts
        /// </summary>
        public long TotalRtcpPacketsReceieved { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtcpPacketsReceived); } }

        /// <summary>
        /// The total amount of Rtcp bytes received of all contained TransportContexts
        /// </summary>
        public long TotalRtcpBytesReceieved { get { return IsDisposed ? 0 : TransportContexts.Sum(c => c.RtcpBytesRecieved); } }

        #endregion

        #endregion

        #region Constructor

        static RtpClient()
        {
            //Todo make static with port. static readonly int DefaultRtpTransportPort. (see if Rtcp also has one)
            if (false == UriParser.IsKnownScheme(RtpProtcolScheme)) UriParser.Register(new HttpStyleUriParser(), RtpProtcolScheme, 9670);
        }

        RtpClient(bool shouldDispose = true)
            : base(shouldDispose)
        {
            AverageMaximumRtcpBandwidthPercentage = DefaultReportInterval.TotalSeconds;

            ConfigureThread = ConfigureRtpThread;
        }

        /// <summary>
        /// Assigns the events necessary for operation and creates or assigns memory to use as well as inactivtyTimout.
        /// </summary>
        /// <param name="memory">The optional memory segment to use</param>
        /// <param name="incomingPacketEventsEnabled"><see cref="IncomingPacketEventsEnabled"/></param>
        /// <param name="frameChangedEventsEnabled"><see cref="FrameChangedEventsEnabled"/></param>
        public RtpClient(MemorySegment memory = null, bool incomingPacketEventsEnabled = true, bool frameChangedEventsEnabled = true, bool outgoingPacketEvents = true, bool shouldDispose = true)
            : this(shouldDispose)
        {
            if (memory == null)
            {
                //Determine a good size based on the MTU (this should cover most applications)
                //Need an IP or the default IP to ensure the MTU Matches, use 1600 because 1500 is unaligned.
                m_Buffer = new MemorySegment(1600);
            }
            else
            {
                m_Buffer = memory;

                if (m_Buffer.Count < RtpHeader.Length) throw new ArgumentOutOfRangeException("memory", "memory.Count must contain enough space for a RtpHeader");
            }

            //RtpPacketReceieved += new RtpPacketHandler(HandleIncomingRtpPacket);
            //RtcpPacketReceieved += new RtcpPacketHandler(HandleIncomingRtcpPacket);
            //RtpPacketSent += new RtpPacketHandler(HandleOutgoingRtpPacket);
            //RtcpPacketSent += new RtcpPacketHandler(HandleOutgoingRtcpPacket);
            //InterleavedData += new InterleaveHandler(HandleInterleavedData);

            //Allow events to be raised
            HandleIncomingRtpPackets = HandleIncomingRtcpPackets = IncomingRtpPacketEventsEnabled = IncomingRtcpPacketEventsEnabled = incomingPacketEventsEnabled;

            //Fire events for packets received and Allow events to be raised
            HandleOutgoingRtpPackets = HandleOutgoingRtcpPackets = OutgoingRtpPacketEventsEnabled = OutgoingRtcpPacketEventsEnabled = outgoingPacketEvents;

            //Handle frame changes and Allow frame change events to be raised
            HandleFrameChanges = FrameChangedEventsEnabled = frameChangedEventsEnabled;
        }

        /// <summary>
        /// Creates a RtpClient instance using the given array as a buffer.
        /// </summary>
        /// <param name="buffer">The buffer to use</param>
        /// <param name="offset">The offset to start using the buffer at</param>
        /// <param name="incomingPacketEventsEnabled"><see cref="IncomingPacketEventsEnabled"/></param>
        /// <param name="frameChangedEventsEnabled"><see cref="FrameChangedEventsEnabled"/></param>
        public RtpClient(byte[] buffer, int offset = 0, bool incomingPacketEventsEnabled = true, bool frameChangedEventsEnabled = true, bool outgoingPacketEvents = true, bool shouldDispose = true)
            : this(new MemorySegment(buffer, offset), incomingPacketEventsEnabled, frameChangedEventsEnabled, outgoingPacketEvents, shouldDispose) { }

        /// <summary>
        /// Creates a RtpClient instance using the given array as a buffer.
        /// </summary>
        /// <param name="buffer">The buffer to use</param>
        /// <param name="offset">The offset to start using the buffer at</param>
        /// <param name="count">The amount of bytes to use in the buffer</param>
        /// <param name="incomingPacketEventsEnabled"><see cref="IncomingPacketEventsEnabled"/></param>
        /// <param name="frameChangedEventsEnabled"><see cref="FrameChangedEventsEnabled"/></param>
        public RtpClient(byte[] buffer, int offset, int count, bool incomingPacketEventsEnabled = true, bool outgoingPacketEvents = true, bool frameChangedEventsEnabled = true, bool shouldDispose = true)
            : this(new MemorySegment(buffer, offset, count), incomingPacketEventsEnabled, frameChangedEventsEnabled, outgoingPacketEvents, shouldDispose) { }

        #endregion

        #region Overloads               

        public override string ToString()
        {
            return string.Join(((char)ASCII.HyphenSign).ToString(), base.ToString(), InternalId);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Adds a the given context to the instances owned by this client. 
        /// Throws a RtpClientException if the given context conflicts in channel either data or control with that of one which is already owned by the instance.
        /// </summary>
        /// <param name="context">The context to add</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void AddContext(TransportContext context, bool checkDataChannel = true, bool checkControlChannel = true, bool checkLocalIdentity = true, bool checkRemoteIdentity = true)
        {
            if (checkDataChannel || checkControlChannel || checkLocalIdentity || checkRemoteIdentity) foreach (TransportContext c in TransportContexts)
                {
                    //If checking channels
                    if (checkDataChannel || checkControlChannel)
                    {
                        //If checking the data channel
                        if (checkDataChannel && c.DataChannel == context.DataChannel || c.ControlChannel == context.DataChannel)
                        {
                            TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Data Channel is already in use by the context in the Tag");
                        }

                        //if checking the control channel
                        if (checkControlChannel && c.ControlChannel == context.ControlChannel || c.DataChannel == context.ControlChannel)
                        {
                            TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Control Channel is already in use by the context in the Tag");
                        }

                    }

                    //If the identity will overlap the Payload type CANNOT be the same







                    //if chekcking local identifier
                    if (checkLocalIdentity && c.SynchronizationSourceIdentifier == context.SynchronizationSourceIdentifier)
                    {
                        foreach (var pt in context.MediaDescription.PayloadTypes)
                        {
                            if (System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, pt))
                            {
                                TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Local SSRC is already in use by the context in the Tag");
                            }
                        }
                    }

                    //if chekcking remote identifier (and it has been defined)
                    if (checkRemoteIdentity && false.Equals(context.InDiscovery) && false.Equals(c.InDiscovery) &&
                        c.RemoteSynchronizationSourceIdentifier == context.RemoteSynchronizationSourceIdentifier)
                    {
                        foreach (var pt in context.MediaDescription.PayloadTypes)
                        {
                            if (System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, pt))
                            {
                                TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Remote SSRC is already in use by the context in the Tag");
                            }
                        }
                    }
                }


            //Add the context (This can introduce incorrect logic if the caller adds the context with channels in a reverse order, e.g. 2-3, 0-1)
            TransportContexts.Add(context);

            //Should check if sending is allowed via the media description
            if (context.IsActive) SendReports(context);
        }

        public /*virtual*/ bool TryAddContext(TransportContext context) { try { AddContext(context); } catch { return false; } return true; }

        /// <summary>
        /// Removes the given <see cref="TransportContext"/>
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ bool TryRemoveContext(TransportContext context)
        {
            try
            {
                return TransportContexts.Remove(context);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets any <see cref="TransportContext"/> used by this instance.
        /// </summary>
        /// <returns>The <see cref="TransportContexts"/> used by this instance.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ IEnumerable<TransportContext> GetTransportContexts()
        {
            //if (IsDisposed) return Enumerable.Empty<TransportContext>();
            try
            {
                return TransportContexts;
            }
            catch (System.InvalidOperationException)
            {
                //May duplicate objects already projected, store index or use for construct.
                return GetTransportContexts();
            }
            catch { throw; }
        }

        #region Rtcp


        /// <summary>
        /// Creates any <see cref="RtcpReport"/>'s which are required by the implementation.
        /// The <see cref="SendersReport"/> and <see cref="ReceiversReport"/> (And accompanying <see cref="SourceDescriptionReport"/> if bandwidth allows) are created for the given context.
        /// </summary>
        /// <param name="context">The context to prepare Rtcp reports for</param>
        /// <param name="checkBandwidth">Indicates if the bandwidth of the RtpCliet or Context given should be checked.</param>
        /// <param name="storeReports">Indicates if the reports created should be stored on the corresponding properties of the instace.</param>
        /// <returns>The RtcpReport created.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IEnumerable<Rtcp.RtcpReport> PrepareReports(TransportContext context, bool checkBandwidth = true, bool storeReports = true)
        {
            //Start with a sequence of empty packets
            IEnumerable<Rtcp.RtcpReport> compound = System.Linq.Enumerable.Empty<Rtcp.RtcpReport>();

            int reports = 0;

            //If Rtp data was sent then send a Senders Report.
            if (context.RtpPacketsSent > 0)
            {
                //Insert the last SendersReport as the first compound packet
                if (storeReports)
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(LinqExtensions.Yield((context.SendersReport = TransportContext.CreateSendersReport(context, false))), compound);
                else
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(LinqExtensions.Yield(TransportContext.CreateSendersReport(context, false)), compound);

                ++reports;
            }

            //If Rtp data was received OR Rtcp data was sent then send a Receivers Report.
            if (context.RtpPacketsReceived > 0 || context.TotalRtcpBytesSent > 0)
            {
                //Insert the last ReceiversReport as the first compound packet
                if (storeReports)
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(LinqExtensions.Yield((context.ReceiversReport = TransportContext.CreateReceiversReport(context, false))), compound);
                else
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(LinqExtensions.Yield(TransportContext.CreateReceiversReport(context, false)), compound);

                ++reports;
            }

            //If there are any packets to be sent AND we don't care about bandwidth OR the bandwidth is not exceeded
            if (reports > 0 &&
                (checkBandwidth == false || false == context.RtcpBandwidthExceeded))
            {
                //Todo, possibly send additional items only when AverageRtcpBandwidth is not exceeded...

                //Include the SourceDescription
                if (storeReports)
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(compound, LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context, (string.IsNullOrWhiteSpace(ClientName) ? null : new Rtcp.SourceDescriptionReport.SourceDescriptionItem(Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem.SourceDescriptionItemType.CName, System.Text.Encoding.UTF8.GetBytes(ClientName))), AdditionalSourceDescriptionItems))));
                else
                    compound = System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(LinqExtensions.Yield(TransportContext.CreateSourceDescription(context, (string.IsNullOrWhiteSpace(ClientName) ? null : new Rtcp.SourceDescriptionReport.SourceDescriptionItem(Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem.SourceDescriptionItemType.CName, System.Text.Encoding.UTF8.GetBytes(ClientName))), AdditionalSourceDescriptionItems)), compound);
            }

            //Could also put a Goodbye for inactivity ... :) Currently handled by SendGoodbye, possibly allow for optional parameter where this occurs here.

            return compound;
        }

        /// <summary>
        /// Sends any reports required for all owned TransportContexts using <see cref="SendReports"/>
        /// </summary>
        /// <returns>A value indicating if reports were immediately sent</returns>        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ bool SendReports()
        {
            if (m_StopRequested) return false;

            bool sentAny = false;

            foreach (TransportContext tc in TransportContexts)
            {
                if (IDisposedExtensions.IsNullOrDisposed(tc).Equals(false) &&
                    tc.IsRtcpEnabled && SendReports(tc))
                {
                    sentAny = true;
                }
            }

            return sentAny;
        }

        /// <summary>
        /// Sends a Goodbye to for all contained TransportContext, which will also stop the process sending or receiving after the Goodbye is sent
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendGoodbyes()
        {
            foreach (RtpClient.TransportContext tc in TransportContexts)
                SendGoodbye(tc, null, tc.SynchronizationSourceIdentifier);
        }

        /// <summary>
        /// Sends a GoodbyeReport and stores it in the <paramref name="context"/> given if the <paramref name="ssrc"/> is also given and is equal to the <paramref name="context.SynchronizationSourceIdentifier"/>
        /// </summary>
        /// <param name="context">The context of the report</param>
        /// <param name="reasonForLeaving">An optional reason why the report is being sent.</param>
        /// <param name="ssrc">The optional identity to use in the report.</param>
        /// <param name="force">Indicates if the call should be forced. <see cref="IsRtcpEnabled"/>, when true the report will also not be stored</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ int SendGoodbye(TransportContext context, byte[] reasonForLeaving = null, int? ssrc = null, bool force = false, RFC3550.SourceList sourceList = null, bool empty = false)
        {
            //Check if the Goodbye can be sent.
            if (IsDisposed //If the RtpClient is disposed 
                || //OR the context is disposed
                IDisposedExtensions.IsNullOrDisposed(context)
                || //OR the call has not been forced AND the context IsRtcpEnabled AND the context is active
                (false.Equals(force) && context.IsRtcpEnabled && context.IsActive
                && //AND the final Goodbye was sent already
                object.ReferenceEquals(context.Goodbye, null).Equals(false) &&
                context.Goodbye.Transferred.HasValue))
            {
                //Indicate nothing was sent
                return 0;
            }

            //Make a Goodbye, indicate version in Client, allow reason for leaving and optionall other sources
            Rtcp.GoodbyeReport goodBye = TransportContext.CreateGoodbye(context, reasonForLeaving, ssrc ?? context.SynchronizationSourceIdentifier, sourceList);

            //If the sourceList is null and empty is true then indicate so by using 0 (the source should ignore, this is to indicate various things if required)
            //Context should have an option SendEmptyGoodbyeOnInactivity

            if (IDisposedExtensions.IsNullOrDisposed(sourceList) && empty) goodBye.BlockCount = 0;

            //Store the Goodbye in the context if not forced the ssrc was given and it was for the context given.
            if (false.Equals(force) && ssrc.HasValue && ssrc.Value.Equals(context.SynchronizationSourceIdentifier)) context.Goodbye = goodBye;

            //Send the packet and return the amount of bytes which resulted.
            return SendRtcpPackets(System.Linq.Enumerable.Concat(PrepareReports(context, false, true), LinqExtensions.Yield(goodBye)));
        }

        /// <summary>
        /// Sends a <see cref="Rtcp.SendersReport"/> for each TranportChannel if allowed by the <see cref="MaximumRtcpBandwidth"/>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendSendersReports()
        {
            if (false == IsDisposed && false == m_StopRequested) foreach (TransportContext tc in TransportContexts) SendSendersReport(tc);
        }

        /// <summary>
        /// Send any <see cref="SendersReport"/>'s required by the given context immediately reguardless of <see cref="MaximumRtcpBandwidth"/>
        /// Return the amount of bytes sent when sending the reports.
        /// </summary>
        /// <param name="context">The context</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.Synchronized)]
        internal protected /*virtual*/ int SendSendersReport(TransportContext context, bool force = false)
        {
            //Determine if the SendersReport can be sent.
            if (IsDisposed //If the context is disposed
                && //AND the call has not been forced AND the context IsRtcpEnabled 
                (false == force && true == context.IsRtcpEnabled)
                // OR there is no RtcpSocket
                || context.RtcpSocket == null)
            {
                //Indicate nothing was sent
                return 0;
            }

            //Ensure the SynchronizationSourceIdentifier of the transportChannel is assigned
            context.AssignIdentity();

            //First report include no blocks (No last senders report), store the report being sent
            context.SendersReport = TransportContext.CreateSendersReport(context, false);

            //Always send compound with SourceDescription for now
            return SendRtcpPackets(System.Linq.Enumerable.Concat(LinqExtensions.Yield<RtcpPacket>(context.SendersReport), LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context)))));
        }

        /// <summary>
        /// Send any <see cref="ReceiversReports"/> required by this RtpClient instance.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendReceiversReports()
        {
            if (false.Equals(IsDisposed) && false.Equals(m_StopRequested)) foreach (TransportContext tc in TransportContexts) SendReceiversReport(tc);
        }

        /// <summary>
        /// Send any <see cref="ReceiversReports"/>'s required by the given context immediately reguardless <see cref="MaximumRtcpBandwidth"/>
        /// Return the amount of bytes sent when sending the reports.
        /// </summary>
        /// <param name="context">The context</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ int SendReceiversReport(TransportContext context, bool force = false)
        {
            //Determine if the ReceiversReport can be sent.
            if (IsDisposed //If the context is disposed
                && //AND the call has not been forced AND the context IsRtcpEnabled 
                (false.Equals(force) && context.IsRtcpEnabled)
                // OR there is no RtcpSocket
                || object.ReferenceEquals(context.RtcpSocket, null))
            {
                //Indicate nothing was sent
                return 0;
            }

            //Ensure the SynchronizationSourceIdentifier of the transportContext is assigned
            context.AssignIdentity();

            //create and store the receivers report sent
            context.ReceiversReport = TransportContext.CreateReceiversReport(context, false);

            //If the bandwidth is not exceeded also send a SourceDescription
            if (AverageRtcpBandwidthExceeded.Equals(false))
            {
                return SendRtcpPackets(System.Linq.Enumerable.Concat(LinqExtensions.Yield<RtcpPacket>(context.ReceiversReport),
                    LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context)))));
            }

            //Just send the ReceiversReport
            return SendRtcpPackets(LinqExtensions.Yield(context.ReceiversReport));
        }

        /// <summary>
        /// Selects a TransportContext by matching the SynchronizationSourceIdentifier to the given sourceid
        /// </summary>
        /// <param name="sourceId"></param>
        /// <returns>The context which was identified or null if no context was found.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ TransportContext GetContextBySourceId(int sourceId)
        {
            if (IsDisposed) return null;

            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (IDisposedExtensions.IsNullOrDisposed(c).Equals(false) &&
                    c.SynchronizationSourceIdentifier.Equals(sourceId) || c.RemoteSynchronizationSourceIdentifier.Equals(sourceId)) break;

                c = null;
            }

            return c;
        }

        //DataChannel ControlChannel or overload?

        ////internal protected virtual TransportContext GetContextByChannel(byte channel)
        ////{
        ////    if (IsDisposed) return null;
        ////    try
        ////    {
        ////        foreach (RtpClient.TransportContext tc in TransportContexts)
        ////            if (tc.DataChannel == channel || tc.ControlChannel == channel) return tc;
        ////    }
        ////    catch (InvalidOperationException) { return GetContextByChannel(channel); }
        ////    catch { if (false == IsDisposed) throw; }
        ////    return null;
        ////}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ TransportContext GetContextByChannels(params byte[] channels)
        {
            if (IsDisposed) return null;

            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (false.Equals(IDisposedExtensions.IsNullOrDisposed(c)) &&
                    System.Linq.Enumerable.Contains(channels, c.DataChannel) || System.Linq.Enumerable.Contains(channels, c.ControlChannel)) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by using the packet's Channel property
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ TransportContext GetContextForPacket(RtcpPacket packet)
        {
            if (IsDisposed || IDisposedExtensions.IsNullOrDisposed(packet)) return null;
            //Determine based on reading the packet this is where a RtcpReport class would be useful to allow reading the Ssrc without knownin the details about the type of report
            try { return GetContextBySourceId(packet.SynchronizationSourceIdentifier); }
            catch (System.InvalidOperationException) { return GetContextForPacket(packet); }
            catch { if (false.Equals(IsDisposed)) throw; }
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void EnquePacket(RtcpPacket packet)
        {
            if (IsDisposed || m_StopRequested || IDisposedExtensions.IsNullOrDisposed(packet) || MaximumOutgoingPackets > 0 && m_OutgoingRtpPackets.Count > MaximumOutgoingPackets)
            {
                //Turn threading on.
                ThreadEvents = true;

                //Enqueue the packet as not to drop it
                m_OutgoingRtcpPackets.Add(packet);

                return;
            }

            //Enqueue the packet
            m_OutgoingRtcpPackets.Add(packet);
        }


        /// <summary>
        /// Sends the given packets, this function assumes all packets sent belong to the same party.
        /// </summary>
        /// <param name="packets"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ int SendRtcpPackets(IEnumerable<RtcpPacket> packets, TransportContext context, out System.Net.Sockets.SocketError error)
        {
            error = SocketError.SocketError;

            if (IsDisposed || packets == null) return 0;

            //If we don't have an transportContext to send on or the transportContext has not been identified or Rtcp is Disabled or there is no remote rtcp end point
            if (IDisposedExtensions.IsNullOrDisposed(context) || context.SynchronizationSourceIdentifier.Equals(Binary.Zero) || false.Equals(context.IsRtcpEnabled) || context.RemoteRtcp == null)
            {
                //Return
                return 0;
            }

            //Todo Determine from Context to use control channel and length. (Check MediaDescription)

            //When sending more then one packet compound packets must be padded correctly.

            //Use ToCompoundBytes to ensure that all compound packets are correctly formed.
            //Don't Just `stack` the packets as indicated if sending, assuming they are valid.

            //how manu bytes sent so far.
            int sent = 0;

            int length = 0;

            if (m_IListSockets)
            {
                List<System.ArraySegment<byte>> buffers = new List<System.ArraySegment<byte>>();
                IList<System.ArraySegment<byte>> packetBuffers;

                //Try to get the buffer for each packet
                foreach (RtcpPacket packet in packets)
                {
                    //If we can
                    if (packet.TryGetBuffers(out packetBuffers))
                    {
                        //Add those buffers
                        buffers.AddRange(packetBuffers);

                        //Keep track of the length
                        length += packet.Length;
                    }
                    else
                    {
                        //Just send them in their own array.
                        sent += SendData(System.Linq.Enumerable.ToArray(RFC3550.ToCompoundBytes(packets)),
                            context.ControlChannel, context.RtcpSocket, context.RemoteRtcp, out error);

                        buffers = null;

                        break;
                    }

                }

                //If nothing was sent and the buffers are not null and the socket is tcp use framing.
                if (length > 0 && context.IsActive && sent == 0 && false.Equals(buffers == null))
                {
                    if (context.RtcpSocket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, should have function to create framing to be compatible with RFC4571
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, context.ControlChannel, 0, 0 };

                        Binary.Write16(framing, 2, Binary.IsLittleEndian, (short)length);

                        //Add the framing
                        buffers.Insert(0, new System.ArraySegment<byte>(framing));
                    }

                    //Send that data.
                    sent += context.RtcpSocket.Send(buffers, System.Net.Sockets.SocketFlags.None, out error);
                }
            }
            else
            {

                //Iterate the packets
                foreach (RtcpPacket packet in packets)
                {
                    //If the data is not contigious
                    if (false.Equals(packet.IsContiguous()))
                    {
                        //Just send all packets in their own array by projecting the data (causes an allocation)
                        sent += SendData(System.Linq.Enumerable.ToArray(RFC3550.ToCompoundBytes(packets)),
                            context.ControlChannel, context.RtcpSocket, context.RemoteRtcp, out error);

                        //Stop here.
                        break;
                    }

                    //Account for the length of the packet
                    length += packet.Length;
                }

                //If nothing was sent then send the data now.
                if (length > 0 && sent == 0)
                {
                    //Send the framing seperately to keep the allocations minimal.

                    //Note, Live555 and LibAV may not be able to handle this, use IListSockets to work around.
                    if (context.RtcpSocket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, should have function to create framing to be compatible with RFC4571
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, context.ControlChannel, 0, 0 };

                        Binary.Write16(framing, 2, Binary.IsLittleEndian, (short)length);

                        while (sent < InterleavedOverhead &&
                            (false.Equals(error == SocketError.ConnectionAborted) &&
                            false.Equals(error == SocketError.ConnectionReset) &&
                            false.Equals(error == SocketError.NotConnected)))
                        {
                            //Send all the framing.
                            sent += context.RtcpSocket.Send(framing, sent, InterleavedOverhead - sent, System.Net.Sockets.SocketFlags.None, out error);
                        }

                        sent = 0;
                    }
                    else error = SocketError.Success;

                    int packetLength;

                    //if the framing was delivered then send the packet
                    if (error == SocketError.Success) foreach (RtcpPacket packet in packets)
                        {
                            //cache the length
                            packetLength = packet.Length;

                            //While there is data to send
                            while (sent < packetLength &&
                                false.Equals(error == SocketError.ConnectionAborted) &&
                                false.Equals(error == SocketError.ConnectionReset))
                            {
                                //Send it.
                                sent += context.RtcpSocket.Send(packet.Header.First16Bits.m_Memory.Array,
                                    packet.Header.First16Bits.m_Memory.Offset + sent, packetLength - sent,
                                    System.Net.Sockets.SocketFlags.None, out error);
                            }

                            //Reset offset.
                            sent = 0;
                        }

                    //Set sent to how many bytes were sent.
                    sent = length + InterleavedOverhead;
                }
            }

            //If the compound bytes were completely sent then all packets have been sent
            if (error == SocketError.Success)
            {
                //Check to see if each packet which was sent
                int csent = 0;

                //Iterate each managed packet to determine if it was completely sent.
                foreach (RtcpPacket packet in packets)
                {
                    //Handle null or disposed packets.
                    if (IDisposedExtensions.IsNullOrDisposed(packet)) continue;

                    //Increment for the length of the packet
                    csent += packet.Length;

                    //If more data was contained then sent don't set Transferred and raise and event
                    if (csent > sent)
                    {
                        ++context.m_FailedRtcpTransmissions;

                        break;
                    }

                    //set sent
                    packet.Transferred = System.DateTime.UtcNow;

                    //Raise en event
                    HandleOutgoingRtcpPacket(this, packet, context);
                }
            }

            return sent;
        }

        public /*virtual*/ int SendRtcpPackets(IEnumerable<RtcpPacket> packets)
        {
            if (object.ReferenceEquals(packets, null)) return 0;

            System.Net.Sockets.SocketError error;

            TransportContext context = GetContextForPacket(System.Linq.Enumerable.FirstOrDefault(packets));

            return SendRtcpPackets(packets, context, out error);
        }

        internal /*virtual*/ bool SendReports(TransportContext context, bool force = false)
        {
            System.Net.Sockets.SocketError error;

            return SendReports(context, out error, force);
        }

        //Todo, remove virtuals or not.

        /// <summary>
        /// Sends any <see cref="RtcpReport"/>'s immediately for the given <see cref="TransportContext"/> if <see cref="AverageRtcpBandwidthExceeded"/> is false.
        /// </summary>
        /// <param name="context">The <see cref="TransportContext"/> to send a report for</param>
        /// <param name="error"></param>
        /// <param name="force"></param>
        /// <returns>A value indicating if reports were sent</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        internal /*virtual*/ bool SendReports(TransportContext context, out System.Net.Sockets.SocketError error, bool force = false)
        {
            //Ensure set
            error = SocketError.SocketError;

            //Check for the stop signal (or disposal)
            if (false.Equals(force) && m_StopRequested || IsDisposed ||  //Otherwise
                false.Equals(context.IsRtcpEnabled)
                || //Or Rtcp Bandwidth for this context or RtpClient has been exceeded
                context.RtcpBandwidthExceeded || AverageRtcpBandwidthExceeded
                || false.Equals(context.Goodbye == null)) return false; //No reports can be sent.


            //If forced or the last reports were sent in less time than alloted by the m_SendInterval
            //Indicate if reports were sent in this interval
            return force || context.LastRtcpReportSent == System.TimeSpan.MinValue || context.LastRtcpReportSent > context.m_SendInterval ?
                 SendRtcpPackets(PrepareReports(context, true, true), context, out error) > 0
                 :
                 false;
        }

        /// <summary>
        /// Sends a RtcpGoodbye Immediately if the given context:
        /// <see cref="IsRtcpEnabled"/>  and the context has not received a RtcpPacket during the last <see cref="ReceiveInterval"/>.
        /// OR
        /// <see cref="IsRtpEnabled"/> and the context <see cref="IsContinious"/> but <see cref="Uptime"/> is > the <see cref="MediaEndTime"/>
        /// </summary>
        /// <param name="lastActivity">The time the lastActivity has occured on the context (sending or recieving)</param>
        /// <param name="context">The context to check against</param>
        /// <returns>True if the connection is inactive and a Goodebye was attempted to be sent to the remote party</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal /*virtual*/ bool SendGoodbyeIfInactive(System.DateTime lastActivity, TransportContext context)
        {
            bool inactive = false;

            if (IsDisposed
                ||
                m_StopRequested
                ||
                false.Equals(RtcpEnabled)
                ||
                context.HasRecentRtpActivity
                ||
                context.HasRecentRtcpActivity
                || //If the context has a continous flow OR the general Uptime is less then context MediaEndTime
                (false.Equals(context.IsContinious) && Uptime < context.MediaEndTime))
            {
                return false;
            }

            //Calulcate for the currently inactive time period
            if (object.ReferenceEquals(context.Goodbye, null) &&
                false.Equals(context.HasAnyRecentActivity))
            {
                //Set the amount of time inactive
                context.m_InactiveTime = System.DateTime.UtcNow - lastActivity;

                //Determine if the context is not inactive too long
                //6.3.5 Timing Out an SSRC
                //I use the recieve interval + the send interval
                //It should be standarly 2 * recieve interval
                if (context.m_InactiveTime >= context.m_ReceiveInterval + context.m_SendInterval)
                {
                    //send a goodbye
                    SendGoodbye(context, null, context.SynchronizationSourceIdentifier);

                    //mark inactive
                    inactive = true;

                    //Disable further service
                    //context.IsRtpEnabled = context.IsRtcpEnabled = false;
                }
                else if (context.m_InactiveTime >= context.m_ReceiveInterval + context.m_SendInterval)
                {
                    //send a goodbye but don't store it
                    inactive = SendGoodbye(context) <= 0;
                }
            }

            //indicate a goodbye was sent and a context is now inactive.
            return inactive;
        }

        #endregion

        #region Rtp

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TransportContext GetContextForMediaDescription(Sdp.MediaDescription mediaDescription)
        {
            if (IDisposedExtensions.IsNullOrDisposed(mediaDescription)) return null;

            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (c.MediaDescription.MediaType == mediaDescription.MediaType &&
                    c.MediaDescription.MediaFormat.Equals(mediaDescription.MediaFormat, System.StringComparison.InvariantCultureIgnoreCase)
                    ||
                    object.ReferenceEquals(c.MediaDescription.ControlLine, null).Equals(false) &&
                    c.MediaDescription.ControlLine.Equals(mediaDescription.ControlLine)) break;

                c = null;
            }

            return c;

        }

        /// <summary>
        /// Selects a TransportContext for a RtpPacket by matching the packet's PayloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextForPacket(RtpPacket packet)
        {
            if (IDisposedExtensions.IsNullOrDisposed(packet)) return null;

            return GetContextBySourceId(packet.SynchronizationSourceIdentifier) ?? GetContextByPayloadType(packet.PayloadType);

            //COuld improve by checking both at the same time
            //return TransportContexts.FirstOrDefault( c=> false == IDisposedExtensions.IsNullOrDisposed(c) && c.SynchronizationSourceIdentifier == 
        }

        /// <summary>
        /// Selects a TransportContext for a RtpPacket by matching the packet's PayloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextForFrame(RtpFrame frame)
        {
            if (IDisposedExtensions.IsNullOrDisposed(frame)) return null;

            return TransportContexts.Count == 0 ? null : GetContextBySourceId(frame.SynchronizationSourceIdentifier) ?? GetContextByPayloadType(frame.PayloadType);
        }

        /// <summary>
        /// Selects a TransportContext by matching the given payloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextByPayloadType(int payloadType)
        {
            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (false.Equals(IDisposedExtensions.IsNullOrDisposed(c)) &&
                    false.Equals(IDisposedExtensions.IsNullOrDisposed(c.MediaDescription)) &&
                    System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, payloadType)) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by matching the given socket handle to the TransportContext socket's handle
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextBySocketHandle(System.IntPtr socketHandle)
        {
            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (false.Equals(IDisposedExtensions.IsNullOrDisposed(c)) && c.IsActive && object.ReferenceEquals(c.RtpSocket, null).Equals(false) && c.RtpSocket.Handle == socketHandle || object.ReferenceEquals(c.RtcpSocket, null).Equals(false) && c.RtcpSocket.Handle == socketHandle) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by matching the given socket handle to the TransportContext socket's handle
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextBySocket(System.Net.Sockets.Socket socket)
        {
            return socket == null ? null : GetContextBySocketHandle(socket.Handle);
        }

        /// <summary>
        /// Adds a packet to the queue of outgoing RtpPackets
        /// </summary>
        /// <param name="packet">The packet to enqueue</param> (used to take the RtpCLient too but we can just check the packet payload type
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ void EnquePacket(RtpPacket packet)
        {
            if (IsDisposed || m_StopRequested || IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //Add a the packet to the outgoing
            m_OutgoingRtpPackets.Add(packet);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual */ void EnqueFrame(RtpFrame frame)
        {
            if (IsDisposed || m_StopRequested || IDisposedExtensions.IsNullOrDisposed(frame)) return;

            for (int i = 0, e = frame.Count; i < e; ++i)
            {
                EnquePacket(frame[i]);
            }

            foreach (RtpPacket packet in frame) EnquePacket(packet);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void SendRtpFrame(RtpFrame frame, out System.Net.Sockets.SocketError error, int? ssrc = null)
        {
            error = SocketError.SocketError;

            if (m_StopRequested || IDisposedExtensions.IsNullOrDisposed(frame)) return;

            TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextByPayloadType(frame.PayloadType);

            RtpPacket p;

            for (int i = 0, e = frame.Count; i < e; ++i)
            {
                p = frame[i];

                SendRtpPacket(p, transportContext, out error, ssrc);

                if (false.Equals(error == SocketError.Success)) break;
            }

            //p = null;
        }

        public void SendRtpFrame(RtpFrame frame, int? ssrc = null)
        {
            System.Net.Sockets.SocketError error;

            SendRtpFrame(frame, out error, ssrc);
        }

        /// <summary>
        /// Sends a RtpPacket to the connected client.
        /// </summary>
        /// <param name="packet">The RtpPacket to send</param>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public int SendRtpPacket(RtpPacket packet, TransportContext transportContext, out System.Net.Sockets.SocketError error, int? ssrc = null) //Should be compatible with the Prepare signature.
        {
            error = SocketError.SocketError;

            if (m_StopRequested || IDisposedExtensions.IsNullOrDisposed(packet)) return 0;

            //Context could already be known, ssrc may have value.

            //TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            if (transportContext == null) transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            //If we don't have an transportContext to send on or the transportContext has not been identified
            if (IDisposedExtensions.IsNullOrDisposed(transportContext) || false.Equals(transportContext.IsActive)) return 0;

            //Ensure not sending too large of a packet
            if (packet.Length > transportContext.MaximumPacketSize) TaggedExceptionExtensions.RaiseTaggedException(transportContext, "See Tag. The given packet must be smaller than the value in the transportContext.MaximumPacketSize.");

            //How many bytes were sent
            int sent = 0;

            int length = packet.Length;

            #region Unused [Sends a SendersReport if one was not already]

            //Send a SendersReport before any data is sent.
            //if (transportContext.SendersReport == null && transportContext.IsRtcpEnabled) SendSendersReport(transportContext);

            #endregion

            //Keep track if we have to dispose the packet.
            bool dispose = false;

            if (m_IListSockets)
            {
                IList<ArraySegment<byte>> buffers;

                if (ssrc.HasValue && false.Equals(ssrc.Value.Equals(packet.SynchronizationSourceIdentifier)))
                {
                    //Temporarily make a new packet with the same data and new header with the correct ssrc.
                    packet = new RtpPacket(new RtpHeader(packet.Version, packet.Padding, packet.Extension, packet.Marker, packet.PayloadType, packet.ContributingSourceCount, ssrc.Value, packet.SequenceNumber, packet.Timestamp), new MemorySegment(packet.Payload));

                    //mark to dispose the packet instance
                    dispose = true;
                }

                //If we can get the buffer from the packet
                if (packet.TryGetBuffers(out buffers))
                {
                    //If Tcp
                    if ((int)transportContext.RtpSocket.ProtocolType == (int)System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, transportContext.DataChannel, 0, 0 };

                        //Write the length
                        Binary.Write16(framing, 2, Binary.IsLittleEndian, (short)length);

                        //Add the framing
                        buffers.Insert(0, new System.ArraySegment<byte>(framing));
                    }

                    //Send that data.
                    sent += transportContext.RtpSocket.Send(buffers, System.Net.Sockets.SocketFlags.None, out error);
                }
                else
                {
                    //If the transportContext is changed to automatically update the timestamp by frequency then use transportContext.RtpTimestamp
                    sent += SendData(System.Linq.Enumerable.ToArray(packet.Prepare(null, ssrc, null, null)), transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error, (int)TimeSpanExtensions.TotalMicroseconds(transportContext.m_SendInterval) >> 2);
                }
            }
            else
            {
                //If the ssrc does not have value and the packet is contigious then it can be sent in place.

                //Check if the packet cannot be sent in place
                if (ssrc.HasValue && false.Equals(ssrc.Equals(packet.SynchronizationSourceIdentifier))
                    ||
                    false.Equals(packet.IsContiguous()))
                {

                    //If the transportContext is changed to automatically update the timestamp by frequency then use transportContext.RtpTimestamp
                    sent += SendData(System.Linq.Enumerable.ToArray(packet.Prepare(null, ssrc, null, null)),
                        transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error,
                        (int)TimeSpanExtensions.TotalMicroseconds(transportContext.m_SendInterval) >> 2);
                }
                else
                {
                    //Send the data in place.
                    sent += SendData(packet.Header.First16Bits.m_Memory.Array, packet.Header.First16Bits.m_Memory.Offset, packet.Length,
                        transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error,
                        (int)TimeSpanExtensions.TotalMicroseconds(transportContext.m_SendInterval) >> 2);
                }
            }

            if (error == SocketError.Success && sent >= length)
            {
                packet.Transferred = System.DateTime.UtcNow;

                //Handle the packet outgoing.
                HandleOutgoingRtpPacket(this, packet, transportContext);
            }
            else
            {
                ++transportContext.m_FailedRtpTransmissions;
            }

            if (dispose) packet.Dispose();

            return sent;
        }

        //virtual?

        public int SendRtpPacket(RtpPacket packet, int? ssrc = null)
        {
            System.Net.Sockets.SocketError error;

            TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            return SendRtpPacket(packet, transportContext, out error, ssrc);
        }

        public int SendRtpPacket(RtpPacket packet, TransportContext context)
        {
            System.Net.Sockets.SocketError error;

            return SendRtpPacket(packet, context, out error);
        }

        #endregion

        /// <summary>
        /// Creates and starts a worker thread which will send and receive data as required.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void Activate()
        {
            try
            {
                //If the worker thread is already active then return
                if (m_StopRequested.Equals(false) && IsActive) return;

                //Create the worker thread
                m_WorkerThread = new System.Threading.Thread(new System.Threading.ThreadStart(SendReceieve), ThreadExtensions.MinimumStackSize);

                //Configure
                ConfigureThread(m_WorkerThread); //name and ILogging

                m_WorkerThread.Name = "RtpClient-" + InternalId;

                //Reset stop signal
                m_StopRequested = false;

                //Start highest.
                m_WorkerThread.Priority = System.Threading.ThreadPriority.Highest;

                //Start thread
                m_WorkerThread.Start();

                //Wait for thread to actually start
                while (false.Equals(IsActive)) m_EventReady.Wait(TimeSpanExtensions.OneTick);
                //Could also use the Join but would have to add logic in the thread to handle this.
                //m_WorkerThread.Join(TimeSpanExtensions.OneTick);

                #region Unused Feature [Early Rtcp]

                //Should allow to be overridden by option or otherwise, should not be required.

                //Send the initial senders report, needs to check the SessionDescription to determine if sending is supported..
                //SendSendersReports();

                //Send the initial receivers report, needs to check the SessionDescription to see if recieve is supported...
                //SendReceiversReports();

                #endregion
            }
            catch (System.ObjectDisposedException) { return; }
            catch (System.Exception ex)
            {
                ILoggingExtensions.LogException(Logger, ex);

                throw;
            }
        }

        /// <summary>
        /// Sends the Rtcp Goodbye and signals a stop in the worker thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deactivate()
        {
            if (IsDisposed || false == IsActive) return;

            SendGoodbyes();

            m_StopRequested = true;

            foreach (TransportContext tc in TransportContexts) if (tc.IsActive) tc.DisconnectSockets();

            ThreadExtensions.TryAbortAndFree(ref m_WorkerThread);

            Started = System.DateTime.MinValue;
        }

        public void DisposeAndClearTransportContexts()
        {
            //Dispose contexts
            foreach (TransportContext tc in TransportContexts) tc.Dispose();

            //Counters go away with the transportChannels
            TransportContexts.Clear();
        }

        /// <summary>
        /// Returns the amount of bytes read to completely read the application layer framed data
        /// Where a negitive return value indicates no more data remains.
        /// </summary>
        /// <param name="received">How much data was received</param>
        /// <param name="frameChannel">The output of reading a frameChannel</param>
        /// <param name="context">The context assoicated with the frameChannel</param>
        /// <param name="offset">The reference to offset to look for framing data</param>
        /// <param name="raisedEvent">Indicates if an event was raised</param>
        /// <param name="buffer">The optional buffer to use.</param>
        /// <returns>The amount of bytes the frame data SHOULD have</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int ReadApplicationLayerFraming(ref int received, ref int sessionRequired, ref int offset, out byte frameChannel, out RtpClient.TransportContext context, out bool raisedEvent, byte[] buffer = null)
        {
            //There is no relevant TransportContext assoicated yet.
            context = null;

            //The channel of the frame - The Framing Method
            frameChannel = default(byte);

            raisedEvent = false;

            buffer = buffer ?? m_Buffer.Array;

            int bufferLength = buffer.Length, bufferOffset = offset;

            received = Binary.Min(received, bufferLength - bufferOffset);

            //Assume given enough for sessionRequired

            //Todo Determine from Context to use control channel and length. (Check MediaDescription)
            //NEEDS TO HANDLE CASES WHERE RFC4571 Framing are in play and no $ or Channel are used....            
            //int sessionRequired = InterleavedOverhead;

            if (received <= 0 || sessionRequired < 0 || received < sessionRequired) return -1;

            //Look for the frame control octet
            int startOfFrame = System.Array.IndexOf<byte>(buffer, BigEndianFrameControl, bufferOffset, received);

            //If not found everything belongs to the upper layer
            if (startOfFrame == -1)
            {
                //System.Diagnostics.Debug.WriteLine("Interleaving: " + received);
                OnOutOfBandData(buffer, bufferOffset, received);

                raisedEvent = true;

                //Indicate the amount of data consumed.
                return received;
            }
            else if (startOfFrame > bufferOffset) // If the start of the frame is not at the beginning of the buffer
            {
                //Determine the amount of data which belongs to the upper layer
                int upperLayerData = startOfFrame - bufferOffset;

                //System.Diagnostics.Debug.WriteLine("Moved To = " + startOfFrame + " Of = " + received + " - Bytes = " + upperLayerData + " = " + Encoding.ASCII.GetString(m_Buffer, mOffset, startOfFrame - mOffset));                

                OnOutOfBandData(buffer, bufferOffset, upperLayerData);

                raisedEvent = true;

                //Indicate length from offset until next possible frame. (should always be positive, if somehow -1 is returned this will signal a end of buffer to callers)

                //If there is more data related to upperLayerData it will be evented in the next run. (See RtspClient ProcessInterleaveData notes)
                return upperLayerData;
            }

            //If there is not enough data for a frame header return
            if (bufferOffset + sessionRequired > bufferLength) return -1;

            //The amount of data needed for the frame comes from TryReadFrameHeader
            int frameLength = TryReadFrameHeader(buffer, bufferOffset, out frameChannel, BigEndianFrameControl, true);

            //Assign a context if there is a frame of any size
            if (frameLength >= 0)
            {
                //Assign the context
                context = GetContextByChannels(frameChannel);

                //Increase the result by the size of the header
                frameLength += sessionRequired;
            }

            //Return the amount of bytes or -1 if any error occured.
            return frameLength;
        }

        /// <summary>
        /// Sends the given data on the socket remote
        /// </summary>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        /// <param name="socket"></param>
        /// <param name="remote"></param>
        /// <param name="error"></param>
        /// <param name="useFrameControl"></param>
        /// <param name="useChannelId"></param>
        /// <returns></returns>
        internal protected /*virtual*/ int SendData(byte[] data, byte? channel, System.Net.Sockets.Socket socket, System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, int pollTime = 0, bool useFrameControl = true, bool useChannelId = true)
        {
            return SendData(data, 0, data.Length, channel, socket, remote, out error, pollTime, useFrameControl, useChannelId);
        }

        /// <summary>
        /// Sends the given data on the socket to remote
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="channel"></param>
        /// <param name="socket"></param>
        /// <param name="remote"></param>
        /// <param name="error"></param>
        /// <param name="useFrameControl"></param>
        /// <param name="useChannelId"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ int SendData(byte[] data, int offset, int length, byte? channel, System.Net.Sockets.Socket socket, System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, int pollTime = 0, bool useFrameControl = true, bool useChannelId = true)
        {
            error = SocketError.SocketError;

            //Check there is valid data and a socket which is able to write and that the RtpClient is not stopping
            if (IsDisposed || socket == null || length == 0 || data == null) return 0;

            int sent = 0;

            //Todo could pass TransportContext also....

            //Just had the context in the previous call in most cases...
            //RtpClient.TransportContext tc = context ?? GetContextBySocket(socket);

            try
            {
                #region Tcp Application Layer Framing

                //Under Tcp we must frame the data for the given channel
                if (socket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp && channel.HasValue)
                {
                    //Create the data from the concatenation of the frame header and the data existing
                    //E.g. Under RTSP...Frame the Data in a PDU {$ C LEN ...}

                    //Could set SendBufferSize now.

                    //int sbs = socket.SendBufferSize;

                    //socket.SendBufferSize = length;

                    if (useChannelId && useFrameControl)
                    {
                        //Data now has an offset and length...
                        //data = Enumerable.Concat(LinqExtensions.Yield(BigEndianFrameControl), LinqExtensions.Yield(channel.Value))
                        //    .Concat(Binary.GetBytes((short)length, Binary.IsLittleEndian))
                        //    .Concat(data).ToArray();

                        //Create the framing
                        byte[] framing = new byte[] { BigEndianFrameControl, channel.Value, 0, 0 };

                        //Write the length
                        Binary.Write16(framing, 2, Binary.IsLittleEndian, (short)length);

                        //See if we can write.
                        if (false == socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite))
                        {
                            //Indicate the operation has timed out
                            error = SocketError.TimedOut;

                            return sent;
                        }

                        //Send the framing
                        sent += SocketExtensions.SendTo(framing, 0, InterleavedOverhead, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                        //After small writes do a read. (make sure we don't get back our own data in collision)

                    }
                    else
                    {
                        //Build the data
                        IEnumerable<byte> framingData;

                        //The length is always present
                        framingData = System.Linq.Enumerable.Concat(Binary.GetBytes((short)length, Binary.IsLittleEndian), data);

                        int framingLength = 2;

                        if (useChannelId)
                        {
                            framingData = System.Linq.Enumerable.Concat(LinqExtensions.Yield(channel.Value), framingData);
                            ++framingLength;
                        }

                        if (useFrameControl)
                        {
                            //data = LinqExtensions.Yield(BigEndianFrameControl).Concat(data).ToArray();
                            framingData = System.Linq.Enumerable.Concat(LinqExtensions.Yield(BigEndianFrameControl), data);
                            ++framingLength;
                        }

                        //Project the framing.
                        byte[] framing = System.Linq.Enumerable.ToArray(framingData);

                        //See if we can write.
                        if (false == socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite))
                        {
                            //Indicate the operation has timed out
                            error = SocketError.TimedOut;

                            return sent;
                        }

                        sent += SocketExtensions.SendTo(framing, 0, framingLength, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                    }

                    //Put back
                    //socket.SendBufferSize = sbs;

                    //Must send framing seperately.
                    //MSS cannot be determined easily without hacks or custom socket layer.
                    //Framing was not included in the bytesPerPacket when packetization was performed.
                    //If framing is missed or dropped the reciever implementation should be using a packet inspection routine similar to the one implemented in this library to demux the packet.
                    //This has reprocussions if this client is a proxy as two different ssrc's may overlap and only have different control channels....

                }
                else length = data.Length;

                #endregion

                //Check for the socket to be writable in the receive interval of the context
                if (socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite).Equals(false))
                {
                    //Indicate the operation has timed out
                    error = SocketError.TimedOut;

                    return sent;
                }

                //Send all the data to the endpoint
                sent += SocketExtensions.SendTo(data, offset, length, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                return sent; //- Overhead for tcp, may not have to include it.
            }
            catch
            {
                //Something bad happened, usually disposed already
                return sent;
            }
        }

        /// <summary>
        /// Recieves data on a given socket and endpoint
        /// </summary>
        /// <param name="socket">The socket to receive data on</param>
        /// <returns>The number of bytes recieved</returns>             
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ int ReceiveData(System.Net.Sockets.Socket socket, ref System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, bool expectRtp = true, bool expectRtcp = true, MemorySegment buffer = null)
        {
            //Nothing bad happened yet.
            error = SocketError.SocketError;

            if (IDisposedExtensions.IsNullOrDisposed(buffer)) buffer = m_Buffer;

            //Ensure the socket can poll, should measure against parallel checks with OR
            if (m_StopRequested || object.ReferenceEquals(socket, null) || object.ReferenceEquals(remote, null) || IDisposedExtensions.IsNullOrDisposed(buffer) || IsDisposed) return 0;

            bool tcp = socket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp;

            //Cache the offset at the time of the call
            int received = 0, justRecieved;

            //int max = buffer.Count;

            //int pmax;

            //if (tcp) SocketExtensions.GetMaximumSegmentSize(socket, out pmax);
            //else pmax = SocketExtensions.GetMaximumTransmittableUnit(socket);

            try
            {
                //Determine how much data is 'Available'
                //int available = socket.ReceiveFrom(m_Buffer.Array, offset, m_Buffer.Count, SocketFlags.Peek, ref remote);

                error = SocketError.Success;

                ////If the receive was a success
                //if (available > 0)
                //{              

                do received += justRecieved = socket.ReceiveFrom(buffer.Array, buffer.Offset + received, buffer.Count - received, System.Net.Sockets.SocketFlags.None, ref remote);
                while (received.Equals(0) /*|| justRecieved > 0 && received + justRecieved < pmax*/ && socket.Connected);

                ////Lookup the context to determine if the packet will fit
                //var context = GetContextBySocket(socket);

                ////If there was a context and packet cannot fit
                //if (context != null && received > context.MaximumPacketSize)
                //{
                //    //Log the problem
                //    ILoggingExtensions.Log(Logger, ToString() + "@ReceiveData - Cannot fit packet in buffer");

                //    //Determine if there was enough data to determine if the packet was rtp or rtcp and indicate a failed reception
                //    //if (received > RFC3550.CommonHeaderBits.Size)
                //    //{
                //    //    //context.m_FailedRtcpReceptions++;

                //    //    //context.m_FailedRtpReceptions++;
                //    //}

                //    //remove the reference
                //    context = null;
                //}


                //Use the data received to parse and complete any recieved packets, should take a parseState
                /*using (var memory = new MemorySegment(buffer.Array, buffer.Offset, received)) */
                //}

            }
            catch (System.Net.Sockets.SocketException se)
            {
                error = (System.Net.Sockets.SocketError)se.ErrorCode;
            }
            catch (System.Exception ex)
            {
                ILoggingExtensions.LogException(Logger, ex);
            }

            //Under TCP use Framing to obtain the length of the packet as well as the context.
            if (received > 0) if (tcp) return ProcessFrameData(buffer.Array, buffer.Offset, received, socket);
                else ParseAndHandleData(buffer, ref expectRtcp, ref expectRtp, ref received, ref received);
            //Return the amount of bytes received from this operation
            return received;
        }

        /// <summary>
        /// Used to handle Tcp framing, this should be put on the TransportContext or it should allow a way for Transport to be handled, right now this is done in OnInterleavedData
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ int ProcessFrameData(byte[] buffer, int offset, int count, System.Net.Sockets.Socket socket)
        {
            if (count <= 0) return Binary.Zero;

            //If there is no buffer use our own buffer.
            if (ArrayExtensions.IsNullOrEmpty(buffer))
            {
                if (IDisposedExtensions.IsNullOrDisposed(m_Buffer)) return 0;

                buffer = m_Buffer.Array;
            }

            //Determine which TransportContext will receive the data incoming
            TransportContext relevent = null;

            //The channel of the data
            byte frameChannel = Binary.Zero;

            //Get the length of the given buffer (Should actually use m_Buffer.Count when using our own buffer)
            int bufferLength = buffer.Length,
                //The indicates length of the data
                frameLength = Binary.Zero,
                //The amount of data remaining in the buffer
                remainingInBuffer = count,
                //The amount of data received (which is already equal to what is remaining in the buffer)
                recievedTotal = remainingInBuffer;

            //Determine if Rtp or Rtcp is coming in or some other type (could be combined with expectRtcp and expectRtp == false)
            bool expectRtp = false, expectRtcp = false, incompatible = true, raisedEvent = false, jumbo = false, hasFrameHeader = false;

            //If anything remains on the socket the value will be calulcated.
            int remainingOnSocket = Binary.Zero, remainingInFrame = remainingOnSocket;

            //TODO handle receiving when no $ and Channel is presenent... e.g. RFC4571
            //Would only be 2 then...

            int sessionRequired = InterleavedOverhead;

            //Todo, we allow a buffer to be given so we must also check if its changed to null...

            //Because it is not passed by 'ref' the changes to the array will not be seen if changed by `this` function, e.g. from the `caller`
            //however changes to the `array` from the `caller` WILL NOT be seen either
            //Functions calls which receive `Array` cannot exchange information this way unless they calls are retain the same `version` as passed from the `caller`

            int registerX, registerY;

            //While not disposed and there is data remaining (within the buffer)
            while (IDisposedExtensions.IsNullOrDisposed(m_Buffer).Equals(false) &&
                remainingInBuffer > Binary.Zero &&
                offset >= m_Buffer.Offset &&
                false.Equals(IsDisposed))
            {
                ContinueParsing:
                //Assume not rtp or rtcp and that the data is compatible with the session
                hasFrameHeader = jumbo = expectRtp = expectRtcp = incompatible = false;

                //If a header can be read
                if (remainingInBuffer >= sessionRequired)
                {
                    //Determine if an event was raised each time there was at least the required amount of data.
                    raisedEvent = false;

                    //Parse the frameLength from the given buffer, take changes to the offset through the function.
                    //should give out the frame header length.
                    frameLength = ReadApplicationLayerFraming(ref remainingInBuffer, ref sessionRequired, ref offset, out frameChannel, out relevent, out raisedEvent, buffer);

                    //If the event was raised then the data belong to another protocol
                    if (raisedEvent)
                    {
                        //Move the offset
                        offset += frameLength;

                        //decrease what remains
                        remainingInBuffer -= frameLength;

                        //Iterate again
                        continue;
                    }

                    //Assign jumbo, If a frame was found (Including the null packet)
                    if (frameLength >= Binary.Zero)
                    {
                        //Determine if a frameHeader was found
                        hasFrameHeader = frameLength > sessionRequired;

                        //Determine how large the packet is
                        jumbo = frameLength > bufferLength;

                        //If there WAS a context
                        if (false.Equals(IDisposedExtensions.IsNullOrDisposed(relevent)))
                        {
                            ////Handle indepent framing, supposedly independent and interleaved are not allowed on the same connection...
                            ////We will see if that holds true and for how long...
                            //if (GetContextBySocket(socket).MediaDescription.MediaProtocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                            //{
                            //    sessionRequired = 2;
                            //}

                            #region Verify FrameLength

                            //Verify minimum and maximum packet sizes allowed by context. (taking into account the amount of bytes in the ALF)
                            if (frameLength < relevent.MinimumPacketSize + sessionRequired ||
                                frameLength > relevent.MaximumPacketSize + sessionRequired)
                            {
                                //mark as incompatible
                                //incompatible = true;

                                ILoggingExtensions.Log(Logger, ToString() + "@ProcessFrameData - Buffer Exceeded Packet of " + frameLength + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                //BufferExceeded => alow for resize.

                            }

                            //TODO Independent framing... (e.g. no $)[ only 4 bytes not 6 ]
                            //If all that remains is the frame header then receive more data. 6 comes from (InterleavedOverhead + CommonHeaderBits.Size)
                            //We need more data to be able to verify the frame.
                            if (remainingInBuffer <= sessionRequired + RFC3550.CommonHeaderBits.Size)
                            {
                                //Remove the context
                                relevent = null;

                                ILoggingExtensions.Log(Logger, ToString() + "@ProcessFrameData - (" + remainingInBuffer + ")" + ", Needs more data to inspect packet fields, frameLength = " + frameLength + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                goto CheckRemainingData;

                                ////Only receive this many more bytes for now.
                                //remainingOnSocket = X - remainingInBuffer;

                                ////Receive the rest of the data indicated by frameLength. (Should probably only receive up to X more bytes then make another receive if needed)
                                //goto GetRemainingData;
                            }

                            #endregion

                            #region Verify Packet Headers

                            //Use CommonHeaderBits on the data after the Interleaved Frame Header
                            using (RFC3550.CommonHeaderBits common = new RFC3550.CommonHeaderBits(buffer, offset + sessionRequired))
                            {
                                //Check the version...
                                incompatible = false.Equals(common.Version.Equals(relevent.Version));

                                //If this is a valid context there must be at least a RtpHeader's worth of data in the buffer. 
                                //If this was a RtcpPacket with only 4 bytes it wouldn't have a ssrc and wouldn't be valid to be sent.
                                if (false.Equals(incompatible) &&
                                    (frameChannel.Equals(relevent.DataChannel) &&
                                    remainingInBuffer < Rtp.RtpHeader.Length + sessionRequired)
                                    ||
                                    (frameChannel.Equals(relevent.ControlChannel) &&
                                    remainingInBuffer < Rtcp.RtcpHeader.Length + sessionRequired))
                                {
                                    //Remove the context
                                    relevent = null;

                                    //Mark as incompatible
                                    incompatible = true;

                                    goto EndUsingHeader;

                                    ////Only receive this many more bytes for now.
                                    //remainingOnSocket = 16 - remainingInBuffer;

                                    ////Receive the rest of the data indicated by frameLength. (Should probably only receive up to 6 more bytes then make another receive if needed)
                                    //goto GetRemainingData;
                                }


                                //Perform a set of checks and set weather or not Rtp or Rtcp was expected.                                  
                                if (false.Equals(incompatible))
                                {
                                    //Determine if the packet is Rtcp by looking at the found channel and the relvent control channel
                                    if (frameChannel.Equals(relevent.ControlChannel) && false.Equals(relevent.InDiscovery))
                                    {
                                        //Rtcp

                                        if (remainingInBuffer <= sessionRequired + Rtcp.RtcpHeader.Length)
                                        {
                                            //Remove the context
                                            relevent = null;

                                            goto CheckRemainingData;
                                        }

                                        //use a rtcp header to extract the information in the packet
                                        using (Rtcp.RtcpHeader header = new Rtcp.RtcpHeader(buffer, offset + sessionRequired))
                                        {
                                            //Get the length in 'words' (by adding one)
                                            //A length of 0 means 1 word
                                            //A length of 65535 means only the header (no ssrc [or payload])
                                            ushort lengthInWordsPlusOne = (ushort)(header.LengthInWordsMinusOne + 1);

                                            //Store any rtcp length so we can verify its not 0 and then additionally ensure its value is not larger then the frameLength
                                            //Convert to bytes
                                            int rtcpLen = lengthInWordsPlusOne * 4;

                                            //Check that the supposed  amount of contained words is greater than or equal to the frame length conveyed by the application layer framing
                                            //it must also be larger than the buffer
                                            incompatible = rtcpLen >= frameLength || rtcpLen >= bufferLength;

                                            //if rtcpLen >= ushort.MaxValue the packet may possibly span multiple segments unless a large buffer is used.

                                            if (false.Equals(incompatible) && //It was not already ruled incomaptible
                                                lengthInWordsPlusOne > 0 && //If there is supposed to be SSRC in the packet
                                                header.Size > Rtcp.RtcpHeader.Length && //The header ACTUALLY contains enough bytes to have a SSRC
                                                false.Equals(relevent.InDiscovery))//The remote context knowns the identity of the remote stream                                                 
                                            {
                                                //Determine if Rtcp is expected
                                                //Perform another lookup and check compatibility
                                                expectRtcp = (incompatible = IDisposedExtensions.IsNullOrDisposed(GetContextBySourceId(header.SendersSynchronizationSourceIdentifier))) ? false : true;
                                            }
                                        }
                                    }

                                    //May be mixing channels...
                                    if (false.Equals(expectRtcp) && false.Equals(relevent.InDiscovery))
                                    {
                                        //Rtp
                                        if (remainingInBuffer <= sessionRequired + Rtp.RtpHeader.Length)
                                        {
                                            //Remove the context
                                            relevent = null;

                                            goto CheckRemainingData;
                                        }

                                        //the context by payload type is null is not discovering the identity check the SSRC.
                                        if (false.Equals(IDisposedExtensions.IsNullOrDisposed(GetContextByPayloadType(common.RtpPayloadType))) /*&& false.Equals(relevent.InDiscovery)*/)
                                        {
                                            using (Rtp.RtpHeader header = new RtpHeader(buffer, offset + sessionRequired))
                                            {
                                                //The context was obtained by the frameChannel
                                                //Use the SSRC to determine where it should be handled.
                                                //If there is no context the packet is incompatible
                                                expectRtp = (incompatible = IDisposedExtensions.IsNullOrDisposed(GetContextBySourceId(header.SynchronizationSourceIdentifier))) ? false : true;

                                                //(Could also check SequenceNumber to prevent duplicate packets from being processed.)

                                                ////Verify extensions (handled by ValidatePacket)
                                                //if (header.Extension)
                                                //{

                                                //}

                                            }
                                        }
                                        else incompatible = false;
                                    }
                                }
                                EndUsingHeader:
                                ;
                            }

                            #endregion
                        }

                        //If the frameLength is larger than the buffer all the data cannot fit
                        if (jumbo)
                        {
                            //If rtp or rtcp is expected check data
                            if (expectRtp || expectRtcp || frameChannel < TransportContexts.Count)
                            {
                                ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - Large Packet of " + frameLength + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                //Could allow for the buffer to be replaced here for the remainder of this call only.

                                goto CheckRemainingData;
                            }
                        }
                        else goto CheckRemainingData;

                        //The packet was incompatible or larger than the buffer

                        //Determine how much we can move
                        registerX = frameLength > remainingInBuffer ? Binary.Min(ref remainingInBuffer, ref sessionRequired) : frameLength;

                        //ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData Moving = " + toMove +", frameLength=" + frameLength + ", remainingInBuffer = " + remainingInBuffer);

                        //TODO It may be possible to let the event reciever known how much is available here.

                        //Indicate what was received if not already done
                        if (false.Equals(raisedEvent)) OnOutOfBandData(buffer, offset, registerX);

                        //Move the offset
                        offset += registerX;

                        //Decrease by the length
                        remainingInBuffer -= registerX;

                        //Do another pass
                        continue;

                    }//else there was a frameLength of -1 this indicates there is not enough bytes for a header.
                }
                else//There is not enough data in the buffer as defined by sessionRequired.
                {
                    //unset the frameLength read
                    frameLength = -1;

                    //unset the context read
                    relevent = null;
                }

                //At this point there may be either less sessionRequired or not enough for a complete frame.
                CheckRemainingData:

                //See how many more bytes are required from the wire
                //If the frameLength is less than 0 AND there are less then or equal to sessionRequired remaining in the buffer
                remainingOnSocket = frameLength < 0 && remainingInBuffer <= sessionRequired ?
                    bufferLength - remainingInBuffer //Receive enough to complete the header or see another packet, whatever was ack'd will be available in the buffer.
                        : //Otherwise if the frameLength larger then what remains in the buffer allow for the buffer to be filled or nothing else remains.
                    frameLength > remainingInBuffer ? frameLength - remainingInBuffer : 0;

                //If there is anymore data remaining on the wire
                if (remainingOnSocket > 0 && false.Equals(object.ReferenceEquals(socket, null)) && false.Equals(IsDisposed))
                {
                    //Align the buffer if anything remains on the socket.
                    if (remainingOnSocket + offset + remainingInBuffer > bufferLength)
                    {
                        System.Array.Copy(buffer, offset, buffer, m_Buffer.Offset, remainingInBuffer);

                        //Set the correct offset either way.
                        offset = m_Buffer.Offset + remainingInBuffer;
                    }
                    else
                    {
                        offset += remainingInBuffer;
                    }

                    //Store the error if any
                    System.Net.Sockets.SocketError error = SocketError.Success;

                    //Get all the remaining data, todo, if not active must activate and join thread to hand off context.
                    while (remainingOnSocket > 0 && false.Equals(IsDisposed))
                    {
                        registerY = SocketExtensions.AlignedReceive(buffer, offset, remainingOnSocket, socket, out error);

                        //Handle any error
                        switch (error)
                        {
                            case SocketError.WouldBlock:
                            case SocketError.SystemNotReady:
                            case SocketError.TooManyOpenSockets:
                            case SocketError.TryAgain:
                            case SocketError.TimedOut:
                                ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - (" + error + ") remainingOnSocket " + remainingOnSocket + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                if (registerY > 0) break;

                                continue;
                            case SocketError.Success:
                                break;
                            case SocketError.Shutdown:
                            //If a socket error occured remove the context so no parsing occurs
                            default:
                                //OnTruncatedData
                                OnOutOfBandData(buffer, offset - remainingInBuffer, remainingInBuffer);

                                return recievedTotal;
                        }

                        //Decrease what is remaining from the wire by what was received
                        remainingOnSocket -= registerY;

                        //Move the offset
                        offset += registerY;

                        //Increment received
                        recievedTotal += registerY;

                        //Incrment remaining in buffer for what was recieved.
                        remainingInBuffer += registerY;
                    }

                    //Move back to where the frame started
                    offset -= remainingInBuffer;

                    //Go to the top of the loop to verify the data again.
                    if (jumbo.Equals(false) && IDisposedExtensions.IsNullOrDisposed(relevent).Equals(false)) goto ContinueParsing;
                }

                //If the client is not disposed
                if (false.Equals(IsDisposed))
                {
                    //Calulcate how much remains
                    //remainingInFrame = jumbo ? frameLength - remainingInBuffer : frameLength;                    

                    //Todo, don't waste allocations on 0
                    //Parse the data in the buffer
                    using (MemorySegment memory = hasFrameHeader ? new MemorySegment(buffer, offset + sessionRequired, Binary.Min(ref frameLength, ref remainingInBuffer) - sessionRequired) : new MemorySegment(buffer, offset, remainingInBuffer))
                    {
                        registerX = memory.Count;

                        //Don't use 0 as flow control here. Raising what potentially be multiple events would be dumb.
                        if (registerX.Equals(0))
                        {
                            offset += sessionRequired;

                            remainingInBuffer -= sessionRequired;

                            continue;
                        }

                        //If there is a frame header than handle the data otherwise process as out of band.
                        if (hasFrameHeader) ParseAndHandleData(memory, ref expectRtcp, ref expectRtp, ref registerX, ref remainingInFrame);
                        else OnOutOfBandData(memory.Array, memory.Offset, memory.Count);

                        //Decrease remaining in buffer
                        remainingInBuffer -= registerX;

                        //Move the offset
                        offset += registerX;

                        //Ensure large frames are completely received by receiving the rest of the frame now.
                        if (jumbo /*frameLength > bufferLength*/)
                        {
                            //Remove the context
                            relevent = null;

                            //No more header...
                            hasFrameHeader = false;

                            //Determine how much remains
                            remainingOnSocket = frameLength - registerX;

                            //If there is anything left
                            if (remainingOnSocket > 0 /*&& expectRtcp || expectRtp*/)
                            {
                                //Set the new length of the frame based on the length of the buffer
                                remainingInFrame = frameLength -= registerX;

                                //Set what is remaining
                                remainingInBuffer = 0;

                                //Use all the buffer
                                offset = m_Buffer.Offset;

                                //still to big?
                                jumbo = remainingInFrame > bufferLength;

                                //go to receive it
                                goto CheckRemainingData;
                            }
                        }
                    }
                }
            }

            //Handle any data which remains if not already
            if (false.Equals(raisedEvent) && offset >= 0 && remainingInBuffer > 0)
            {
                OnOutOfBandData(buffer, offset, remainingInBuffer);
            }

            //Return the number of bytes recieved
            return recievedTotal;
        }


        /// <summary>
        /// Parses the data in the buffer for valid Rtcp and Rtcp packet instances.
        /// </summary>
        /// <param name="memory">The memory to parse</param>
        /// <param name="from">The socket which received the data into memory and may be used for packet completion.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected /*virtual*/ void ParseAndHandleData(MemorySegment memory, ref bool parseRtcp, ref bool parseRtp, ref int remaining, ref int expected)
        {

            if (IDisposedExtensions.IsNullOrDisposed(memory) || memory.Count.Equals(0) || remaining <= 0) return;

            //handle demultiplex scenarios e.g. RFC5761
            if (parseRtcp.Equals(parseRtp) && memory.Count > RFC3550.CommonHeaderBits.Size)
            {
                //Double Negitive, Demux based on PayloadType? RFC5761?

                //Distinguishable RTP and RTCP Packets
                //http://tools.ietf.org/search/rfc5761#section-4

                //Observation 1) Rtp packets can only have a PayloadType from 64-95
                //However Rtcp Packets may also use PayloadTypes 72- 76.. (Reduced size...)

                //Observation 2) Rtcp Packets defined in RFC3550 Start at 200 (SR -> Goodbye) 204,
                // 209 - 223 is cited in the above as well as below
                //RTCP packet types in the ranges 1-191 and 224-254 SHOULD only be used when other values have been exhausted.

                using (RFC3550.CommonHeaderBits header = new RFC3550.CommonHeaderBits(memory))
                {
                    //Could ensure version here to make reception more unified.

                    //Just use the payload type to avoid confusion, payload types for Rtcp and Rtp cannot and should not overlap
                    parseRtcp = false.Equals(parseRtp = false.Equals(IDisposedExtensions.IsNullOrDisposed(GetContextByPayloadType(header.RtpPayloadType))));

                    //Could also lookup the ssrc
                }
            }

            //If the packet was truncated then it may be necessary to remove atleast the 'Padding' bit if it was set.

            //if(expected > remaining){...//OnTrunatedPacket(memory, bool rtp, bool rtcp, expected)}

            //Cache start, count and index
            int offset = memory.Offset, count = memory.Count, index = 0,
            //Calulcate remaining, take whatever is less
            mRemaining = +remaining;

            //If rtcp should be parsed
            if (parseRtcp && mRemaining >= Rtcp.RtcpHeader.Length)
            {
                //Iterate the packets within the buffer, calling Dispose on each packet
                foreach (RtcpPacket rtcp in Rtcp.RtcpPacket.GetPackets(memory.Array, offset + index, mRemaining))
                {
                    //Handle the packet further (could indicate truncated here)
                    HandleIncomingRtcpPacket(this, rtcp);

                    //Move the offset the length of the packet parsed
                    index += rtcp.Length;

                    mRemaining -= rtcp.Length;
                }

            }

            //If rtp is parsed
            if (parseRtp && mRemaining >= RtpHeader.Length)
            {
                //Use the packet to call Dispose.
                using (RtpPacket rtp = new RtpPacket(memory.Array, offset + index, mRemaining))
                {
                    //Handle the packet further  (could indicate truncated here)
                    HandleIncomingRtpPacket(this, rtp);

                    //Move the index past the length of the packet
                    index += rtp.Length;

                    //Calculate the amount of octets remaining in the segment.
                    mRemaining -= rtp.Length;
                }
            }

            //If not all data was consumed
            if (mRemaining > 0)
            {
                ILoggingExtensions.Log(Logger, ToString() + "@ParseAndCompleteData - Remaining= " + mRemaining);

                OnOutOfBandData(memory.Array, offset + index, mRemaining);
            }

            return;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void HandleEvent()
        {
            //Dequeue the event frame
            System.Tuple<RtpClient.TransportContext, BaseDisposable, bool, bool> tuple;

            //handle the event frame
            if (m_EventData.TryDequeue(out tuple))
            {
                //If the item was already disposed then do nothing
                if (IDisposedExtensions.IsNullOrDisposed(tuple.Item2)) return;

                //using(tuple.Item2)

                //handle for recieved frames
                //todo, length may be more valuable than bool, - means in, positive is out
                if (tuple.Item4 && tuple.Item2 is RtpFrame)
                {
                    ParallelRtpFrameChanged(tuple.Item2 as RtpFrame, tuple.Item1, tuple.Item3);
                }
                else
                {
                    //Determine what type of packet
                    IPacket what = tuple.Item2 as IPacket;

                    //handle the packet event
                    if (what is RtpPacket)
                    {
                        if (tuple.Item4) ParallelRtpPacketRecieved(what as RtpPacket, tuple.Item1);
                        else ParallelRtpPacketSent(what as RtpPacket, tuple.Item1);
                    }
                    else if (what is RtcpPacket)
                    {
                        if (tuple.Item4) ParallelRtcpPacketRecieved(what as RtcpPacket, tuple.Item1);
                        else ParallelRtcpPacketSent(what as RtcpPacket, tuple.Item1);
                    }
                    else ParallelOutOfBandData(what as PacketBase);

                    //Free whatever was used now that the event is handled.
                    //if(false == tuple.Item2.ShouldDispose) BaseDisposable.SetShouldDispose(tuple.Item2, true, true);
                }
            }
        }

        /// <summary>
        /// Entry point of the m_EventThread. Handles dispatching events
        /// </summary>
        void HandleEvents()
        {
            EventsStarted = System.DateTime.UtcNow;

            unchecked
            {
                Begin:
                try
                {
                    //While the main thread is active.
                    while (m_ThreadEvents)
                    {
                        //If the event is not set
                        if (false.Equals(m_EventReady.IsSet))
                        {
                            //Wait for the event signal half of the amount of time
                            if (false.Equals(m_EventReady.Wait(TimeSpanExtensions.OneTick)))
                            {
                                //Todo, ThreadInfo.

                                //Check if not already below normal priority
                                if (false.Equals(System.Threading.Thread.CurrentThread.Priority == System.Threading.ThreadPriority.Lowest))
                                {
                                    //Relinquish priority
                                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest;
                                }
                            }
                        }

                        //Reset the event when all frames are dispatched
                        if (m_EventData.IsEmpty)
                        {
                            m_EventReady.Reset();

                            m_EventReady.Wait();
                        }
                        else if (false.Equals(IsActive)) break;

                        //Set priority
                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;

                        //handle the event in waiting.
                        HandleEvent();
                    }
                }
                catch (System.Threading.ThreadAbortException) { System.Threading.Thread.ResetAbort(); ILoggingExtensions.Log(Logger, ToString() + "@HandleEvents Aborted"); }
                catch (System.Exception ex) { ILoggingExtensions.Log(Logger, ToString() + "@HandleEvents: " + ex.Message); goto Begin; }
            }
        }

        internal int m_SignalOffset = -1, m_SignalCount = -1;

        internal System.Net.Sockets.Socket m_SignalSocket;

        bool DoSignalWork(/*ref out*/)
        {
            //Todo, HandOff
            while (m_SignalOffset > 0 && IsDisposed.Equals(false))
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

                int recv = ProcessFrameData(null, m_SignalOffset, m_SignalCount, m_SignalSocket);

                m_SignalOffset += recv;

                m_SignalOffset -= recv;

                if (m_SignalCount <= 0)
                {
                    m_SignalOffset = m_SignalCount = -1;

                    m_SignalSocket = null;

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Entry point of the m_WorkerThread. Handles sending out RtpPackets and RtcpPackets in buffer and handling any incoming RtcpPackets.
        /// Sends a Goodbye and exits if no packets are sent of recieved in a certain amount of time
        /// </summary>
        //[System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        //[System.Security.SecurityCritical]
        void SendReceieve()
        {
            Started = System.DateTime.UtcNow;

            //Don't worry about overflow.
            unchecked
            {

                Begin:

                bool critical = false;

                try
                {
                    ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Begin");

                    System.DateTime lastOperation = System.DateTime.UtcNow;

                    //Todo, HandOff
                    if (DoSignalWork()) lastOperation = System.DateTime.UtcNow;

                    System.Net.Sockets.SocketError lastError = SocketError.SocketError;

                    bool shouldStop = IsDisposed || m_StopRequested;

                    //Should keep error Count and if errorCount == TransportContexts.Count then return otherwise reset.

                    int receivedRtp = 0, receivedRtcp = 0;

                    bool duplexing, rtpEnabled, rtcpEnabled;

                    //Until aborted
                    while (false.Equals(shouldStop))
                    {
                        //Keep how much time has elapsed thus far
                        System.TimeSpan taken = System.DateTime.UtcNow - lastOperation;

                        //Todo
                        //use a local projection of GetTransportContexts()
                        //Use a sort by the last active context so that least active contexts go first

                        //set stop by all

                        //Stop if nothing has happed in at least the time required for sending and receiving on all contexts.
                        //shouldStop = GetTransportContexts().Where(tc => false == IDisposedExtensions.IsNullOrDisposed(tc)).All(tc => tc.SendInterval > TimeSpan.Zero ? taken > tc.SendInterval + tc.ReceiveInterval : false);

                        //peek thread exit
                        if (shouldStop || IsDisposed || m_StopRequested) return;

                        #region Recieve Incoming Data

                        //See Todo, this only increases usage in most environments, however if you have > 1 GBPS and you really want to try...

                        ////System.Collections.ArrayList readSockets = new System.Collections.ArrayList();

                        ////System.Collections.ArrayList writeSockets = new System.Collections.ArrayList();

                        ////System.Collections.ArrayList errorSockets = new System.Collections.ArrayList();

                        //Loop each context, newly added contexts will be seen on each iteration
                        for (int i = 0; false.Equals(shouldStop || IsDisposed || m_StopRequested) && i < TransportContexts.Count; ++i)
                        {

                            //Todo, HandOff
                            if (DoSignalWork()) lastOperation = System.DateTime.UtcNow;

                            ////readSockets.Clear();

                            ////writeSockets.Clear();

                            ////errorSockets.Clear();

                            //Obtain a context
                            TransportContext tc = TransportContexts[i];

                            //Check for a context which is able to receive data
                            if (IDisposedExtensions.IsNullOrDisposed(tc)
                                //Active must be true
                                || false.Equals(tc.IsActive)
                                //If the context does not have continious media it must only receive data for the duration of the media.
                                || false.Equals(tc.IsContinious) && tc.TimeRemaining < System.TimeSpan.Zero
                                //There can't be a Goodbye sent or received
                                || false.Equals(tc.Goodbye == null)) continue;

                            //Receive Data on the RtpSocket and RtcpSocket, summize the amount of bytes received from each socket.

                            //Reset the error.
                            lastError = SocketError.SocketError;

                            //Ensure priority is above normal
                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;

                            //Critical
                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;

                            //Rfphd
                            receivedRtp = 0;
                            receivedRtcp = 0;

                            duplexing = tc.IsDuplexing;

                            rtpEnabled = tc.IsRtpEnabled;

                            rtcpEnabled = tc.IsRtcpEnabled;

                            ////readSockets.Add(tc.RtpSocket);

                            ////readSockets.Add(tc.RtcpSocket);

                            ////writeSockets.Add(tc.RtpSocket);

                            ////writeSockets.Add(tc.RtcpSocket);

                            ////errorSockets.Add(tc.RtpSocket);

                            ////errorSockets.Add(tc.RtcpSocket);

                            //-1 may not poll forever, may return immediately. error [could] mean out of band inline
                            ////System.Net.Sockets.Socket.Select(readSockets, writeSockets, errorSockets, -1);

                            //Determine how long to poll for, use 1 quarter of the entire interval
                            int usec = (int)TimeSpanExtensions.TotalMicroseconds(tc.m_ReceiveInterval) >> 4;

                            //If receiving Rtp and the socket is able to read
                            if (rtpEnabled && (shouldStop || IsDisposed || m_StopRequested).Equals(false)
                                //&& (readSockets.Contains(tc.RtpSocket) || errorSockets.Contains(tc.RtpSocket))
                                //Check if the socket can read data first or that data needs to be received
                                && tc.RtpSocket.Poll(usec, System.Net.Sockets.SelectMode.SelectRead) || (tc.LastRtpPacketReceived.Equals(System.TimeSpan.MinValue) || tc.LastRtpPacketReceived >= tc.m_ReceiveInterval))
                            {
                                //Receive RtpData
                                receivedRtp += ReceiveData(tc.RtpSocket, ref tc.RemoteRtp, out lastError, rtpEnabled, duplexing, tc.ContextMemory);

                                //Check if an error occured
                                if (receivedRtp.Equals(0) || false.Equals(lastError == SocketError.Success))
                                {
                                    //Increment for failed receptions
                                    ++tc.m_FailedRtpReceptions;

                                    //Log for the error
                                    ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve RtpSocket - SocketError = " + lastError + " lastOperation = " + lastOperation + " taken = " + taken);

                                    switch (lastError)
                                    {
                                        case SocketError.Success: lastOperation = System.DateTime.UtcNow; break;
                                        case SocketError.ConnectionReset:
                                        case SocketError.ConnectionAborted:
                                        case SocketError.AccessDenied:
                                            ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve (Rtp)Deactivate");
                                            Deactivate();
                                            return;
                                    }
                                }
                            }
                            else if (rtpEnabled && taken >= tc.m_ReceiveInterval)
                            {
                                //Indicate the poll was not successful
                                lastError = SocketError.TimedOut;

                                ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Unable to Poll RtpSocket in tc.m_ReceiveInterval = " + tc.ReceiveInterval + ", taken =" + taken);
                            }

                            //if Rtcp is enabled
                            if (rtcpEnabled && false.Equals(shouldStop || IsDisposed || m_StopRequested))
                            {
                                //Check if reports needs to be received (Sometimes data doesn't flow immediately)
                                bool needsToReceiveReports = TotalBytesReceieved > 0 && (tc.LastRtcpReportReceived.Equals(System.TimeSpan.MinValue) || tc.LastRtcpReportReceived >= tc.m_ReceiveInterval);

                                //The last report was never received or recieved longer ago then required
                                if (needsToReceiveReports
                                    //&& (readSockets.Contains(tc.RtcpSocket) || errorSockets.Contains(tc.RtcpSocket))
                                    //And the socket can read
                                    && tc.RtcpSocket.Poll(usec, System.Net.Sockets.SelectMode.SelectRead))
                                {
                                    //ReceiveRtcp Data
                                    receivedRtcp += ReceiveData(tc.RtcpSocket, ref tc.RemoteRtcp, out lastError, duplexing, rtcpEnabled, tc.ContextMemory);

                                    //Check if an error occured
                                    if (receivedRtcp.Equals(0) || false.Equals(lastError == SocketError.Success))
                                    {
                                        //Increment for failed receptions
                                        ++tc.m_FailedRtcpReceptions;

                                        //Log for the error
                                        ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve RtcpSocket - SocketError = " + lastError + " lastOperation = " + lastOperation + " taken = " + taken);

                                        switch (lastError)
                                        {
                                            case SocketError.Success: lastOperation = System.DateTime.UtcNow; break;
                                            case SocketError.ConnectionReset:
                                            case SocketError.ConnectionAborted:
                                            case SocketError.AccessDenied:
                                                ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve (Rtcp)Deactivate");
                                                Deactivate();
                                                return;
                                        }
                                    }
                                }
                                else if (rtcpEnabled && needsToReceiveReports && false.Equals(tc.HasAnyRecentActivity))
                                {
                                    //Indicate the poll was not successful
                                    lastError = SocketError.TimedOut;

                                    //If data is not yet flowing the do not log.
                                    ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - No RecentActivity and Unable to Poll RtcpSocket, LastReportsReceived = " + tc.LastRtcpReportReceived + ", taken =" + taken);
                                }

                                //Try to send reports for the latest packets or a goodbye if inactive.
                                if (SendReports(tc, out lastError) || SendGoodbyeIfInactive(lastOperation, tc)) lastOperation = System.DateTime.UtcNow;
                            }
                            else if (false.Equals(tc.HasSentRtcpWithinSendInterval) && SendReports(tc, out lastError)) lastOperation = System.DateTime.UtcNow;
                        }

                        //if there was a socket error at the last stage
                        switch (lastError)
                        {
                            case SocketError.SocketError:
                            case SocketError.Success:
                                break;
                            default:
                                {
                                    //If there are no packets outgoing
                                    if ((m_OutgoingRtcpPackets.Count + m_OutgoingRtpPackets.Count).Equals(0))
                                    {

                                        //Just Take no action (leave Priority Normal)
                                        break;

                                        #region Unused, Throttle Priority when there are no outgoing packets.

                                        //System.Threading.Thread.CurrentThread.Priority = false == m_EventReady.Wait(TimeSpanExtensions.OneTick) ? ThreadPriority.Normal : ThreadPriority.BelowNormal;

                                        //System.Threading.Thread.CurrentThread.Priority = m_EventReady.IsSet ? ThreadPriority.BelowNormal : ThreadPriority.Normal;

                                        //////Attempt to Halt and use the rest of the time slice, if no interrupt was received use BlowNormal
                                        ////if (false == System.Threading.Thread.Yield()) System.Threading.Thread.CurrentThread.Priority = ThreadPriority.Normal;
                                        ////else System.Threading.Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                                        #endregion

                                    }
                                    else
                                    {
                                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                                    }

                                    break;
                                }
                        }

                        //Critical
                        System.Threading.Thread.EndCriticalRegion();

                        critical = false;

                        #endregion

                        #region Handle Outgoing RtcpPackets

                        int remove = m_OutgoingRtcpPackets.Count;

                        if (remove > 0)
                        {
                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;

                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;
                            //Todo, do a TakeWhile and sort by something which will allow packets which have different parties or channels.

                            //Try and send the lot of them
                            if (SendRtcpPackets(System.Linq.Enumerable.Take(m_OutgoingRtcpPackets, remove)) > 0)
                            {
                                lastOperation = System.DateTime.UtcNow;

                                //Remove what was attempted to be sent (don't try to send again)
                                m_OutgoingRtcpPackets.RemoveRange(0, remove);
                            }

                            System.Threading.Thread.EndCriticalRegion();

                            critical = false;

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                        }

                        #endregion

                        #region Handle Outgoing RtpPackets

                        remove = m_OutgoingRtpPackets.Count;

                        if (remove > 0)
                        {

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;

                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;

                            //Could check for timestamp more recent then packet at 0  on transporContext and discard...
                            //Send only A few at a time to share with rtcp

                            //If more than 1 thread is accessing this logic one could declare another varaible to compare what was supposed to be removed with what is actually being removed.
                            remove = 0;

                            //int? lastTimestamp;

                            //Take the list count to reduce exceptions
                            for (int i = 0; i < m_OutgoingRtpPackets.Count; ++i)
                            {
                                //Get a packet
                                RtpPacket packet = m_OutgoingRtpPackets[i];

                                //If already disposed
                                if (IDisposedExtensions.IsNullOrDisposed(packet))
                                {
                                    ++remove;

                                    continue;
                                }

                                //If the packet should dispose
                                bool shouldDispose = packet.ShouldDispose;

                                //prevent dispose
                                if (shouldDispose) BaseDisposable.SetShouldDispose(packet, false, false);

                                //Get the context for the packet
                                TransportContext sendContext = GetContextForPacket(packet);

                                if (IDisposedExtensions.IsNullOrDisposed(sendContext) || IDisposedExtensions.IsNullOrDisposed(sendContext.Goodbye).Equals(false))
                                {
                                    ++remove;

                                    continue;
                                }

                                //Send the packet using the context's SynchronizationSourceIdentifier
                                if (SendRtpPacket(packet, sendContext, out lastError, sendContext.SynchronizationSourceIdentifier) >= packet.Length /* && lastError == SocketError.Success*/)
                                {
                                    lastOperation = System.DateTime.UtcNow;
                                }

                                //Indicate to remove another packet
                                ++remove;

                                if (shouldDispose) BaseDisposable.SetShouldDispose(packet, true, false);

                                if (m_StopRequested) break;


                                //Could also check timestamp in cases where marker is not being set
                                //if (lastTimestamp.HasValue && packet.Timestamp != lastTimestamp) break;
                                //lastTimestamp = packet.Timestamp;
                            }

                            //If any packets should be removed remove them now
                            if (remove > 0)
                            {
                                //Todo, Place in Confirm Stage for resending.
                                //Examine Rtcp RR and prune Confirming packets based on HighestExtendedSequenceNumber.
                                //When Feedback is used packets in Confirming stage can be re-transmitted easily.
                                //Packets outside of Confirming are either not yet sent or have been sent long ago, take care when honoring re-transmission

                                //Remove what was sent
                                m_OutgoingRtpPackets.RemoveRange(0, remove);
                            }

                            System.Threading.Thread.EndCriticalRegion();

                            critical = false;

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                        }

                        #endregion
                    }
                }
                catch (System.Threading.ThreadAbortException)
                {
                    System.Threading.Thread.ResetAbort();

                    if (critical) System.Threading.Thread.EndCriticalRegion();

                    ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve Aborted");
                }
                catch (System.Exception ex)
                {
                    //if (ex is SocketException)
                    //{
                    //    SocketException se = ex as SocketException;

                    //    if (se.SocketErrorCode == SocketError.ConnectionAborted || se.SocketErrorCode == SocketError.ConnectionReset)
                    //    {
                    //        return;
                    //    }
                    //}

                    ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve: " + ex.Message);

                    if (critical) System.Threading.Thread.EndCriticalRegion();

                    goto Begin;
                }
            }

            ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Exit");
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Calls <see cref="Deactivate"/> and disposes all contained <see cref="RtpClient.TransportContext"/>.
        /// Stops the raising of any events.
        /// Removes the Logger
        /// </summary>
        protected internal override void Dispose(bool disposing)
        {
            if (false.Equals(disposing) || false.Equals(ShouldDispose)) return;

            base.Dispose(ShouldDispose);

            if (false.Equals(IsDisposed)) return;

            DisposeAndClearTransportContexts();

            //Stop raising events
            RtpPacketSent = null;
            RtcpPacketSent = null;
            RtpPacketReceieved = null;
            RtcpPacketReceieved = null;
            OutOfBandData = null;

            //Send abort signal to all threads contained.
            //Todo, maybe offer Delegate AbortDelegate..
            IThreadReferenceExtensions.AbortAndFreeAll(this);

            //Empty packet buffers
            m_OutgoingRtpPackets.Clear();

            //m_OutgoingRtpPackets = null;

            m_OutgoingRtcpPackets.Clear();

            //m_OutgoingRtcpPackets = null;

            ThreadEvents = false;

            m_EventData.Clear();

            //Allow a waiting thread to exit
            m_EventReady.Set();

            m_EventReady.Reset();

            AdditionalSourceDescriptionItems.Clear();

            ClientName = null;

            //Remove the buffer
            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(m_Buffer)))
            {
                m_Buffer.Dispose();

                m_Buffer = null;
            }

            ILoggingExtensions.Log(Logger, GetType().Name + "(" + ToString() + ")@Dipose - Complete");

            //Unset the logger
            Logger = null;
        }

        #endregion

        public IEnumerable<Thread> GetReferencedThreads()
        {
            IEnumerable<System.Threading.Thread> threads = System.Linq.Enumerable.Empty<System.Threading.Thread>();

            if (IsDisposed) return threads;

            if (IsActive) threads = threads.Concat(LinqExtensions.Yield(m_WorkerThread));

            if (m_ThreadEvents) threads = threads.Concat(LinqExtensions.Yield(m_EventThread));

            return threads;
        }

        public bool TrySetLogger(ILogging logger)
        {
            if (IDisposedExtensions.IsNullOrDisposed(this)) return false;

            Logger = logger;

            return true;
        }

        public bool TryGetLogger(out ILogging logger)
        {
            if (IDisposedExtensions.IsNullOrDisposed(this))
            {
                logger = null;

                return false;
            }

            logger = Logger;

            return true;
        }

        #region Nested Types

        /// <summary>
        ///Contains the information and assets relevent to each stream in use by a RtpClient
        /// </summary>
        public class TransportContext : SuppressedFinalizerDisposable, ISocketReference
        {
            #region Statics

            //Todo
            internal static byte[] CreateApplicationLayerFraming(TransportContext context)
            {
                //Determine  how many bytes, independent uses 2 where as rtsp uses 4

                //Determine if RFC4571 via the Connection line etc.

                int size = InterleavedOverhead;

                byte[] result = new byte[size];

                return result;
            }

            //ReadApplictionLayerFraming should be here also...

            //The virtuals could probably be moved here such as PrepareReports etc.

            internal static void ConfigureRtpRtcpSocket(Socket socket) //,ILogging = null
            {
                if (socket == null) throw new ArgumentNullException("socket");

                ExceptionExtensions.ResumeOnError(() => SocketExtensions.EnableAddressReuse(socket));

                //RtpSocket.Blocking = false;

                //RtpSocket.SendBufferSize = RtpSocket.ReceiveBufferSize = 0; //Use local buffer dont copy

                //IP Options for InterNetwork
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    //http://en.wikipedia.org/wiki/Type_of_service
                    //CS5,EF	40,46	5 :Critical - mainly used for voice RTP
                    //40 || 46 is used for RTP Audio per Wikipedia
                    //48 is Internetwork Control
                    //56 is Network Control
                    //Set type of service

                    ExceptionExtensions.ResumeOnError(() => socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 47));

                    //Tell the network stack what we send and receive has an order
                    ExceptionExtensions.ResumeOnError(() => socket.DontFragment = true);
                }

                //Don't buffer sending
                ExceptionExtensions.ResumeOnError(() => socket.SendBufferSize = 0);

                if (socket.ProtocolType == ProtocolType.Tcp)
                {
                    //Retransmit for 0 sec
                    if (OperatingSystemExtensions.IsWindows)
                    {
                        //Disable Retransmission
                        ExceptionExtensions.ResumeOnError(() => SocketExtensions.DisableTcpRetransmissions(socket));

                        // Enable No Syn Retries
                        Extensions.ExceptionExtensions.ResumeOnError(() => SocketExtensions.EnableTcpNoSynRetries(socket));

                        // Set OffloadPreferred
                        Extensions.ExceptionExtensions.ResumeOnError(() => SocketExtensions.SetTcpOffloadPreference(socket));

                    }

                    //Don't buffer receiving
                    ExceptionExtensions.ResumeOnError(() => socket.ReceiveBufferSize = 0);


                    //If both send and receieve buffer size are 0 then there is no coalescing when nagle's algorithm is disabled
                    ExceptionExtensions.ResumeOnError(() => SocketExtensions.DisableTcpNagelAlgorithm(socket));
                }
                else if (socket.ProtocolType == ProtocolType.Udp)
                {
                    //Set max ttl for slower networks
                    ExceptionExtensions.ResumeOnError(() => socket.Ttl = 255);

                    //May help if behind a router
                    //Allow Nat Traversal
                    //RtpSocket.SetIPProtectionLevel(IPProtectionLevel.Unrestricted);
                }

                //Todo, set RecieveTimeout.. (done in From methods)

                //socket.SendTimeout = 1;
                //socket.ReceiveTimeout = 1;
            }

            public static TransportContext FromMediaDescription(Sdp.SessionDescription sessionDescription,
                byte dataChannel, byte controlChannel,
                Sdp.MediaDescription mediaDescription,
                bool rtcpEnabled = true, int remoteSsrc = 0, int minimumSequentialpackets = 2,
                IPAddress localIp = null, IPAddress remoteIp = null,
                int? rtpPort = null, int? rtcpPort = null,
                bool connect = false,
                Socket existingSocket = null,
                Action<Socket> configure = null)
            {
                //Must have a mediaDescription
                if (IDisposedExtensions.IsNullOrDisposed(mediaDescription)) throw new ArgumentNullException("mediaDescription");

                //If there is no sdp there must be a local and remoteIp
                if (IDisposedExtensions.IsNullOrDisposed(sessionDescription) && (object.ReferenceEquals(localIp, null) || object.ReferenceEquals(remoteIp, null))) throw new InvalidOperationException("Must have a sessionDescription or the localIp and remoteIp cannot be established.");

                //If no remoteIp was given attempt to parse it from the sdp
                if (object.ReferenceEquals(remoteIp, null))
                {
                    Sdp.SessionDescriptionLine cLine = mediaDescription.ConnectionLine;

                    //Try the sesion level if the media level doesn't have one
                    if (object.ReferenceEquals(cLine, null)) cLine = sessionDescription.ConnectionLine;

                    //Attempt to parse the IP, if failed then throw an exception.
                    if (object.ReferenceEquals(cLine, null)
                        ||
                        false.Equals(IPAddress.TryParse(new Sdp.Lines.SessionConnectionLine(cLine).Host, out remoteIp))) throw new InvalidOperationException("Cannot determine remoteIp from ConnectionLine");
                }

                //For AnySourceMulticast the remoteIp would be a multicast address.
                bool multiCast = System.Net.IPAddress.Broadcast.Equals(remoteIp) || IPAddressExtensions.IsMulticast(remoteIp);

                //If no localIp was given determine based on the remoteIp
                //--When there is no remoteIp this should be done first to determine if the sender is multicasting.
                if (object.ReferenceEquals(localIp, null)) localIp = multiCast ? SocketExtensions.GetFirstMulticastIPAddress(remoteIp.AddressFamily) : SocketExtensions.GetFirstUnicastIPAddress(remoteIp.AddressFamily);

                //The localIp and remoteIp should be on the same network otherwise they will need to be mapped or routed.
                //In most cases this can be mapped.
                if (false.Equals(localIp.AddressFamily == remoteIp.AddressFamily)) throw new InvalidOperationException("local and remote address family must match, please create an issue and supply a capture.");

                //Todo, need TTL here.

                //Should also probably store the network interface.

                int ttl = 255;

                //If no remoteSsrc was given then check for one
                if (remoteSsrc.Equals(0))
                {
                    //Check for SSRC Attribute Line on the Media Description
                    //a=ssrc:<ssrc-id> <attribute>
                    //a=ssrc:<ssrc-id> <attribute>:<value>

                    Sdp.SessionDescriptionLine ssrcLine = mediaDescription.SsrcLine;

                    //To use typed line

                    if (object.ReferenceEquals(ssrcLine, null).Equals(false))
                    {
                        string part = ssrcLine.GetPart(1);

                        if (false == string.IsNullOrWhiteSpace(part))
                        {
                            remoteSsrc = part[0] == '-' ? (int)uint.Parse(part) : int.Parse(part);
                        }
                    }
                }

                //Create the context
                TransportContext tc = new TransportContext(dataChannel, controlChannel, RFC3550.Random32(Media.Rtcp.SourceDescriptionReport.PayloadType), mediaDescription,
                    rtcpEnabled, remoteSsrc, minimumSequentialpackets);

                int reportReceivingEvery = 0,
                    reportSendingEvery = 0,
                    asData = 0;

                //If rtcp is enabled
                if (rtcpEnabled)
                {
                    //Set to the default interval
                    reportSendingEvery = reportReceivingEvery = (int)DefaultReportInterval.TotalMilliseconds;

                    //Todo should be using the BandwidthLine type and IsDisabled property of instance
                    //Then would have access to BandwidthTypeString on instance also.

                    //If any bandwidth lines were parsed
                    if (Media.Sdp.Lines.SessionBandwidthLine.TryParseBandwidthDirectives(mediaDescription, out reportReceivingEvery, out reportSendingEvery, out asData))
                    {
                        //Determine if rtcp is disabled in the media description
                        bool rtcpDisabled = reportReceivingEvery.Equals(0) && reportSendingEvery.Equals(0);

                        //If Rtcp is not disabled then this will set the read and write timeouts.
                        if (false.Equals(rtcpDisabled))
                        {
                            /*
                             For the RTP A/V Profile [2], which specifies that the default RTCP
                                interval algorithm defined in the RTP spec [1] is to be used, at
                                least RS/(RS+RR) of the RTCP bandwidth is dedicated to active data
                                senders.  If the proportion of senders to total participants is less
                                than or equal to RS/(RS+RR), each sender gets RS divided by the
                                number of senders.  When the proportion of senders is greater than
                                RS/(RS+RR), the senders get their proportion of the sum of these
                                parameters, which means that a sender and a non-sender each get the
                                same allocation.  Therefore, it is not possible to constrain the data
                                senders to use less RTCP bandwidth than is allowed for non-senders.
                                A few special cases are worth noting:
                             */

                            tc.IsRtcpEnabled = true;

                            if (reportReceivingEvery > 0) tc.m_ReceiveInterval = TimeSpan.FromSeconds(reportReceivingEvery / Extensions.TimeSpanExtensions.MicrosecondsPerMillisecond);

                            if (reportSendingEvery > 0) tc.m_SendInterval = TimeSpan.FromSeconds(reportSendingEvery / Extensions.TimeSpanExtensions.MicrosecondsPerMillisecond);

                            //Todo
                            //Should set MaximumRtcpBandwidthPercentage

                            //Todo, should set Send and ReceiveTimeout

                            //Todo, specify Report Interval seperately..

                        }//Disable rtcp (already checked to be enabled)
                        //else if (rtcpEnabled) tc.IsRtcpEnabled = false;
                    }
                }

                //Check Time Description? use start and end rather than range? (Only if not 0 0) then...

                //var timeDesc = sessionDescription.TimeDescriptions.FirstOrDefault();

                //if (timeDesc != null)
                //{
                //    ///
                //}

                //check for range in mediaDescription

                //Another hacky way would be to simply leave EndTime null.... 

                var rangeInfo = mediaDescription.RangeLine ?? (IDisposedExtensions.IsNullOrDisposed(sessionDescription).Equals(false) ? sessionDescription.RangeLine : null);

                if (object.ReferenceEquals(rangeInfo, null).Equals(false) && rangeInfo.m_Parts.Count > 0)
                {
                    string type;

                    Media.Sdp.SessionDescription.TryParseRange(rangeInfo.m_Parts.First(), out type, out tc.m_StartTime, out tc.m_EndTime);
                }

                //https://www.ietf.org/rfc/rfc3605.txt

                //rtcpAttribute indicates if RTCP should use a special port and not be dervied from the RtpPort algorithmically 

                //"a=rtcp:" 

                /*
                 
                  Example encodings could be:

                    m=audio 49170 RTP/AVP 0
                    a=rtcp:53020

                    m=audio 49170 RTP/AVP 0
                    a=rtcp:53020 IN IP4 126.16.64.4

                    m=audio 49170 RTP/AVP 0
                    a=rtcp:53020 IN IP6 2001:2345:6789:ABCD:EF01:2345:6789:ABCD
                 
                 */

                Sdp.SessionDescriptionLine rtcpLine = mediaDescription.RtcpLine;

                if (object.ReferenceEquals(rtcpLine, null).Equals(false))
                {
                    //Todo...

                    throw new NotImplementedException("Make a thread if you need rtcp AttributeField support immediately.");
                }

                //rtcp-mux is handled in the Initialize call

                //tc.ConfigureSocket = given;

                //Todo, should verify ports against PortRange when HasPortRange == true.

                //if (mediaDescription.PortRange.HasValue) //mediaDescription.HasPortRange
                //{

                //}

                //Handle connect
                if (connect)
                {
                    //Determine if a socket was given or if it will be created.
                    bool hasSocket = object.ReferenceEquals(existingSocket, null).Equals(false);

                    //If a configuration has been given then set that configuration in the TransportContext.
                    if (object.ReferenceEquals(configure, null).Equals(false)) tc.ConfigureSocket = configure;

                    //Check for udp if no existing socket was given
                    if (hasSocket.Equals(false) && string.Compare(mediaDescription.MediaProtocol, Media.Rtp.RtpClient.RtpAvpProfileIdentifier, true).Equals(0))
                    {
                        //Registers must be stored, it might make more sense to leave the defaults as is and change the parameters of this function to then
                        //- allow for sockets to be given to the Initialize e.g. a Initializer delegate.
                        //Todo, Allow for Register and Creation from the MediaDescription...

                        //TODO, allow for localPort to be specified.

                        //Find a local port
                        int localPort = SocketExtensions.ProbeForOpenPort(ProtocolType.Udp);

                        if (localPort < 0) throw new ArgumentOutOfRangeException("Cannot find an open port.");

                        //Create the sockets and connect
                        tc.Initialize(localIp, remoteIp, //LocalIP, RemoteIP
                            localPort.Equals(0) ? localPort : localPort++, //LocalRtp
                            localPort.Equals(0) ? localPort : localPort++, //LocalRtcp                            
                            rtpPort ?? mediaDescription.MediaPort, //RemoteRtp
                            rtcpPort ?? (false.Equals(mediaDescription.MediaPort.Equals(0)) ? mediaDescription.MediaPort + 1 : mediaDescription.MediaPort)); //RemoteRtcp
                    }
                    else if (hasSocket)//If had a socket use it
                    {
                        tc.Initialize(existingSocket);
                    }
                    else //Create the sockets and connect (TCP)
                    {
                        //Todo, verify c= line on sdp or m= and ensure framing type.

                        tc.Initialize(localIp, remoteIp, rtpPort ?? mediaDescription.MediaPort);
                    }

                    //Needs ttl here.

                    //Should also check for the ConnectionAddress even if remoteIp was given...

                    if (multiCast)
                    {
                        //remoteIp should be groupAdd from media c= line.

                        //If the address cannot be joined then an exception will occur here.
                        try
                        {

                            SocketExtensions.JoinMulticastGroup(tc.RtpSocket, remoteIp);

                            SocketExtensions.SetMulticastTimeToLive(tc.RtpSocket, ttl);

                            if (rtcpEnabled && false.Equals(tc.RtcpSocket.Handle == tc.RtpSocket.Handle))
                            {
                                SocketExtensions.JoinMulticastGroup(tc.RtcpSocket, remoteIp);

                                SocketExtensions.SetMulticastTimeToLive(tc.RtcpSocket, ttl);
                            }
                        }

                        catch
                        {
                            //Handle in application.
                        }
                    }
                }

                //Return the context created
                return tc;
            }

            public static GoodbyeReport CreateGoodbye(TransportContext context, byte[] reasonForLeaving = null, int? ssrc = null, RFC3550.SourceList sourcesLeaving = null)
            {
                //Make a Goodbye, indicate version in Client, allow reason for leaving 
                //Todo add other parties where null with SourceList
                return new GoodbyeReport(context.Version, ssrc ?? (int)context.SynchronizationSourceIdentifier, sourcesLeaving, reasonForLeaving);
            }

            /// <summary>
            /// Creates a <see cref="SendersReport"/> from the given context and updates the RtpExpectedPrior and RtpReceivedPrior accordingly.
            /// Note, If empty is false and no previous <see cref="SendersReport"/> was sent then the report will be empty anyway.
            /// </summary>
            /// <param name="context"></param>
            /// <param name="empty">Specifies if the report should have any report blocks if possible</param>
            /// <returns>The report created</returns>
            /// TODO, Allow an alternate ssrc
            public static SendersReport CreateSendersReport(TransportContext context, bool empty, bool rfc = true)
            {
                //Create a SendersReport
                SendersReport result = new SendersReport(context.Version, 0, context.SynchronizationSourceIdentifier);

                //Use the values from the TransportChannel (Use .NtpTimestamp = 0 to Disable NTP)[Should allow for this to be disabled]
                result.NtpTimestamp = context.SenderNtpTimestamp + context.SenderNtpOffset;

                if (result.NtpTimestamp == 0) result.NtpDateTime = DateTime.UtcNow;

                //Note that in most cases this timestamp will not be equal to the RTP timestamp in any adjacent data packet.  Rather, it MUST be  calculated from the corresponding NTP timestamp using the relationship between the RTP timestamp counter and real time as maintained by periodically checking the wallclock time at a sampling instant.
                result.RtpTimestamp = context.SenderRtpTimestamp;

                //If no data has been received this value will be 0, set it to the expected value based on the time.
                if (result.RtpTimestamp == 0) result.RtpTimestamp = (int)NetworkTimeProtocol.DateTimeToNptTimestamp32(result.NtpDateTime);

                //Counters
                result.SendersOctetCount = (int)(rfc ? context.RfcRtpBytesSent : context.RtpBytesSent);
                result.SendersPacketCount = (int)context.RtpPacketsSent;

                //Ensure there is a remote party
                //If source blocks are included include them and calculate their statistics
                if (false.Equals(empty) && false.Equals(context.InDiscovery) && context.IsValid && context.TotalPacketsSent > 0)
                {
                    uint fraction, lost;

                    RFC3550.CalculateFractionAndLoss(ref context.RtpBaseSeq, ref context.RtpMaxSeq, ref context.RtpSeqCycles, ref context.ValidRtpPacketsReceived, ref context.RtpReceivedPrior, ref context.RtpExpectedPrior, out fraction, out lost);

                    //Create the ReportBlock based off the statistics of the last RtpPacket and last SendersReport
                    result.Add(new ReportBlock((int)context.RemoteSynchronizationSourceIdentifier,
                        (byte)fraction,
                        (int)lost,
                        context.SendSequenceNumber,
                        (int)context.SenderJitter,
                        //The middle 32 bits out of 64 in the NTP timestamp (as explained in Section 4) received as part of the most recent RTCP sender report (SR) packet from source SSRC_n. If no SR has been received yet, the field is set to zero.
                        (int)((context.SenderNtpTimestamp >> 16) << 32),
                        //The delay, expressed in units of 1/65536 seconds, between receiving the last SR packet from source SSRC_n and sending this reception report block. If no SR packet has been received yet from SSRC_n, the DLSR field is set to zero.
                        context.LastRtcpReportSent > TimeSpan.MinValue ? (int)context.LastRtcpReportSent.TotalSeconds / ushort.MaxValue : 0));
                }

                return result;
            }

            /// <summary>
            /// Creates a <see cref="ReceiversReport"/> from the given context and updates the RtpExpectedPrior and RtpReceivedPrior accordingly.
            /// </summary>
            /// <param name="context">The context</param>
            /// <param name="empty">Indicates if the report should be empty</param>
            /// <returns>The report created</returns>
            public static ReceiversReport CreateReceiversReport(TransportContext context, bool empty)
            {
                ReceiversReport result = new ReceiversReport(context.Version, 0, context.SynchronizationSourceIdentifier);

                //if (false == empty && false == context.InDiscovery && context.IsValid && context.TotalRtpPacketsReceieved > 0)
                if (false.Equals(empty) && context.TotalRtpPacketsReceieved > 0)
                {
                    uint fraction, lost;

                    RFC3550.CalculateFractionAndLoss(ref context.RtpBaseSeq, ref context.RtpMaxSeq, ref context.RtpSeqCycles, ref context.ValidRtpPacketsReceived, ref context.RtpReceivedPrior, ref context.RtpExpectedPrior, out fraction, out lost);

                    //Create the ReportBlock based off the statistics of the last RtpPacket and last SendersReport
                    result.Add(new ReportBlock((int)context.RemoteSynchronizationSourceIdentifier,
                        (byte)fraction,
                        (int)lost,
                        context.RecieveSequenceNumber,
                        (int)context.RtpJitter >> 4,//The last report may not be null but may be disposed and time is probably invalid if so, in such a case use LastRtcpReportRecieved                    
                        (int)(false == IDisposedExtensions.IsNullOrDisposed(context.SendersReport) ? NetworkTimeProtocol.DateTimeToNptTimestamp32(context.SendersReport.NtpDateTime) : context.LastRtcpReportReceived > TimeSpan.MinValue ? NetworkTimeProtocol.DateTimeToNptTimestamp32(DateTime.UtcNow - context.LastRtcpReportReceived) : 0),
                        (context.SendersReport != null ? ((DateTime.UtcNow - context.SendersReport.Created).Seconds / ushort.MaxValue) * 1000 : 0) //If also sending senders reports this logic may not be correct
                                                                                                                                                   //context.LastRtcpReportSent > TimeSpan.MinValue ? (int)context.LastRtcpReportSent.TotalSeconds / ushort.MaxValue : 0)
                    ));

                }

                return result;
            }

            /// <summary>
            /// Creates a <see cref="SourceDescriptionReport"/> from the given context.
            /// If <paramref name="cName"/> is null then <see cref="SourceDescriptionItem.CName"/> will be used.
            /// </summary>
            /// <param name="context">The context</param>
            /// <param name="cName">The optional cName to use</param>
            /// <returns>The created report</returns>
            public static SourceDescriptionReport CreateSourceDescription(TransportContext context, Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem cName = null, IEnumerable<Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem> items = null)
            {
                //Todo, params context overload? overload with other Items
                return new SourceDescriptionReport(context.Version)
                { 
                    //Todo, should have ip / port etc to identify multiple connections to the same server
                    new Media.Rtcp.SourceDescriptionReport.SourceDescriptionChunk((int)context.SynchronizationSourceIdentifier, Common.Extensions.LinqExtensions.Yield((cName ?? Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem.CName)).Concat(items ?? System.Linq.Enumerable.Empty<Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem>()))
                };
            }

            //FrameGenerator => RtpPacketHandler

            #endregion

            #region Fields

            /// <summary>
            /// The version of packets which the TransportContents handles
            /// </summary>
            public int Version = 2;

            /// <summary>
            /// The amount of <see cref="RtpPacket"/>'s which must be received before IsValid is true.
            /// </summary>
            public int MinimumSequentialValidRtpPackets = RFC3550.DefaultMinimumSequentalRtpPackets;

            public int MaxMisorder = RFC3550.DefaultMaxMisorder;

            public int MaxDropout = RFC3550.DefaultMaxDropout;

            /// <summary>
            /// The channels which identity the TransportContext.
            /// </summary>
            public byte DataChannel, ControlChannel;

            /// <summary>
            /// Indicates if Rtp is enabled on the TransportContext
            /// </summary>
            public bool IsRtpEnabled = true;

            /// <summary>
            /// Indicates if Rtcp will be used on this TransportContext
            /// </summary>
            public bool IsRtcpEnabled = true;

            //The EndPoints connected to (once connected don't need the Ports unless 0 is used to determine the port)
            internal protected EndPoint LocalRtp, LocalRtcp, RemoteRtp, RemoteRtcp;

            //bytes and packet counters
            internal long RfcRtpBytesSent, RfcRtpBytesRecieved,
                         RtpBytesSent, RtpBytesRecieved,
                         RtcpBytesSent, RtcpBytesRecieved,
                         RtpPacketsSent, RtcpPacketsSent,
                         RtpPacketsReceived, RtcpPacketsReceived;

            //The current, highest received and highest sent Sequence numbers recieved by the RtpClient
            internal ushort m_SequenceNumber, m_LastSentSequenceNumber, RtpMaxSeq;

            //Used for Rtp and Rtcp Transport Calculations (Should be moved into State Structure)
            internal uint RtpTransit, SenderTransit,
                //Count of bytes recieved prior to the reception of a report
                RtpReceivedPrior,
                //Count of bytes expected prior to the recpetion of a report
                RtpExpectedPrior,
                //The amount of times the Seq number has cycled
                RtpSeqCycles,
                //The amount of base RTP Sequences encountered
                RtpBaseSeq,
                //Rtp Probation value
                RtpProbation,
                //The amount of bad RTP Sequences encountered
                RtpBadSeq,
                //Jitter value
                RtpJitter, SenderJitter,
                //Valid amount of rtp packets recieved 
                ValidRtpPacketsReceived;

            internal TimeSpan m_SendInterval = DefaultReportInterval, m_ReceiveInterval = DefaultReportInterval,
                m_InactiveTime = Extensions.TimeSpanExtensions.InfiniteTimeSpan,
                m_StartTime = TimeSpan.Zero, m_EndTime = Extensions.TimeSpanExtensions.InfiniteTimeSpan;
            //Used to allow a specific reporting interval (would proably need varaibles for send and receive to allow full customization...)
            //m_ContextReportInterval = DefaultReportInterval;

            //When packets are succesfully transferred the DateTime (utc) is copied in these variables and will reflect the point in time in which  the last 
            internal DateTime m_FirstPacketReceived, m_FirstPacketSent,
                m_LastRtcpIn, m_LastRtcpOut,  //Rtcp packets were received and sent
                m_LastRtpIn, m_LastRtpOut, //Rtp packets were received and sent
                m_Initialized;//When initialize was called.

            //TimeRange?

            /// <summary>
            /// Keeps track of any failures which occur when sending or receieving data.
            /// </summary>
            internal protected int m_FailedRtpTransmissions, m_FailedRtcpTransmissions, m_FailedRtpReceptions, m_FailedRtcpReceptions;

            /// <summary>
            /// Used to ensure data is quantified as packets which are allowed.
            /// </summary>
            ushort m_MimumPacketSize = RtpHeader.Length - RtcpHeader.Length, m_MaximumPacketSize = ushort.MaxValue;

            /// <summary>
            /// To allow multiple receivers as set by <see cref="MaximumRemoteIdentities"/>
            /// </summary>
            //public readonly List<int> Recievers = new List<int>();

            internal readonly HashSet<System.Net.IPAddress> MulticastGroups = new HashSet<IPAddress>();

            #endregion

            #region Properties

            /// <summary>
            /// Indicates if duplicate packets will be tolerated
            /// </summary>
            public bool AllowDuplicatePackets
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// Indicates if multiple payload types will be tolerated in the <see cref="CurrentFrame"/> or <see cref="LastFrame"/> during 'Add'
            /// </summary>
            public bool AllowsMultiplePayloadTypes
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// Indicates if out of order packets will be tolerated
            /// </summary>
            public bool AllowOutOfOrderPackets
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            ///// <summary>
            ///// A value which indicates the maximum amount of remote sources this context will accept data from.
            ///// </summary>
            //public int MaximumRemoteIdentities
            //{
            //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
            //    get;
            //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
            //    set;
            //}

            /// <summary>
            /// A value which is used when during <see cref="Initialize"/> to set the <see cref="RecieveBufferSize"/> relative to the size of <see cref="ContextMemory"/>
            /// </summary>
            public int RecieveBufferSizeMultiplier
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            public Action<Socket> ConfigureSocket
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// Sets or gets the applications-specific state associated with the TransportContext.
            /// </summary>
            public Object ApplicationContext
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// Gets or sets the MemorySegment used by this context.
            /// </summary>
            public MemorySegment ContextMemory
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// The smallest packet which may be sent or recieved on the TransportContext.
            /// </summary>
            public int MinimumPacketSize
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return (int)m_MimumPacketSize; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set { m_MimumPacketSize = (ushort)value; }
            }

            /// <summary>
            /// The largest packet which may be sent or recieved on the TransportContext.
            /// </summary>
            public int MaximumPacketSize
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return (int)m_MaximumPacketSize; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set { m_MaximumPacketSize = (ushort)value; }
            }

            public bool HasAnyRecentActivity
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return HasRecentRtpActivity || HasRecentRtcpActivity; }
            }


            public bool HasRecentRtpActivity
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    //Check for Rtp Receive Activity if receiving
                    return HasReceivedRtpWithinReceiveInterval
                        || //Check for Rtp Send Activity if sending
                        HasSentRtpWithinSendInterval;
                }
            }

            public bool HasRecentRtcpActivity
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    //Check for Rtcp Receive Activity if receiving
                    return HasReceivedRtcpWithinReceiveInterval
                        || //Check for Rtcp Send Activity if sending
                        HasSentRtcpWithinSendInterval;
                }
            }

            public bool HasReceivedRtpWithinReceiveInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return TotalRtpPacketsReceieved >= 0 &&
                        false.Equals(m_LastRtpIn.Equals(DateTime.MinValue)) &&
                        false.Equals(m_ReceiveInterval.Equals(TimeSpanExtensions.InfiniteTimeSpan)) &&
                        LastRtpPacketReceived < m_ReceiveInterval;
                }
            }

            public bool HasSentRtpWithinSendInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return IsActive && TotalRtpPacketsSent >= 0 &&
                        false.Equals(m_LastRtpOut.Equals(DateTime.MinValue)) &&
                        false.Equals(m_SendInterval.Equals(TimeSpanExtensions.InfiniteTimeSpan)) &&
                        LastRtpPacketSent < m_SendInterval;
                }
            }

            public bool HasReceivedRtcpWithinReceiveInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return TotalRtcpPacketsReceieved >= 0 &&
                        false.Equals(m_LastRtcpIn.Equals(DateTime.MinValue)) &&
                        false.Equals(m_ReceiveInterval.Equals(TimeSpanExtensions.InfiniteTimeSpan)) &&
                        LastRtcpReportReceived < m_ReceiveInterval;
                }
            }

            public bool HasSentRtcpWithinSendInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return TotalRtcpPacketsSent >= 0 &&
                        false.Equals(m_LastRtcpOut == DateTime.MinValue) &&
                        false.Equals(m_SendInterval == TimeSpanExtensions.InfiniteTimeSpan) &&
                        LastRtcpReportSent < m_SendInterval;
                }
            }

            /// <summary>
            /// Indicates if the RemoteParty is known by a unique id other than 0 unless <see cref="MinimumSequentialValidRtpPackets"/> have been recieved
            /// </summary>
            internal bool InDiscovery
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return ValidRtpPacketsReceived < MinimumSequentialValidRtpPackets && RemoteSynchronizationSourceIdentifier.Equals(Binary.Zero); }
            }

            /// <summary>
            /// Gets or Sets a value which indicates if the Rtp and Rtcp Sockets should be Disposed when Dispose is called.
            /// </summary>
            public bool LeaveOpen
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            //Any frames for this channel
            public RtpFrame CurrentFrame
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            public RtpFrame LastFrame
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            //for testing purposes
            //internal RtpFrame Holding
            //{
            //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
            //    get;

            //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
            //    internal protected set;
            //}

            /// <summary>
            /// The socket used for Transport of Rtp and Interleaved data
            /// </summary>
            public Socket RtpSocket
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            /// <summary>
            /// The socket used for Transport of Rtcp and Interleaved data
            /// </summary>
            public Socket RtcpSocket
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            /// <summary>
            /// Indicates if the TransportContext has a <see cref="LocalRtp"/> or <see cref="LocalRtcp"/> EndPoint, usually established in Initialize
            /// </summary>
            public bool IsActive
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    if (IsRtpEnabled)
                    {
                        return false.Equals(object.ReferenceEquals(RtpSocket, null)) && false.Equals(object.ReferenceEquals(LocalRtp, null));
                    }
                    else if (IsRtcpEnabled)
                    {
                        return false.Equals(object.ReferenceEquals(RtcpSocket, null)) && false.Equals(object.ReferenceEquals(LocalRtcp, null));
                    }

                    return false;
                }
            }

            /// <summary>
            /// The maximum amount of bandwidth Rtcp can utilize (of the overall bandwidth available to the TransportContext) during reports
            /// </summary>
            public double MaximumRtcpBandwidthPercentage
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            /// <summary>
            /// Indicates if the amount of bandwith currently utilized for Rtcp reporting has exceeded the amount of bandwidth allowed by the <see cref="MaximumRtcpBandwidthPercentage"/> property.
            /// </summary>
            public bool RtcpBandwidthExceeded
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    if (false.Equals(IsRtcpEnabled) || IsDisposed) return true;

                    double maximumRtcpBandwidthPercentage = MaximumRtcpBandwidthPercentage;

                    //If disposed no limit is imposed do not check
                    if (maximumRtcpBandwidthPercentage.Equals(Binary.DoubleZero)) return false;

                    long totalReceived = TotalBytesReceieved;

                    if (totalReceived.Equals(Binary.LongZero)) return false;

                    long totalRtcp = TotalRtcpBytesSent + TotalRtcpBytesReceieved;

                    if (totalRtcp.Equals(Binary.LongZero)) return false;

                    return totalRtcp >= totalReceived / maximumRtcpBandwidthPercentage;
                }
            }

            /// <summary>
            /// The amount of time the TransportContext has been sending packets.
            /// </summary>
            public TimeSpan TimeSending
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return IsDisposed || m_FirstPacketSent.Equals(DateTime.MinValue) ?
                        Extensions.TimeSpanExtensions.InfiniteTimeSpan
                        :
                        DateTime.UtcNow - m_FirstPacketSent;
                }
            }

            /// <summary>
            /// The amount of time the TransportContext has been receiving packets.
            /// </summary>
            public TimeSpan TimeReceiving
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return IsDisposed || m_FirstPacketReceived.Equals(DateTime.MinValue) ?
                        Extensions.TimeSpanExtensions.InfiniteTimeSpan
                        :
                        DateTime.UtcNow - m_FirstPacketReceived;
                }
            }

            ///// <summary>
            ///// Indicates if the context has been Sending or Receiving for more time then allowed.
            ///// </summary>
            //public bool MediaEnded
            //{
            //    get
            //    {
            //        return !IsContinious && TimeSending == Extensions.TimeSpanExtensions.InfiniteTimeSpan && TimeReceiving == TimeSending;
            //    }
            //}

            /// <summary>
            /// The time at which the media starts
            /// </summary>
            public TimeSpan MediaStartTime
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_StartTime; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set { m_StartTime = value; }
            }

            /// <summary>
            /// The time at which the media ends
            /// </summary>
            public TimeSpan MediaEndTime
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_EndTime; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set { m_EndTime = value; }
            }

            public TimeSpan MediaDuration
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsContinious ? TimeSpanExtensions.InfiniteTimeSpan : m_EndTime - m_StartTime; }
            }

            /// <summary>
            /// Indicates if the <see cref="MediaEndTime"/> is <see cref="Extensions.TimeSpanExtensions.InfiniteTimeSpan"/>. (Has no determined end time)
            /// </summary>
            public bool IsContinious
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_EndTime == Extensions.TimeSpanExtensions.InfiniteTimeSpan; }
            }

            /// <summary>
            /// <see cref="Extensions.TimeSpanExtensions.InfiniteTimeSpan"/> if <see cref="IsContinious"/>,
            /// othewise the amount of time remaining in the media.
            /// </summary>
            public TimeSpan TimeRemaining
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsContinious ? m_EndTime : TimeSpan.FromTicks(m_EndTime.Ticks - (Math.Max(TimeReceiving.Ticks, TimeSending.Ticks))); }
            }

            /// <summary>
            /// Allows getting or setting of the interval which occurs between data transmissions
            /// </summary>
            public TimeSpan SendInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_SendInterval; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set { m_SendInterval = value; }
            }

            /// <summary>
            /// Allows gettings or setting of the interval which occurs between data receptions
            /// </summary>
            public TimeSpan ReceiveInterval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_ReceiveInterval; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set { m_ReceiveInterval = value; }
            }

            /// <summary>
            /// Gets the time in which in TranportContext was last active for a send or receive operation
            /// </summary>
            public TimeSpan InactiveTime
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_InactiveTime; }
            }


            /// <summary>
            /// Gets the time in which the last Rtcp reports were sent.
            /// </summary>
            public TimeSpan LastRtcpReportSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return m_LastRtcpOut.Equals(DateTime.MinValue) ? TimeSpan.MinValue : DateTime.UtcNow - m_LastRtcpOut;
                }
            }

            /// <summary>
            /// Gets the time in which the last Rtcp reports were received.
            /// </summary>
            public TimeSpan LastRtcpReportReceived
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return m_LastRtcpIn.Equals(DateTime.MinValue) ? TimeSpan.MinValue : DateTime.UtcNow - m_LastRtcpIn;
                }
            }

            /// <summary>
            /// Gets the time in which the last RtpPacket was received.
            /// </summary>
            public TimeSpan LastRtpPacketReceived
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return m_LastRtpIn.Equals(DateTime.MinValue) ? TimeSpan.MinValue : DateTime.UtcNow - m_LastRtpIn;
                }
            }

            /// <summary>
            /// Gets the time in which the last RtpPacket was transmitted.
            /// </summary>
            public TimeSpan LastRtpPacketSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return m_LastRtpOut.Equals(DateTime.MinValue) ? TimeSpan.MinValue : DateTime.UtcNow - m_LastRtpOut;
                }
            }

            /// <summary>
            /// Gets the time since <see cref="Initialize was called."/>
            /// </summary>
            public TimeSpan TimeActive
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return m_Initialized.Equals(DateTime.MinValue) ? TimeSpan.MinValue : DateTime.UtcNow - m_Initialized;
                }
            }

            /// <summary>
            /// Indicates the amount of times a failure has occured when sending RtcpPackets
            /// </summary>
            public int FailedRtcpTransmissions
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_FailedRtcpTransmissions; }
            }

            /// <summary>
            /// Indicates the amount of times a failure has occured when sending RtpPackets
            /// </summary>
            public int FailedRtpTransmissions
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_FailedRtpTransmissions; }
            }

            /// <summary>
            /// Indicates the amount of times a failure has occured when receiving RtcpPackets
            /// </summary>
            public int FailedRtcpReceptions
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_FailedRtcpReceptions; }
            }

            /// <summary>
            /// Indicates the amount of times a failure has occured when receiving RtpPackets
            /// </summary>
            public int FailedRtpReceptions
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return m_FailedRtpReceptions; }
            }

            /// <summary>
            /// Corresponds to the ID used by remote systems to identify this TransportContext, a table might be necessary if you want to use a different id in different places
            /// </summary>
            public int SynchronizationSourceIdentifier
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            /// <summary>
            /// Corresponds to the ID used to identify remote parties.            
            /// Use a <see cref="Conference"/> if the size of the group or its members should be limited in some capacity.
            /// </summary>
            public int RemoteSynchronizationSourceIdentifier
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            /// <summary>
            /// MediaDescription which contains information about the type of Media on the Interleave
            /// </summary>
            public Sdp.MediaDescription MediaDescription
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set;
            }

            /// <summary>
            /// Determines if the source has recieved at least <see cref="MinimumSequentialValidRtpPackets"/> RtpPackets
            /// </summary>
            public /*virtual*/ bool IsValid
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return ValidRtpPacketsReceived >= MinimumSequentialValidRtpPackets; }
            }

            /// <summary>
            /// Indicates if the Rtcp is enabled and the <see cref="LocalRtp"/> is equal to the <see cref="LocalRtcp"/>
            /// </summary>
            public bool LocalMultiplexing
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed || IsRtcpEnabled.Equals(false) || object.ReferenceEquals(LocalRtp, null) ? false : LocalRtp.Equals(LocalRtcp); }
            }

            /// <summary>
            /// Indicates if the Rtcp is enabled and the <see cref="RemoteRtp"/> is equal to the <see cref="RemoteRtcp"/>
            /// </summary>
            public bool RemoteMultiplexing
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed || IsRtcpEnabled.Equals(false) || object.ReferenceEquals(RemoteRtp, null) ? false : RemoteRtp.Equals(RemoteRtcp); }
            }

            /// <summary>
            /// <c>false</c> if NOT [RtpEnabled AND RtcpEnabled] AND [LocalMultiplexing OR RemoteMultiplexing]
            /// </summary>
            public bool IsDuplexing
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    if (IsDisposed) return false;

                    return (IsRtpEnabled && IsRtcpEnabled) && (LocalMultiplexing || RemoteMultiplexing);
                }
            }

            /// <summary>
            /// The last <see cref="ReceiversReport"/> sent or received by this RtpClient.
            /// </summary>
            public ReceiversReport ReceiversReport
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            /// <summary>
            /// The last <see cref="SendersReport"/> sent or received by this RtpClient.
            /// </summary>
            public SendersReport SendersReport
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            /// The last <see cref="SourceDescriptionReport"/> sent or received by this RtpClient.
            public SourceDescriptionReport SourceDescription
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            /// The last <see cref="GoodbyeReport"/> sent or received by this RtpClient.
            public GoodbyeReport Goodbye
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            /// <summary>
            /// The total amount of packets (both Rtp and Rtcp) receieved
            /// </summary>
            public long TotalPacketsReceived
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return RtpPacketsReceived + RtcpPacketsReceived; }
            }

            /// <summary>
            /// The total amount of packets (both Rtp and Rtcp) sent
            /// </summary>
            public long TotalPacketsSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return RtpPacketsSent + RtcpPacketsSent; }
            }

            /// <summary>
            /// The total amount of RtpPackets sent
            /// </summary>
            public long TotalRtpPacketsSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpPacketsSent; }
            }

            /// <summary>
            /// The amount of bytes in all rtp packets payloads which have been sent.
            /// </summary>
            public long RtpPayloadBytesSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpBytesSent; }
            }

            /// <summary>
            /// The amount of bytes in all rtp packets payloads which have been received.
            /// </summary>
            public long RtpPayloadBytesRecieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpBytesRecieved; }
            }

            /// <summary>
            /// The total amount of bytes related to Rtp sent (including headers)
            /// </summary>
            public long TotalRtpBytesSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpBytesSent + RtpHeader.Length * RtpPacketsSent; }
            }

            /// <summary>
            /// The total amount of bytes related to Rtp received
            /// </summary>
            public long TotalRtpBytesReceieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpBytesRecieved + RtpHeader.Length * RtpPacketsSent; }
            }

            /// <summary>
            /// The total amount of RtpPackets received
            /// </summary>
            public long TotalRtpPacketsReceieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtpPacketsReceived; }
            }

            /// <summary>
            /// The total amount of RtcpPackets recieved
            /// </summary>
            public long TotalRtcpPacketsSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtcpPacketsSent; }
            }

            /// <summary>
            /// The total amount of sent bytes related to Rtcp 
            /// </summary>
            public long TotalRtcpBytesSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtcpBytesSent; }
            }

            /// <summary>
            /// The total amount of received bytes (both Rtp and Rtcp) received
            /// </summary>
            public long TotalBytesReceieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : TotalRtcpBytesReceieved + TotalRtpBytesReceieved; }
            }

            /// <summary>
            /// The total amount of received bytes (both Rtp and Rtcp) sent
            /// </summary>
            public long TotalBytesSent
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : TotalRtcpBytesSent + TotalRtpBytesSent; }
            }

            /// <summary>
            /// The total amount of RtcpPackets received
            /// </summary>
            public long TotalRtcpPacketsReceieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtcpPacketsReceived; }
            }

            /// <summary>
            /// The total amount of bytes related to Rtcp received
            /// </summary>
            public long TotalRtcpBytesReceieved
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return IsDisposed ? 0 : RtcpBytesRecieved; }
            }

            /// <summary>            
            /// Gets the sequence number of the last RtpPacket recieved on this channel
            /// </summary>
            public int RecieveSequenceNumber
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return (short)m_SequenceNumber; }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set { m_SequenceNumber = (ushort)value; }
            }

            public int SendSequenceNumber
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return (short)m_LastSentSequenceNumber; }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal protected set { m_LastSentSequenceNumber = (ushort)value; }
            }

            public int RtpTimestamp
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            public int SenderRtpTimestamp
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            /// <summary>
            /// The NtpTimestamp from the last SendersReport recieved or created
            /// </summary>
            public long NtpTimestamp
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            public long SenderNtpTimestamp
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal set;
            }

            //Allows for time difference between the source and the client when issuing reports, will be added to any NtpTimestamp created.

            public long NtpOffset
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            public long SenderNtpOffset
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set;
            }

            #endregion

            #region Constructor

            /// <summary>
            /// Creates a TransportContext from the given parameters
            /// </summary>
            /// <param name="dataChannel"></param>
            /// <param name="controlChannel"></param>
            /// <param name="ssrc"></param>
            /// <param name="rtcpEnabled"></param>
            /// <param name="senderSsrc"></param>
            /// <param name="minimumSequentialRtpPackets"></param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TransportContext(byte dataChannel, byte controlChannel, int ssrc, bool rtcpEnabled = true, int senderSsrc = 0, int minimumSequentialRtpPackets = 2, Action<System.Net.Sockets.Socket> configure = null, bool shouldDispose = true)
                : base(shouldDispose)
            {
                //MaximumRemoteIdentities = 1;

                //Todo, threshold?
                AllowOutOfOrderPackets = true;

                if (dataChannel.Equals(controlChannel)) throw new InvalidOperationException("dataChannel and controlChannel must be unique.");

                if (ssrc.Equals(senderSsrc) && false.Equals(ssrc.Equals(0))) throw new InvalidOperationException("ssrc and senderSsrc must be unique.");

                if (minimumSequentialRtpPackets < 0) throw new InvalidOperationException("minimumSequentialRtpPackets must be >= 0");

                DataChannel = dataChannel;

                ControlChannel = controlChannel;

                SynchronizationSourceIdentifier = ssrc;

                IsRtcpEnabled = rtcpEnabled;

                //If 0 then all packets are answered
                RemoteSynchronizationSourceIdentifier = senderSsrc;

                //MinimumSequentialValidRtpPackets should be equal to 0 when RemoteSynchronizationSourceIdentifier is null I think, this essentially means respond to all inquiries.
                //A confrence may be able to contain this type of behavior better if required.
                MinimumSequentialValidRtpPackets = minimumSequentialRtpPackets;

                //Default bandwidth restriction
                MaximumRtcpBandwidthPercentage = DefaultReportInterval.TotalSeconds;

                //Assign the function responsible for configuring the socket
                ConfigureSocket = configure ?? ConfigureRtpRtcpSocket;

                //Use the default unless assigned after creation
                RecieveBufferSizeMultiplier = DefaultRecieveBufferSizeMultiplier;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TransportContext(byte dataChannel, byte controlChannel, int ssrc, Sdp.MediaDescription mediaDescription, bool rtcpEnabled = true, int senderSsrc = 0, int minimumSequentialRtpPackets = 2, bool shouldDispose = true)
                : this(dataChannel, controlChannel, ssrc, rtcpEnabled, senderSsrc, minimumSequentialRtpPackets, null, shouldDispose)
            {
                MediaDescription = mediaDescription;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TransportContext(byte dataChannel, byte controlChannel, int ssrc, Sdp.MediaDescription mediaDescription, Socket socket, bool rtcpEnabled = true, int senderSsrc = 0, int minimumSequentialRtpPackets = 2, bool shouldDispose = true)
                : this(dataChannel, controlChannel, ssrc, mediaDescription, rtcpEnabled, senderSsrc, minimumSequentialRtpPackets, shouldDispose)
            {
                RtpSocket = RtcpSocket = socket;
            }

            #endregion

            #region Methods

            /// <summary>
            /// Assigns a Non Zero value to <see cref="SynchronizationSourceIdentifier"/> to a random value based on the given seed.
            /// The value will also be different than <see cref="RemoteSynchronizationSourceIdentifier"/>.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected void AssignIdentity(int seed = SendersReport.PayloadType)
            {
                if (SynchronizationSourceIdentifier.Equals(Binary.Zero))
                {
                    //Generate the id per RFC3550
                    do SynchronizationSourceIdentifier = RFC3550.Random32(seed);
                    while (SynchronizationSourceIdentifier.Equals(Binary.Zero) || SynchronizationSourceIdentifier.Equals(RemoteSynchronizationSourceIdentifier));
                }
            }

            /// <summary>
            /// Calculates RTP Interarrival Jitter as specified in RFC 3550 6.4.1.
            /// </summary>
            /// <param name="packet">RTP packet.</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void UpdateJitterAndTimestamp(RtpPacket packet)//bool sent
            {
                //Determine to update sent or received values
                bool sentPacket = packet.Transferred.HasValue;

                // RFC 3550 A.8.
                //Determine the time the last packet was sent or received
                TimeSpan arrivalDifference = (sentPacket ? LastRtpPacketSent : LastRtpPacketReceived);

                if (sentPacket)
                {
                    RFC3550.CalulcateJitter(ref arrivalDifference, ref SenderJitter, ref SenderTransit);

                    //Update the Sender RtpTimestamp on the Context
                    SenderRtpTimestamp = packet.Timestamp;

                    //Update the Sender NtpTimestamp on the Context.
                    SenderNtpTimestamp = (long)NetworkTimeProtocol.DateTimeToNptTimestamp(packet.Transferred ?? packet.Created);
                }
                else //Handle as a recieved packet
                {

                    RFC3550.CalulcateJitter(ref arrivalDifference, ref RtpJitter, ref RtpTransit);

                    //Update the RtpTimestamp on the Context
                    RtpTimestamp = packet.Timestamp;

                    //Update the NtpTimestamp on the Context.
                    NtpTimestamp = (long)NetworkTimeProtocol.DateTimeToNptTimestamp(sentPacket ? packet.Transferred.Value : packet.Created);
                }

                //Context is not inactive.
                m_InactiveTime = Extensions.TimeSpanExtensions.InfiniteTimeSpan;
            }

            /// <summary>
            /// Resets the variables used in packets validation based on the given parameter.
            /// </summary>
            /// <param name="sequenceNumber">The sequence number to reset to.</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ResetRtpValidationCounters(int sequenceNumber)
            {
                ushort val = (ushort)sequenceNumber;

                RFC3550.ResetRtpValidationCounters(ref val, ref RtpBaseSeq, ref RtpMaxSeq, ref RtpBadSeq, ref RtpSeqCycles, ref RtpReceivedPrior, ref ValidRtpPacketsReceived);
            }

            /// <summary>
            /// Performs checks on the packet which can be overriden in a derrived implementation
            /// </summary>
            /// <param name="packet"></param>
            /// <returns></returns>
            public virtual bool ValidatePacketAndUpdateSequenceNumber(RtpPacket packet) //out int codeReason
            {

                /*NOTE : 
                 * http://www.ietf.org/rfc/rfc3551.txt
                 * 
                  Static payload type 13 was assigned to the Comfort Noise (CN) payload format defined in RFC 3389.  
                  Payload type 19 was marked reserved because it had been temporarily allocated to an earlier version of Comfort Noise
                  present in some draft revisions of this document.
                 */

                //If there is no Payload return, this prevents injection by utilizing just a RtpHeader which happens to be valid.
                //I can think of no good reason to allow this in this implementation, if required dervive and ensure that RTCP is not better suited for whatever is being done.
                //The underlying goto CheckSequenceNumber is what is used to performed this check currently.
                //if (payloadLength == 0 && packet.PayloadType != 13) return false;
                //else if (packet.PayloadType == 13  || packet.PayloadType == 19) goto CheckSequenceNumber;

                if (packet.Header.IsCompressed || packet.PayloadType.Equals(13)) goto CheckSequenceNumber;

                // RFC 3550 A.1. Notes: Each TransportContext instance may be better suited to have a structure which defines this logic.

                //o  RTP version field must equal 2.

                if (false.Equals(packet.Version.Equals(Version))) return false;

                //o  The payload type must be known, and in particular it must not be equal to SR or RR.

                int check = packet.PayloadType;

                //Check the payload type is known and not equal to sr or rr.
                if (check.Equals(SendersReport.PayloadType) || check.Equals(ReceiversReport.PayloadType) || false.Equals(MediaDescription.PayloadTypes.Contains(check))) return false;

                //Space complex
                int payloadLength = packet.Payload.Count;

                //o  If the P bit is set, Padding must be less than the total packet length minus the header size.
                if (packet.Padding && payloadLength > 0 && packet.PaddingOctets >= payloadLength) return false;

                check = packet.ContributingSourceCount;

                ///  o  The length of the packet must be consistent with CC and payload type (if payloads have a known length this is checked with the IsComplete property).
                if (check > 0 && payloadLength < check * Binary.BytesPerInteger) return false;

                //Only performed to ensure validity
                if (packet.Extension)
                {
                    //o  The X bit must be zero if the profile does not specify that the
                    //   header extension mechanism may be used.  
                    //   Otherwise, the extension
                    //   length field must be less than the total packet size minus the
                    //   fixed header length and padding.

                    //Read the amount of paddingOctets
                    check = packet.PaddingOctets;

                    //Ensure the padding is valid first
                    if (check >= payloadLength) return false;

                    //Ensure the above is also true.
                    if (packet.ExtensionOctets > payloadLength - check) return false;
                }

                #region Notes on RFC3550 Implementation

                /*
                      The validity check can be made stronger requiring more than two
                        packets in sequence.  The disadvantages are that a larger number of
                        initial packets will be discarded (or delayed in a queue) and that
                        high packet loss rates could prevent validation.  However, because
                        the RTCP header validation is relatively strong, if an RTCP packet is
                        received from a source before the data packets, the count could be
                        adjusted so that only two packets are required in sequence.  If
                        initial data loss for a few seconds can be tolerated, an application
                        MAY choose to discard all data packets from a source until a valid
                        RTCP packet has been received from that source.
                     * 
                     * Please Note: This is why packets are stored in the CurrentFrame of the TransportContext. (To avoid loss where possible)
                     * A property exists for disabling the handling of RtpPackets which are incoming or outgoing.
                     * 
                     * Derived implementations may want to perform additional checks noted below inter alia.
                     * 
                     Depending on the application and encoding, algorithms may exploit
                       additional knowledge about the payload format for further validation.
                       For payload types where the timestamp increment is the same for all
                       packets, 
                     * the timestamp values can be predicted from the previous                  ------ Note:
                       packet received from the same source using the sequence number           ------ The source is not valid until MIN_SEQUENTIAL have been received.
                       difference (assuming no change in payload type).                         ------ This implementation maskes no assumptions about the Timestamp property.

                       A strong "fast-path" check is possible since with high probability       ------ Note:
                       the first four octets in the header of a newly received RTP data         ------  This implementation is engineered with the state of mind that certain profiles
                       packet will be just the same as that of the previous packet from the     ------  may REQUIRE that Padding or Extensions only be present in RtpPacket N of a RtpFrame X
                       same SSRC except that the sequence number will have increased by one.    ------  Thus this check is NOT performed. The SequenceNumber of the TransportContext is assigned in the HandleIncomingRtpPacket function AFTER the sender is valid.

                     * Similarly, a single-entry cache may be used for faster SSRC lookups      ------ Note: This implementation utilizes the single-entry cache once MIN_SEQUENTIAL have been received.
                       in applications where data is typically received from one source at a    ------ In scenarios with more then 1 participant is required a Conference class is used.
                       time.
                     */

                #endregion

                CheckSequenceNumber:

                check = packet.SequenceNumber;

                //Return the result of processing the verification of the sequence number according the RFC3550 A.1
                if (UpdateSequenceNumber(ref check))
                {
                    //Update the SequenceNumber
                    RecieveSequenceNumber = check;

                    return true;
                }
                else if (check < RecieveSequenceNumber) return true;

                return false;
            }


            public bool UpdateSequenceNumber(int sequenceNumber) //,bool probe = false
            {
                return UpdateSequenceNumber(ref sequenceNumber);
            }

            /// <summary>
            /// Performs checks in accorance with RFC3550 A.1 and returns a value indicating if the given sequence number is in state.
            /// </summary>
            /// <param name="sequenceNumber">The sequenceNumber to check.</param>
            /// <returns>True if in state, otherwise false.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            [System.CLSCompliant(false)]
            public bool UpdateSequenceNumber(ref int sequenceNumber)
            {
                ushort val = (ushort)sequenceNumber;

                /*bool result = */
                return RFC3550.UpdateSequenceNumber(ref val,
 ref RtpBaseSeq, ref RtpMaxSeq, ref RtpBadSeq,
 ref RtpSeqCycles, ref RtpReceivedPrior, ref RtpProbation,
 ref ValidRtpPacketsReceived,
 ref MinimumSequentialValidRtpPackets, ref MaxMisorder, ref MaxDropout);

                //return result;
            }

            /// <summary>
            /// Randomizes the SequenceNumber
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void RandomizeSequenceNumber()
            {
                RecieveSequenceNumber = Utility.Random.Next();
            }

            #region Initialize            

            //Todo allow for Leave Open...

            /// <summary>
            /// Creates the required Udp sockets for the TransportContext and updates the assoicated Properties and Fields
            /// </summary>
            /// <param name="localIp"></param>
            /// <param name="remoteIp"></param>
            /// <param name="localRtpPort"></param>
            /// <param name="localRtcpPort"></param>
            /// <param name="remoteRtpPort"></param>
            /// <param name="remoteRtcpPort"></param>
            /// <param name="punchHole"></param>
            /// <notes>
            /// Attention Freebox Stb Users!!!! -- Todo make an option to allow on the first receive to adjust port?
            /// Please use 0 For remoteRtpPort and remoteRtcpPort as the Freebox Stb does not use the correct Rtp or Rtcp ports indicated in the Describe request.
            /// </notes>
            public void Initialize(IPAddress localIp, IPAddress remoteIp, int localRtpPort, int localRtcpPort, int remoteRtpPort = 0, int remoteRtcpPort = 0, bool punchHole = true)
            {
                Initialize(new IPEndPoint(localIp, localRtpPort), new IPEndPoint(remoteIp, remoteRtpPort), new IPEndPoint(localIp, localRtcpPort), new IPEndPoint(remoteIp, remoteRtcpPort), punchHole);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Initialize(IPEndPoint localRtp, IPEndPoint remoteRtp, IPEndPoint localRtcp, IPEndPoint remoteRtcp, bool punchHole = true)
            {
                if (IsDisposed || IsActive) return;

                m_Initialized = DateTime.UtcNow;

                if (localRtp.Address.AddressFamily.Equals(remoteRtp.Address.AddressFamily).Equals(false)) TaggedExceptionExtensions.RaiseTaggedException<TransportContext>(this, "localIp and remoteIp AddressFamily must match.");
                else if (punchHole) punchHole = IPAddressExtensions.IsOnIntranet(remoteRtp.Address).Equals(false); //Only punch a hole if the remoteIp is not on the LAN by default.

                //Erase previously set values on the TransportContext.
                //RtpBytesRecieved = RtpBytesSent = RtcpBytesRecieved = RtcpBytesSent = 0;

                //Set now if not already set
                AssignIdentity();

                try
                {
                    //Create the RtpSocket
                    RtpSocket = new Socket(localRtp.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                    //Configure it
                    ConfigureSocket(RtpSocket);

                    //Apply the send and receive timeout based on the ReceiveInterval
                    RtpSocket.SendTimeout = RtpSocket.ReceiveTimeout = (int)(ReceiveInterval.TotalMilliseconds) >> 1;

                    LocalRtp = localRtp;

                    RemoteRtp = remoteRtp;

                    try
                    {
                        //Assign the LocalRtp EndPoint and Bind the socket to that EndPoint
                        RtpSocket.Bind(LocalRtp);

                        //Assign the RemoteRtp EndPoint and Bind the socket to that EndPoint
                        RtpSocket.Connect(RemoteRtp);
                    }
                    catch
                    {
                        //Can't bind or connect
                    }

                    ////Handle Multicast joining (Might need to track interface)
                    //if (Common.Extensions.IPEndPointExtensions.IsMulticast(remoteRtp))
                    //{
                    //    SocketExtensions.JoinMulticastGroup(RtpSocket, remoteRtp.Address, ttl);
                    //}

                    //Determine if holepunch is required

                    //Todo, have option NOT to use 0

                    //Todo, send reports, don't use proprietary messages
                    if (punchHole)
                    {
                        //Send some bytes to ensure the result is open, if we get a SocketException the port is closed
                        //new RtpPacket(Version, false, false, false, MediaDescription.MediaFormat, SynchronizationSourceIdentifier, RemoteSynchronizationSourceIdentifier ?? 0, 0, 0, null);
                        try { RtpSocket.SendTo(WakeUpBytes, 0, WakeUpBytes.Length, SocketFlags.None, RemoteRtp); }
                        catch (SocketException)
                        {
                            //The port was not open, allow the next recieve to determine the port
                            RemoteRtp = new IPEndPoint(((IPEndPoint)RemoteRtp).Address, 0);
                        }//We don't care about the response or any issues during the holePunch
                    }

                    //If Duplexing Rtp and Rtcp (on the same socket)
                    if (remoteRtp.Equals(remoteRtcp))
                    {
                        RtcpSocket = RtpSocket;
                    }
                    else if (IsRtcpEnabled)
                    {
                        //Create the RtcpSocket
                        RtcpSocket = new Socket(localRtp.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                        //Configure it
                        ConfigureSocket(RtcpSocket);

                        //Apply the send and receive timeout based on the ReceiveInterval
                        RtcpSocket.SendTimeout = RtcpSocket.ReceiveTimeout = (int)(ReceiveInterval.TotalMilliseconds) >> 1;

                        LocalRtcp = localRtcp;

                        RemoteRtcp = remoteRtcp;

                        try
                        {
                            //Assign the LocalRtcp EndPoint and Bind the socket to that EndPoint
                            RtcpSocket.Bind(LocalRtcp);

                            //Assign the RemoteRtcp EndPoint and Bind the socket to that EndPoint
                            RtcpSocket.Connect(RemoteRtcp);
                        }
                        catch
                        {
                            //Can't bind or connect
                        }

                        //Todo, send reports, don't use proprietary messages

                        if (punchHole)
                        {
                            //new RtcpPacket(Version, Rtcp.ReceiversReport.PayloadType, 0, 0, SynchronizationSourceIdentifier, 0);
                            try { RtcpSocket.SendTo(WakeUpBytes, 0, WakeUpBytes.Length, SocketFlags.None, RemoteRtcp); }
                            catch (SocketException)
                            {
                                //The port was not open, allow the next recieve to determine the port
                                RemoteRtcp = new IPEndPoint(((IPEndPoint)RemoteRtcp).Address, 0);
                            }
                        }
                    }

                    //Setup the receive buffer size for all sockets of this context to use memory defined in excess of the context memory to ensure a high receive rate in udp
                    if (RecieveBufferSizeMultiplier >= 0 &&
                        false.Equals(IDisposedExtensions.IsNullOrDisposed(ContextMemory)) &&
                        ContextMemory.Count > 0)
                    {
                        //Ensure the receive buffer size is updated for that context.
                        ISocketReferenceExtensions.SetReceiveBufferSize(((ISocketReference)this), RecieveBufferSizeMultiplier * ContextMemory.Count);
                    }

                }
                catch
                {
                    throw;
                }
            }

            #region Tcp

            /// <summary>
            /// Creates the required Tcp socket for the TransportContext and updates the assoicated Properties and Fields
            /// </summary>
            /// <param name="localIp"></param>
            /// <param name="remoteIp"></param>
            /// <param name="remotePort"></param>
            public void Initialize(IPAddress localIp, IPAddress remoteIp, int remotePort)
            {
                Initialize(new IPEndPoint(localIp, remotePort), new IPEndPoint(remoteIp, remotePort));
            }

            /// <summary>
            /// Creates a Tcp socket on from local to remote and sets the RtpSocket and RtcpSocket to that socket.
            /// </summary>
            /// <param name="local"></param>
            /// <param name="remote"></param>
            public void Initialize(IPEndPoint local, IPEndPoint remote)
            {
                LocalRtp = local;

                RemoteRtp = remote;

                Socket socket = new Socket(local.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                ConfigureSocket(socket);

                Initialize(socket);

                //This reference is not needed anymore
                socket = null;
            }

            /// <summary>
            /// Uses the given socket for the duplexed data
            /// </summary>
            /// <param name="duplexed">The socket to use</param>
            public void Initialize(Socket duplexed)
            {
                if (IsDisposed || IsActive) return;

                //If the socket is not exclusively using the address
                if (false.Equals(duplexed.ExclusiveAddressUse))
                {
                    //Duplicte the socket's type for a Rtcp socket.
                    Socket rtcpSocket = new Socket(duplexed.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    //Configure the duplicate
                    ConfigureSocket(rtcpSocket);

                    //Initialize with the duplicate socket
                    Initialize(duplexed, rtcpSocket);

                    //Should log, can't initialize
                    if (IsActive.Equals(false)) rtcpSocket.Dispose();

                    //This reference is no longer needed.
                    rtcpSocket = null;
                }
                else Initialize(duplexed, duplexed); //Otherwise use the existing socket twice
            }

            #endregion

            #region Existing Sockets (Could be Mixed Tcp and Udp)

            /// <summary>
            /// Used to provide sockets which are already bound and connected for use in rtp and rtcp operations
            /// </summary>
            /// <param name="rtpSocket"></param>
            /// <param name="rtcpSocket"></param>
            //TODO Must allow leaveOpen for existing sockets
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Initialize(Socket rtpSocket, Socket rtcpSocket)
            {
                if (IsDisposed || IsActive) return;

                if (object.ReferenceEquals(rtpSocket, null)) throw new ArgumentNullException("rtpSocket");

                //Maybe should just be set to the rtpSocket?
                if (object.ReferenceEquals(rtcpSocket, null)) throw new ArgumentNullException("rtcpSocket");

                //RtpBytesRecieved = RtpBytesSent = RtcpBytesRecieved = RtcpBytesSent = 0;

                m_Initialized = DateTime.UtcNow;

                RtpSocket = rtpSocket;

                RtpSocket.SendTimeout = RtpSocket.ReceiveTimeout = (int)(ReceiveInterval.TotalMilliseconds) >> 1;

                bool punchHole = false.Equals(RtpSocket.ProtocolType == ProtocolType.Tcp) && false.Equals(IPAddressExtensions.IsOnIntranet(((IPEndPoint)RtpSocket.RemoteEndPoint).Address)); //Only punch a hole if the remoteIp is not on the LAN by default.

                if (object.ReferenceEquals(RemoteRtp, null)) RemoteRtp = RtpSocket.RemoteEndPoint;

                if (object.ReferenceEquals(LocalRtp, null))
                {
                    LocalRtp = RtpSocket.LocalEndPoint;

                    if (false.Equals(RtpSocket.IsBound))
                    {
                        RtpSocket.Bind(LocalRtp);
                    }

                    if (false.Equals(RtpSocket.Connected))
                    {
                        try { RtpSocket.Connect(RemoteRtp); }
                        catch { /*Only tcp must succeed*/ }
                    }
                }

                //If a different socket is used for rtcp configure it also
                if (object.ReferenceEquals((RtcpSocket = rtcpSocket), null).Equals(false))
                {
                    //If the socket is not the same as the RtcpSocket configure it also
                    if ((RtpSocket.Handle == RtcpSocket.Handle).Equals(false))
                    {
                        RtcpSocket.SendTimeout = RtcpSocket.ReceiveTimeout = (int)(ReceiveInterval.TotalMilliseconds) >> 1;

                        LocalRtcp = RtcpSocket.LocalEndPoint;

                        RemoteRtcp = RtcpSocket.RemoteEndPoint;

                        if (object.ReferenceEquals(LocalRtcp, null).Equals(false) && false.Equals(RtcpSocket.IsBound)) RtcpSocket.Bind(LocalRtcp);

                        if (object.ReferenceEquals(RemoteRtcp, null).Equals(false) && false.Equals(RtcpSocket.Connected)) try { RtcpSocket.Connect(RemoteRtcp); }
                            catch { /*Only tcp must succeed*/ }
                    }
                    else
                    {
                        //Just assign the same end points from the rtp socket.

                        if (object.ReferenceEquals(LocalRtcp, null)) LocalRtcp = LocalRtp;

                        if (object.ReferenceEquals(RemoteRtcp, null)) RemoteRtcp = RemoteRtp;
                    }
                }
                else RtcpSocket = RtpSocket;

                //Todo, send reports, don't use proprietary messages
                if (punchHole)
                {
                    //new RtcpPacket(Version, Rtcp.ReceiversReport.PayloadType, 0, 0, SynchronizationSourceIdentifier, 0);

                    try { RtpSocket.SendTo(WakeUpBytes, 0, WakeUpBytes.Length, SocketFlags.None, RemoteRtp); }
                    catch (SocketException) { }//We don't care about the response or any issues during the holePunch

                    //Check for the same socket, don't send more than 1 wake up sequence to a unique socket.
                    //IntPtr doesn't expose Equals(IntPtr), it's because it would be possible to be either an int or long but it should be able to be done against it's own type... its only void* anyway...
                    //Todo, PtrCompare?
                    if (RtpSocket.Handle == RtcpSocket.Handle) return;

                    try { RtcpSocket.SendTo(WakeUpBytes, 0, WakeUpBytes.Length, SocketFlags.None, RemoteRtcp); }
                    catch (SocketException) { }//We don't care about the response or any issues during the holePunch
                }

                AssignIdentity();

                Goodbye = null;
            }

            #endregion

            #endregion

            //Todo Seperate Initialize and Connect and PunchHole (come up with a better name for PunchHole)

            //Todo HandleException / Event

            /// <summary>
            /// Receives data on the given socket
            /// </summary>
            /// <param name="buffer"></param>
            /// <param name="offset"></param>
            /// <param name="count"></param>
            /// <param name="socket"></param>
            /// <param name="remote"></param>
            /// <returns>The amount of bytes received</returns>

            /// <summary>
            /// Closes the Rtp and Rtcp Sockets
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void DisconnectSockets()
            {
                if (IsActive.Equals(false) || IsDisposed) return;

                if (LeaveOpen)
                {
                    //Maybe should drop multicast group....

                    RtpSocket = RtcpSocket = null;
                }
                else
                {
                    foreach (System.Net.IPAddress groupAddress in MulticastGroups)
                    {
                        //Todo, TryLeave. If false shutdown is coming anyway...
                        SocketExtensions.LeaveMulticastGroup(RtpSocket, groupAddress);
                    }

                    MulticastGroups.Clear();

                    //For Udp the RtcpSocket may be the same socket as the RtpSocket if the sender/reciever is duplexing
                    if (object.ReferenceEquals(RtcpSocket, null).Equals(false) && RtpSocket.Handle.Equals(RtcpSocket.Handle).Equals(false)) RtcpSocket.Close();

                    //Close the RtpSocket
                    if (object.ReferenceEquals(RtpSocket, null).Equals(false)) RtpSocket.Close();

                    RtpSocket = RtcpSocket = null;
                }

                //Remove the end points
                LocalRtp = LocalRtcp = RemoteRtp = RemoteRtcp = null;

                //Why erase stats?
                //m_FirstPacketReceived = DateTime.MinValue;

                //m_FirstPacketSent = DateTime.MinValue;
            }

            //Usually called when a ssrc collision occurs 

            /// <summary>
            /// Resets the RemoteSynchronizationSourceIdentifier and packet counters values.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void ResetState()
            {
                //if (RemoteSynchronizationSourceIdentifier.HasValue) RemoteSynchronizationSourceIdentifier = null;// default(int);

                RemoteSynchronizationSourceIdentifier = 0;

                //Set all to 0
                RfcRtpBytesSent = RtpPacketsSent = RtpBytesSent = RtcpPacketsSent =
                    RtcpBytesSent = RtpPacketsReceived = RtpBytesRecieved = RtcpBytesRecieved =
                        RtcpPacketsReceived = m_FailedRtcpTransmissions = m_FailedRtpTransmissions = m_FailedRtcpReceptions = m_FailedRtpReceptions = 0;
            }

            /// <summary>
            /// Disposes the TransportContext and all underlying resources.
            /// </summary>
            public override void Dispose()
            {
                if (IsDisposed) return;

                base.Dispose();

                //If the instance should dispose
                if (ShouldDispose)
                {
                    //Disconnect sockets
                    DisconnectSockets();

                    //Remove references to the context memory and the application context
                    ContextMemory = null;

                    ApplicationContext = null;
                }
            }

            #endregion

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            IEnumerable<Socket> ISocketReference.GetReferencedSockets()
            {
                if (IsDisposed) yield break;

                if (object.ReferenceEquals(RtpSocket, null).Equals(false))
                {
                    yield return RtpSocket;

                    if (IsDuplexing || RtpSocket.ProtocolType == ProtocolType.Tcp) yield break;
                }

                //Todo, these may be the same sockets...
                if (object.ReferenceEquals(RtcpSocket, null).Equals(false)) yield return RtcpSocket;
            }
        }

        #endregion
    }
}
