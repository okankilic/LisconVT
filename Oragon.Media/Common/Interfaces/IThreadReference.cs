using Oragon.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oragon.Interfaces
{
    /// <summary>
    /// Provides an interface which allows access to any <see cref="System.Threading.Thread"/> owned by an implementer of this interface.
    /// </summary>
    public interface IThreadReference
    {
        IEnumerable<Thread> GetReferencedThreads();

        /// <summary>
        /// Provides a function which configures a thread as required.
        /// </summary>
        Action<Thread> ConfigureThread { get; set; } //ApplyConfiguration (Name etc)
    }
}
