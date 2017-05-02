#region Copyright
/*
This file came from Managed Media Aggregation, You can always find the latest version @ https://net7mma.codeplex.com/
  
 Julius.Friedman@gmail.com / (SR. Software Engineer ASTI Transportation Inc. http://www.asti-trans.com)

Permission is hereby granted, free of charge, 
 * to any person obtaining a copy of this software and associated documentation files (the "Software"), 
 * to deal in the Software without restriction, 
 * including without limitation the rights to :
 * use, 
 * copy, 
 * modify, 
 * merge, 
 * publish, 
 * distribute, 
 * sublicense, 
 * and/or sell copies of the Software, 
 * and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
 * 
 * 
 * JuliusFriedman@gmail.com should be contacted for further details.

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
 * 
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, 
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
 * TORT OR OTHERWISE, 
 * ARISING FROM, 
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * v//
 */
#endregion

#region Using Statements

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oragon.Classes.Disposables;
using Oragon.Classes;
using Oragon.Classes.Binary;
using Oragon.Extensions;
using Oragon.Media.Common.Extensions;

#endregion

namespace Oragon.Media.Rtp
{
    #region RtpHeader

    #region Reference

    /* Copied from http://tools.ietf.org/html/rfc3550#section-5
         
           Schulzrinne, et al.         Standards Track                    [Page 12]
           FFC 3550                          RTP                          July 2003
         5. RTP Data Transfer Protocol

         5.1 RTP Fixed Header Fields

           The RTP header has the following format:

            0                   1                   2                   3
            0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
           +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
           |V=2|P|X|  CC   |M|     PT      |       sequence number         |
           +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
           |                           timestamp                           |
           +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
           |           synchronization source (SSRC) identifier            |
           +=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
           |            contributing source (CSRC) identifiers             |
           |                             ....                              |
           +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
         */

    #endregion

    /// <summary>
    /// Provides a manged abstraction around the first 12 octets in any RtpPacket.
    /// Further information can be found at http://tools.ietf.org/html/rfc3550#section-5
    /// </summary>
    /// <remarks>
    /// Is IDisposeable because the class OWNS a reference to the CommonHeaderBits and a byte[].
    /// </remarks>
    public class RtpHeader : SuppressedFinalizerDisposable, IEnumerable<byte>
    {
        #region Implementation Notes and Reference

        /*
              RTP data packets contain no length field or other delineation,
               therefore RTP relies on the underlying protocol(s) to provide a
               length indication.  The maximum length of RTP packets is limited only by the underlying protocols.
             * 
     ******  (Subsequently by the Maximum Transmittable Unit if applicable of the underlying network which implements the underlying protocol)  ******
             *
               If RTP packets are to be carried in an underlying protocol that
               provides the abstraction of a continuous octet stream rather than
               messages (packets), an encapsulation of the RTP packets MUST be
               defined to provide a framing mechanism.  
             * 
             * Framing is also needed if the underlying protocol may contain padding so that the extent of the
               RTP payload cannot be determined.  
               The framing mechanism is not defined here.
             * 
               http://tools.ietf.org/html/rfc4571 Defined a framining mechanism to which known Errata exists at the time of this implementation.
             * http://www.rfc-editor.org/errata_search.php?rfc=4571
             * 
             * RFC4571 Also states @  [Page 1] (Paragraph 1, the final sentence)
             * 
             * `However, earlier versions of RTP/AVP did define a framing method, and this method is in use in several implementations.`
             * 
             * For reference the earlier version of RTP can be found @ ftp://gaia.cs.umass.edu/pub/hgschulz/rtp/draft-ietf-avt-rtp-04.txt
             * 
             * RFC2336 defined a method similar to that which is used by RFC4571 and can be located @ http://tools.ietf.org/html/rfc2326#section-10.12
             *
             * Specifically the difference is that multiple packets may not be stored in a single interleaved frame 
             * Also that different packet types e.g. Rtp and Rtcp cannot appear in the same frame.
             * 
             */

