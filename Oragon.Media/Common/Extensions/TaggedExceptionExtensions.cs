using Oragon.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Extensions
{
    public static class TaggedExceptionExtensions
    {
        /// <summary>
        /// Raises the given <see cref="TaggedException"/>
        /// </summary>
        /// <typeparam name="T">The type related to the exception.</typeparam>
        /// <param name="exception">The <see cref="System.Exception"/> which occured.</param>
        public static void Raise<T>(this TaggedException<T> exception) { if (object.ReferenceEquals(exception, null).Equals(false)) throw exception; }

        /// <summary>
        /// Tries to <see cref="Raise"/> the given <see cref="TaggedException"/>
        /// </summary>
        /// <typeparam name="T">The type related to the exception.</typeparam>
        /// <param name="exception">The <see cref="System.Exception"/> which occured.</param>

        public static void TryRaise<T>(this TaggedException<T> exception) //storeData
        {
            try { exception.Raise(); }
            catch { /*hide*/ }
        }

        /// <summary>
        /// Raises the given <see cref="TaggedException"/>
        /// </summary>
        /// <typeparam name="T">The type related to the exception.</typeparam>
        /// <param name="exception">The <see cref="System.Exception"/> which occured.</param>
        /// <param name="breakForResume">Indicates if the function should attach the debugger.</param>
        public static void RaiseAndAttachIfUnhandled<T>(this TaggedException<T> exception, bool breakForResume = true)
        {
            //If not attaching then fall back to TryRaise which hides the exception and return.
            if (false.Equals(breakForResume))
            {
                exception.TryRaise();

                return;
            }

            //Raise the exception
            try { exception.Raise(); }
            catch //Handle it
            {
                //If the debugger is not attached and it cannot be then return
                if (false.Equals(DebugExtensions.Attach())) return;

                //Break if still attached
                DebugExtensions.BreakIfAttached();
            }
        }

        /// <summary>
        /// Raises an <see cref="Common.Exception"/> on the calling thread.
        /// </summary>
        /// <typeparam name="T">The type of the exception to raise.</typeparam>
        /// <param name="tag">The element related to the exception</param>
        /// <param name="message">The message realted to the exception, if not provided a default message will be used.</param>
        /// <param name="innerException">any <see cref="System.Exception"/> which is related to the exception being thrown</param>
        public static void RaiseTaggedException<T>(T tag, string message, System.Exception innerException = null) { new TaggedException<T>(tag, message ?? TaggedException<T>.DefaultExceptionTypeMessage<T>(), innerException).Raise(); }

        /// <summary>
        /// Tries to raises an <see cref="Common.Exception"/> on the calling thread and if the exception is not handled it will be discared.
        /// </summary>
        /// <typeparam name="T">The type of the exception to raise.</typeparam>
        /// <param name="tag">The element related to the exception</param>
        /// <param name="message">The message realted to the exception, if not provided a default message will be used.</param>
        /// <param name="innerException">any <see cref="System.Exception"/> which is related to the exception being thrown</param>
        public static void TryRaiseTaggedException<T>(T tag, string message, System.Exception innerException = null) { new TaggedException<T>(tag, message ?? TaggedException<T>.DefaultExceptionTypeMessage<T>(), innerException).TryRaise(); }
    }
}
