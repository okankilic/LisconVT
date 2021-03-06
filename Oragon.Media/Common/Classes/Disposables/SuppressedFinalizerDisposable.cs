﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Classes.Disposables
{
    /// <summary>
    /// Provides an implementation of the <see cref="BaseDisposable"/> with a supressed finalizer.
    /// </summary>
    /// <remarks>
    /// <see href="http://stackoverflow.com/questions/18020861/how-to-get-notified-before-static-variables-are-finalized/18316325#18316325">StackOverflow</see>, <see href="http://stackoverflow.com/questions/8011001/can-anyone-explain-this-finalisation-behaviour">Also</see> some for details
    /// </remarks>
    public class SuppressedFinalizerDisposable : BaseDisposable
    {

#if DEBUG
        /// <summary>
        /// Determine if the object was created in the default app domain and which event handler to unhook at dispose
        /// </summary>
        bool DefaultAppDomain;

#endif

        /// <summary>
        /// Should never run unless immediately finalized.
        /// </summary>
        ~SuppressedFinalizerDisposable()
        {            

#if DEBUG
            if (DefaultAppDomain) AppDomain.CurrentDomain.ProcessExit -= SetShouldDisposeIfSenderIsBaseDisposableAndDisposeNow;
            else AppDomain.CurrentDomain.DomainUnload -= SetShouldDisposeIfSenderIsBaseDisposableAndDisposeNow;
#endif

            Dispose(ShouldDispose);

#if DEBUG
            System.Diagnostics.Debug.WriteLine(ToString() + "@Finalize Completed");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SuppressedFinalizerDisposable(bool shouldDispose)
            : base(shouldDispose) //Suppress Finalize may not be called more than once without a matching Reregister
        {
            //Suppress the finalizer of this instance always.
            GC.SuppressFinalize(this);

#if DEBUG
            if (DefaultAppDomain = AppDomain.CurrentDomain.IsDefaultAppDomain())
                AppDomain.CurrentDomain.ProcessExit += SetShouldDisposeIfSenderIsBaseDisposableAndDisposeNow;
            else
                AppDomain.CurrentDomain.DomainUnload += SetShouldDisposeIfSenderIsBaseDisposableAndDisposeNow;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal protected override void Dispose(bool disposing)
        {
            //If already disposed or disposing and should not dispose return.
            if (false.Equals(disposing)) return;

            base.Dispose(disposing);
        }

        //Resurrect() {  }
    }
}