        #endregion

        #region Constants and Statics

        /// <summary>
        /// The header length (and subsequently) the minimum size of any given RtpPacket.
        /// </summary>
        public const int Length = 12;

        #endregion

        #region Fields

        /// <summary>
        /// A managed abstraction of the first two octets, 16 bits of the RtpHeader.
        /// </summary>
        internal RFC3550.CommonHeaderBits First16Bits;

        /// <summary>
        /// A the last 10 octets of the RtpHeader.
        /// </summary>
        internal byte[] Last10Bytes;

        /// <summary>
        /// The segment which references the <see cref="Last10Bytes"/>
        /// </summary>
        internal MemorySegment SegmentToLast10Bytes;

        #endregion

        #region Properties

        #region Unsafe for Multi Thread Access without prior Synchronization

        /*
             * To provide synchronized access to these contention would be required.
             * It is assumed that consumers of this instance would synchronize their access to these properties during such development.
             * 
             * Arguments for this include the fact that is costs more to syncrhonize than to perform the binary operations themselves.
             * This is also why the propery getters and setters use manual shifting rather than the provided binary implementations when possible.
             * 
             * This class is IDisposable only to ensure there are no memory leaks, the cost of checking for disposition is usually the same as performing the binary opertions and reduces performance.
             * 
             * A Common base class which contains the CheckDispose method, Disposed property and the Dispose implementation may make this implemenation slightly more maintain able.
             * 
             */

        /// <summary>
        /// Gets the Version bit field of the RtpHeader
        /// </summary>
        public int Version
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { /*CheckDisposed();*/ return First16Bits.Version; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.Version = value; }
        }

