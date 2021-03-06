﻿using Oragon.Classes.Disposables;
using Oragon.Collections.Generic;
using Oragon.Extensions;
using Oragon.Interfaces;
using Oragon.Media.Common.Extensions;
using System;
using System.Runtime.CompilerServices;

namespace Oragon.Classes
{
    /// <summary>
    /// Represents a base class for binary types of data which are commonly associated with a packet.
    /// </summary>
    public class PacketBase : LifetimeDisposable, IPacket
    {

        //static readonly System.TimeSpan DefaultHalfLife;

        #region Fields

        //readonly
        internal protected byte[] m_OwnedOctets; //RawLength => m_OwnedOctets.Length

        //internal protected MemorySegment Memory;

        //int CompleteLength;

        #endregion

        #region Constructor

        public PacketBase(bool shouldDispose = true) //OneHour LifeTime
            : base(shouldDispose)
        {

        }

        public PacketBase(bool shouldDispose, System.TimeSpan lifetime)
            :base(shouldDispose, lifetime)
        {

        }

        public PacketBase(int size, bool shouldDispose) //OneHour LifeTime
            : this(shouldDispose)
        {
            m_OwnedOctets = new byte[size];

            Length = size;
        }

        public PacketBase(int size, bool shouldDispose, System.TimeSpan lifetime)
            : this(shouldDispose, lifetime)
        {
            m_OwnedOctets = new byte[size];

            Length = size;
        }

        public PacketBase(byte[] data, int offset, int length, bool isComplete, bool shouldDispose) 
            : this(length, shouldDispose)
        {
            IsComplete = isComplete;

            System.Array.Copy(data, offset, m_OwnedOctets, 0, length);
        }

        #endregion

        #region Properties

