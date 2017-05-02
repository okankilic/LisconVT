using Oragon.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Media.Common.Extensions
{
    public static class IDisposedExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrDisposed(this IDisposed dispose)
        {
            return object.ReferenceEquals(dispose, null) || dispose.IsDisposed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldDisposed(this IDisposed dispose)
        {
            return false.Equals(object.ReferenceEquals(dispose, null)) && dispose.ShouldDispose;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckDisposed(this IDisposed dispose)
        {
            if (false.Equals(object.ReferenceEquals(dispose, null)) && dispose.IsDisposed) throw new System.ObjectDisposedException("IDisposedExtensions.CheckDisposed,true");
        }

        //public static void SetShouldDispose(this IDisposed dispose, bool value, bool callDispose = false)
        //{
        //    if (IDisposedExtensions.IsNullOrDisposed(dispose)) return;

        //    dispose.ShouldDispose = value;

        //    if (callDispose) dispose.Dispose();
        //}
    }
}