        /// <summary>
        /// Indicates if the Padding bit is set in the first octet.
        /// </summary>
        public bool Padding
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { /*CheckDisposed();*/ return First16Bits.Padding; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.Padding = value; }
        }

        /// <summary>
        /// Indicates if the Extension bit is set in the first octet.
        /// </summary>
        public bool Extension
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { /*CheckDisposed();*/ return First16Bits.Extension; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.Extension = value; }
        }

        /// <summary>
        /// Gets or sets the nybble associated with the CC field in the RtpHeader.
        /// </summary>
        public int ContributingSourceCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { /*CheckDisposed();*/ return First16Bits.RtpContributingSourceCount; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.RtpContributingSourceCount = value; }
        }

        /// <summary>
        /// Indicates if the Marker bit is set.
        /// </summary>
        public bool Marker
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { /*CheckDisposed();*/ return First16Bits.RtpMarker; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.RtpMarker = value; }
        }

        /// <summary>
        /// Indicates the format of the data within the Payload
        /// </summary>
        public int PayloadType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            //The value is revealed by clearing the 0th bit in the second octet.
            get { /*CheckDisposed();*/ return First16Bits.RtpPayloadType; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ First16Bits.RtpPayloadType = value; }
        }

        #region Notes

        // NOTE The following fileds at offset by 2 from their absolute offset in the contagious allocation of memory for a given RtpHeader.
        // This is because they are octet aligned and contain no bit packed fields.

        #endregion

        public bool IsCompressed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            get { return SegmentToLast10Bytes.Count < 10; }
        }

        /// <summary>
        /// The amount of bytes this instance would occupy when serialized
        /// </summary>
        public int Size { get { return RFC3550.CommonHeaderBits.Size + SegmentToLast10Bytes.Count; } }

        /// <summary>
        /// Gets or Sets the unsigned 16 bit SequenceNumber field in the RtpHeader.
        /// </summary>
        public int SequenceNumber
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            //The sequence number is stored in Netword Byte Order @ + 0x00 from the second octet (relative offset of 0x02 from the beginning of any header pointer)
            get { /*CheckDisposed();*/ return (ushort)Binary.ReadU16(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset, Binary.IsLittleEndian); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ Binary.Write16(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset, Binary.IsLittleEndian, (ushort)value); }
        }

        /// <summary>
        /// Gets or sets the unsigned 32 bit Timestamp field in the RtpHeader
        /// </summary>
        /// <remarks>
        /// The absolute offset of the Timestamp field is at @ 0x04 from the start of any RtpHeader.
        /// </remarks>
        public int Timestamp
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            //The sequence number is stored in Netword Byte Order  @ + 0x02 from the second octet (relative offset of 0x04 from the beginning of any header pointer)
            get { /*CheckDisposed();*/ return (int)Binary.ReadU32(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset + 2, Binary.IsLittleEndian); } //Always read in reverse
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ Binary.Write32(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset + 2, Binary.IsLittleEndian, (uint)value); }
        }

        /// <summary>
        /// Gets or sets the unsigned 32 bit SSRC field in the RtpHeader
        /// </summary>
        public int SynchronizationSourceIdentifier
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            //The sequence number is stored in Netword Byte Order @ + 0x06 from the second octet (relative offset of 0x08 from the beginning of any header pointer)
            get { /*CheckDisposed();*/ return (int)Binary.ReadU32(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset + 6, Binary.IsLittleEndian); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]

            set { /*CheckDisposed();*/ Binary.Write32(SegmentToLast10Bytes.Array, SegmentToLast10Bytes.Offset + 6, Binary.IsLittleEndian, (uint)value); }
        }

        #endregion

        #endregion

        #region Constructor

        /// <summary>
        /// Reads an instance of the RtpHeader class and copies 12 octets which make up the RtpHeader.
        /// </summary>
        /// <param name="octets">A reference to a byte array which contains at least 12 octets to copy.</param>
        /// <param name="offset">the offset in <paramref name="octets"/> to start</param>
        /// <param name="shouldDispose">indicates if <see cref="SegmentToLast6Bytes"/> will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(byte[] octets, int offset = 0, bool shouldDispose = true)
            : base(shouldDispose)
        {
            //Determine the length of the array
            long octetsLength;

            //If the octets reference is null throw an exception
            if (ArrayExtensions.IsNullOrEmpty(octets, out octetsLength)) throw new ArgumentException("octets must not be null and must contain at least 1 element given the deleniation of the offset parameter.", "octets");

            //Check range
            if (offset > octetsLength) throw new ArgumentOutOfRangeException("offset", "Cannot be greater than the length of octets");

            //Todo, should not matter since this header instance would be allowed to be modified, saving the allocation here is not necessary.
            //The check is only relevent because octets my have less than 2 bytes which cannot be handled without exception in the CommonHeaderBits
            //Read a managed representation of the first two octets which are stored in Big ByteOrder / Network Byte Order
            First16Bits = octetsLength == 1 ? new RFC3550.CommonHeaderBits(octets[offset], default(byte)) : new RFC3550.CommonHeaderBits(octets, offset);

            //Allocate space for the other 10 octets
            Last10Bytes = new byte[10];

            //If there are octets for those bytes in the source array
            if (octetsLength > 2)
            {
                //Copy the remaining bytes of the header which consist of the 
                //SequenceNumber (2 octets / U16)
                //Timestamp (4 octets / U32)
                //SSRC (4 octets / U32)
                Array.Copy(octets, offset + 2, Last10Bytes, 0, Math.Min(10, octetsLength - 2));
            }

            //Assign the segment
            SegmentToLast10Bytes = new MemorySegment(Last10Bytes, 0, Last10Bytes.Length);
        }

        /// <summary>
        /// Creates an exact copy of the RtpHeader from the given RtpHeader
        /// </summary>
        /// <param name="other">The RtpHeader to copy</param>
        /// <param name="reference">A value indicating if the RtpHeader given should be referenced or copied.</param>
        /// <param name="shouldDispose">indicates if <see cref="SegmentToLast6Bytes"/> will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(RtpHeader other, bool reference, bool shouldDispose = true)
            : base(shouldDispose)
        {
            if (reference)
            {
                First16Bits = other.First16Bits;
                Last10Bytes = other.Last10Bytes;
                SegmentToLast10Bytes = other.SegmentToLast10Bytes;
            }
            else
            {
                First16Bits = new RFC3550.CommonHeaderBits(other.First16Bits, false, shouldDispose);
                Last10Bytes = new byte[10];
                SegmentToLast10Bytes = new MemorySegment(Last10Bytes, 0, 10, shouldDispose);
                other.Last10Bytes.CopyTo(Last10Bytes, 0);
            }
        }

        /// <summary>
        /// Creates a new instance using new references to the existing data.
        /// </summary>
        /// <param name="other">The other instance</param>
        /// <param name="shouldDispose">indicates if memory will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(RtpHeader other, bool shouldDispose = true)
            : base(shouldDispose)
        {
            First16Bits = new RFC3550.CommonHeaderBits(other.First16Bits, shouldDispose);

            SegmentToLast10Bytes = new MemorySegment(other.SegmentToLast10Bytes, shouldDispose);
        }

        /// <summary>
        /// Creates an exact copy of the RtpHeader from the given memory
        /// </summary>
        /// <param name="memory">The memory</param>
        /// <param name="shouldDispose">indicates if <see cref="SegmentToLast6Bytes"/> will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(MemorySegment memory, bool shouldDispose = true)
            : base(shouldDispose)
        {
            First16Bits = new RFC3550.CommonHeaderBits(memory, shouldDispose);

            SegmentToLast10Bytes = new MemorySegment(memory.Array, memory.Offset + RFC3550.CommonHeaderBits.Size, Binary.Clamp(memory.Count - RFC3550.CommonHeaderBits.Size, 0, 10), shouldDispose);
        }

        /// <summary>
        /// Creates an instance and places the given values into their respective offsets.
        /// </summary>
        /// <param name="version"></param>
        /// <param name="padding"></param>
        /// <param name="extension"></param>
        /// <param name="shouldDispose">indicates if <see cref="SegmentToLast6Bytes"/> will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(int version, bool padding, bool extension, bool shouldDispose = true)
            : base(shouldDispose)
        {
            First16Bits = new RFC3550.CommonHeaderBits(version, padding, extension);

            //Allocate space for the other 10 octets
            Last10Bytes = new byte[10];

            SegmentToLast10Bytes = new MemorySegment(Last10Bytes, 0, 10);

            Version = version;

            Padding = padding;

            Extension = extension;
        }

        /// <summary>
        /// Creates an instance and places the given values into their respective offsets.
        /// </summary>
        /// <param name="version"></param>
        /// <param name="padding"></param>
        /// <param name="extension"></param>
        /// <param name="marker"></param>
        /// <param name="payloadTypeBits"></param>
        /// <param name="contributingSourceCount"></param>
        /// <param name="ssrc"></param>
        /// <param name="sequenceNumber"></param>
        /// <param name="timestamp"></param>
        /// <param name="shouldDispose">indicates if <see cref="SegmentToLast6Bytes"/> will disposed when <see cref="Dispose"/> is called</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RtpHeader(int version, bool padding, bool extension, bool marker, int payloadTypeBits, int contributingSourceCount, int ssrc, int sequenceNumber, int timestamp, bool shouldDispose = true)
            :this(version, padding, extension, shouldDispose)
        {
            //Set the marker bit
            Marker = marker;

            //Set the payloadType using the property for the same reason.
            PayloadType = payloadTypeBits;

            //Set the ContributingSourceCount through the property for the same reason.
            ContributingSourceCount = contributingSourceCount;

            //Set the id of the participant who sent this RtpHeader.
            SynchronizationSourceIdentifier = ssrc;

            //Set the sequence number of the packet
            SequenceNumber = sequenceNumber;

            //Set the Timestamp property
            Timestamp = timestamp;
        }

        #endregion

        #region Instance Methods        

        /// <summary>
        /// Updates the source array of the <see cref="First16Bits"/> and <see cref="SegmentToLast10Bytes"/>
        /// </summary>
        /// <param name="source"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected void Synchronize(ref byte[] source)
        {
            //Should check IsContiguous

            //Should clear reference to m_OwnedOctets or replace or document that source must equal m_OwnedOctes..

            First16Bits.m_Memory.Update(ref source);

            SegmentToLast10Bytes.Update(ref source);
        }

        /// <summary>
        /// Indicates if the <see cref="First16Bits"/> and <see cref="SegmentToLast10Bytes"/> belong to the same array.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsContiguous()
        {
            return First16Bits.m_Memory.Array == SegmentToLast10Bytes.Array && First16Bits.m_Memory.Offset + First16Bits.m_Memory.Count == SegmentToLast10Bytes.Offset;
        }

        public int CopyTo(byte[] dest, int offset)
        {
            if (IsDisposed) return 0;

            int copied = 0;
            
            copied += First16Bits.CopyTo(dest, offset);

            offset += copied;

            MemorySegmentExtensions.CopyTo(SegmentToLast10Bytes, dest, offset);

            copied += SegmentToLast10Bytes.Count;

            offset += SegmentToLast10Bytes.Count;

            return copied;
        }

        /// <summary>
        /// Clones this RtpHeader instance.
        /// If reference is true any changes performed in either this instance or the new instance will be reflected in both instances.
        /// </summary>
        /// <param name="reference">indictes if the new instance should reference this instance.</param>
        /// <returns>The new instance</returns>
        public RtpHeader Clone(bool reference = false) { return new RtpHeader(this, reference); }

        internal IEnumerable<byte> GetEnumerableImplementation()
        {
            return Enumerable.Concat<byte>(First16Bits, SegmentToLast10Bytes);
        }

        #endregion

        #region IEnumerable Implementations

        IEnumerator<byte> IEnumerable<byte>.GetEnumerator()
        {
            return GetEnumerableImplementation().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerableImplementation().GetEnumerator();
        }

        #endregion

        #region Overrides

        protected internal override void Dispose(bool disposing)
        {
            if (false.Equals(disposing) || false.Equals(ShouldDispose)) return;

            base.Dispose(ShouldDispose);

            if (false.Equals(IsDisposed)) return;

            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(First16Bits)))
            {
                //Dispose the instance
                First16Bits.Dispose();

                //Remove the reference to the CommonHeaderBits instance
                First16Bits = null;
            }


            if (false.Equals(IDisposedExtensions.IsNullOrDisposed(SegmentToLast10Bytes)))
            {
                //Invalidate the pointer
                SegmentToLast10Bytes.Dispose();

                SegmentToLast10Bytes = null;
            }

            //Remove the reference to the allocated array.
            Last10Bytes = null;
        }

        /// <summary>
        /// Uses the first 2 bytes and the <see cref="SynchronizationSourceIdentifier"/> to create a HashCode which represents this header.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() { return First16Bits ^ SynchronizationSourceIdentifier; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(RtpHeader other)
        {
            return other.First16Bits.Equals(First16Bits)
                &&
                other.SynchronizationSourceIdentifier.Equals(SynchronizationSourceIdentifier);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj)
        {
            if (System.Object.ReferenceEquals(this, obj)) return true;

            if (false.Equals((obj is RtpHeader))) return false;

            return Equals(obj as RtpHeader);
        }

        #endregion

        #region Operators

        public static bool operator ==(RtpHeader a, RtpHeader b)
        {
            return object.ReferenceEquals(b, null) ? object.ReferenceEquals(a, null) : a.Equals(b);
        }

        public static bool operator !=(RtpHeader a, RtpHeader b) { return (a == b).Equals(false); }

        #endregion
    }

    #endregion
}
