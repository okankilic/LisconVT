using Oragon.Extensions;
using Oragon.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Media.Common.Extensions
{

    /// <summary>
    /// Provides functions useful to <see cref="IThreadReference"/>
    /// </summary>
    public static class IThreadReferenceExtensions
    {
        public static void AbortAndFreeAll(this IThreadReference reference, int timeoutmSec = (int)TimeSpanExtensions.MicrosecondsPerMillisecond)
        {
            foreach (var tp in reference.GetReferencedThreads())
            {
                //Take a reference to that thread
                System.Threading.Thread t = tp;

                //Call AbortAndFree                                                    //Should be Stopped to check for stop?
                if (false == ThreadExtensions.TryAbortAndFree(ref t, System.Threading.ThreadState.Running, timeoutmSec)) t = null; //Remove the reference if required
            }
        }

        public static void AbortAndFreeAll(this IThreadReference reference, System.TimeSpan timeout)
        {
            foreach (var tp in reference.GetReferencedThreads())
            {
                //Take a reference to that thread
                System.Threading.Thread t = tp;

                //Call AbortAndFree                                                    //Should be Stopped to check for stop?
                if (false == ThreadExtensions.TryAbortAndFree(ref t, timeout, System.Threading.ThreadState.Running)) t = null; //Remove the reference if required
            }
        }
    }
}
