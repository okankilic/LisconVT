using Oragon.Classes;
using Oragon.Classes.Binary;
using Oragon.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Media.Common.Extensions
{
    /// <summary>
    /// Provides useful extension methods for the <see cref="MemorySegment"/> class
    /// </summary>
    public static class MemorySegmentExtensions
    {
        //public static System.IO.MemoryStream ToMemoryStream() readable, writeable, publicablyVisible...

        public static byte[] ToArray(this MemorySegment segment)
        {
            if (IDisposedExtensions.IsNullOrDisposed(segment)) return null;

            if (segment.m_Length.Equals(Binary.LongZero)) return MemorySegment.EmptyBytes;

            byte[] copy = new byte[segment.LongLength];

            CopyTo(segment, copy, 0, segment.Count);

            //Copy the rest
            if (segment.LongLength > segment.Count) Array.Copy(segment.Array, segment.Offset + segment.Count, copy, segment.Count, segment.LongLength - segment.Count);

            return copy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static System.ArraySegment<byte> ToByteArraySegment(this MemorySegment segment)
        {
            return new ArraySegment<byte>(segment.Array, segment.Offset, segment.Count);
        }

        /// <summary>
        /// Copies all bytes from the segment to dest
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="dest"></param>
        /// <param name="destinationIndex">The offset in <paramref name="dest"/> to start copying</param>
        public static void CopyTo(this MemorySegment segment, byte[] dest, int destinationIndex)
        {
            CopyTo(segment, dest, destinationIndex, segment.Count);
        }

        /// <summary>
        /// Copies bytes from the segment to the dest
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="dest"></param>
        /// <param name="destinationIndex">The offset in <paramref name="dest"/> to start copying</param>
        /// <param name="length">The amount of bytes to copy from <paramref name="segment"/></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyTo(this MemorySegment segment, byte[] dest, int destinationIndex, int length)
        {
            if (IDisposedExtensions.IsNullOrDisposed(segment)) return;

            //could check dest and also verify length 

            Array.Copy(segment.Array, segment.Offset, dest, destinationIndex, length);
        }

        //make Left / Right or expect the callers to use -length when they need to...
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MemorySegment Subset(this MemorySegment segment, int offset, int length, bool shouldDispose = true)
        {
            //Should propably enforce that offset and length do not supercede existing length or this is not a true subset.
            return new MemorySegment(segment.Array, offset, length, shouldDispose);
        }

        public static int Find(byte[] source, int start, int count, MemorySegment first, params MemorySegment[] segments)
        {
            int found = 0;

            int needed = count;

            first = null;

            foreach (var segment in segments)
            {
                //Search for the partial match in the segment
                found = Utility.ContainsBytes(segment.Array, ref start, ref count, source, start, needed);

                //If it was found
                if (found >= 0)
                {
                    //If not already set then set it
                    if (object.ReferenceEquals(first, null)) first = segment;

                    //Subtract from needed and if 0 remains break
                    if ((needed -= found) == 0) break;

                    //Continue
                    continue;
                }

                //Reset the count, the match needs to be found in order.
                needed = count;

                //Reset to no first segment
                first = null;
            }

            //return the index or the last partial match.
            return found;
        }
    }
}