        public byte[] Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_OwnedOctets;
            }
        }

        public System.DateTime Created
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return base.CreatedUtc.DateTime; }
        }

        public System.DateTime? Transferred
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Provides an indication if the packet is complete.
        /// </summary>
        public bool IsComplete
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Provides an indication if the packet is compressed
        /// </summary>
        public bool IsCompressed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Provides an indication if the packet is read only
        /// </summary>
        public bool IsReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Provides an indication of the length of the packet.
        /// </summary>
        public long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        /// <summary>
        /// Used to Complete the packet
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public int CompleteFrom(System.Net.Sockets.Socket socket, MemorySegment buffer)
        {
            if (IsComplete) return 0;

            throw new System.NotImplementedException();
        }

        public System.Collections.Generic.IEnumerable<byte> Prepare()
        {
            return m_OwnedOctets;
        }

        public bool TryGetBuffers(out System.Collections.Generic.IList<System.ArraySegment<byte>> buffer)
        {

            if (IsDisposed)
            {
                buffer = default(System.Collections.Generic.IList<System.ArraySegment<byte>>);

                return false;
            }

            buffer = new System.Collections.Generic.List<System.ArraySegment<byte>>()
            {
                new System.ArraySegment<byte>(m_OwnedOctets)
            };

            return true;
        }

        #endregion

        protected internal override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (ShouldDispose)
            {
                m_OwnedOctets = null;
            }
        }
    }

    #region IStamp, Stamp, Directions

    /// <summary>
    /// Pattern
    /// </summary>
    interface IStamp : IPattern
    {
        System.DateTimeOffset StampDateTime { get; }

        System.Net.EndPoint StampEndPoint { get; }
    }

    /// <summary>
    /// Implementation, Abstraction
    /// </summary>
    abstract class Stamp : ClassInterface<IStamp>, InterClass, IStamp
    {
        internal protected System.Net.EndPoint NetEndPoint;

        internal protected System.DateTimeOffset Sample;

        Class InterClass.Class
        {
            get { return this; }
        }

        System.DateTimeOffset IStamp.StampDateTime
        {
            get { return Sample; }
        }

        System.Net.EndPoint IStamp.StampEndPoint
        {
            get { return NetEndPoint; }
        }
    }

    /// <summary>
    /// Enum to describe a direction or many flagged directions.
    /// </summary>
    class Direction : Enum
    {
        [System.Flags]
        public enum DirectionEnum
        {
            None = 0,
            Up = 1,
            Down = 2,
            Left = 4,
            Right = 8
        }

        public DirectionEnum SystemEnum;

        public uint AbsoluteDirection { get { return (uint)SystemEnum; } }

        public override int GetHashCode()
        {
            return (int)SystemEnum;
        }

        public override string ToString()
        {
            return SystemEnum.ToString();
        }

        public override bool Equals(object obj)
        {
            return SystemEnum.Equals(obj);
        }

        public static implicit operator DirectionEnum(Direction d)
        {
            return d.SystemEnum;
        }

        public static implicit operator Direction(DirectionEnum d)
        {
            return new Direction()
            {
                SystemEnum = d
            };
        }

        public static implicit operator Direction(int direction)
        {
            return new Direction()
            {
                SystemEnum = (DirectionEnum)direction
            };
        }

        public static implicit operator Direction(uint direction)
        {
            return new Direction()
            {
                SystemEnum = (DirectionEnum)direction
            };
        }
    }

    /// <summary>
    /// A derived Direction, relatively received
    /// </summary>
    class SendDirection : Direction
    {
        public SendDirection()
        {
            SystemEnum = DirectionEnum.Left | DirectionEnum.Right;
        }
    }

    /// <summary>
    /// A derived Direction, relatively sent
    /// </summary>
    class ReceiveDirection : Direction
    {
        public ReceiveDirection()
        {
            SystemEnum = DirectionEnum.Up | DirectionEnum.Down;
        }
    }

    /// <summary>
    /// A <see cref="Stamp"/> with an encapsulated <see cref="Direction"/>
    /// </summary>
    class PacketStamp : Stamp
    {
        internal Direction StampDirection;
    }

    /// <summary>
    /// A <see cref="Class"/> which provides a derivation of <see cref="ClassInterface"/> specific to <see cref="IStamp"/>.
    /// The implemenation provides an <see cref="Add"/> method which allows data to be added to the <see cref="Queue"/>
    /// </summary>
    class PacketInformation : ClassInterface<IStamp>, InterClass
    {
        readonly ConcurrentLinkedQueueSlim<Tuple<PacketStamp, MemorySegment>> Queue = new ConcurrentLinkedQueueSlim<Tuple<PacketStamp, MemorySegment>>();

        Class InterClass.Class
        {
            get { return this; }
        }

        // ClassInterface<IStamp>

        public bool Add(System.Tuple<PacketStamp, MemorySegment> data)
        {
            return Queue.TryEnqueue(ref data);
        }
    }

    #endregion

    /// <summary>
    /// An implementation of <see cref="IPacket"/> which allows application defined completion semantics.
    /// </summary>
    public class PacketHolder : PacketBase, IPacket
    {
        /// <summary>
        /// A function which is used to determine if this instance is complete.
        /// </summary>
        public System.Func<System.Net.Sockets.Socket, MemorySegment, int> Completer //Name sucks, should probably be an Interface
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }

        #region PacketBase + Completer

        public PacketHolder(bool shouldDispose = true)
            : base(shouldDispose)
        {
            Completer = StaticCompleteFrom;
        }

        public PacketHolder(int size, bool shouldDispose = true)
            : base(size, shouldDispose)
        {
            Completer = StaticCompleteFrom;
        }

        public PacketHolder(int size, bool shouldDispose, System.TimeSpan lifetime)
            : base(size, shouldDispose, lifetime)
        {
            Completer = StaticCompleteFrom;
        }

        #endregion

        #region Holding

        public IPacket Holding
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal protected set;
        }


        public bool IsHolding
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return IDisposedExtensions.IsNullOrDisposed(Holding).Equals(false); }
        }


        public PacketHolder(IPacket toHold)
        {
            Holding = toHold;

            Completer = StaticCompleteFrom;
        }

        #endregion

        #region Completion

        //CurrentLength, ExpectedLength;

        #endregion

        #region Information

        //ReceivedFrom{EndPoint, DateTime}, SentTo {EndPoint, DateTime}

        #endregion

        //AlwaysIncomplete
        static int StaticCompleteFrom(System.Net.Sockets.Socket socket, MemorySegment buffer)
        {
            return int.MinValue;
        }

        //AlwaysComplete

        //Determine

        #region IPacket

        bool IPacket.IsComplete
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (object.ReferenceEquals(Completer, null)) return StaticCompleteFrom(null, null).Equals(int.MinValue);
                return Completer(null, null).Equals(int.MinValue);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        System.Collections.Generic.IEnumerable<byte> IPacket.Prepare()
        {
            if (IsDisposed) return null;

            return m_OwnedOctets ?? Holding.Prepare();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IPacket.TryGetBuffers(out System.Collections.Generic.IList<System.ArraySegment<byte>> buffer)
        {
            if (IsHolding) return Holding.TryGetBuffers(out buffer);

            if (IsDisposed)
            {
                buffer = default(System.Collections.Generic.IList<System.ArraySegment<byte>>);

                return false;
            }

            buffer = new System.Collections.Generic.List<System.ArraySegment<byte>>()
            {
                new System.ArraySegment<byte>(m_OwnedOctets)
            };

            return true;
        }
        
        long IPacket.Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsHolding) return Holding.Length;

                long length;

                ArrayExtensions.IsNullOrEmpty(m_OwnedOctets, out length);

                return length;
            }
        }

        bool IPacket.IsReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsHolding) return Holding.IsReadOnly;

                return IsReadOnly;
            }
        }

        bool IPacket.IsCompressed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsHolding) return Holding.IsCompressed;

                return IsCompressed;
            }
        }

        #endregion

    }
}
