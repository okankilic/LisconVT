using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Oragon.Classes.Consoles
{
    /// <summary>
    /// The <see cref="Class"/> associated with the console.
    /// </summary>
    public class ConsoleClass : Class { }

    /// <summary>
    /// The implementation of <see cref="Interfaces.InterClass"/> which defines the <see cref="Class"/> implementation associated.
    /// </summary>
    public class ConsoleBase : ConsoleClass, Interfaces.InterClass
    {
        /// <summary>
        /// `this`
        /// </summary>
        Class Interfaces.InterClass.Class
        {
            get { return this; }
        }
    }

    /// <summary>
    /// An implementation of <see cref="Interfaces.IConsole"/> and <see cref="ConsoleBase"/>
    /// </summary>
    public abstract class PlatformConsoleImplementation : ConsoleBase, Interfaces.IConsole
    {
        //--
    }

    /// <summary>
    /// 
    /// </summary>
    public class PlatformConsole : PlatformConsoleImplementation
    {
        //--
    }
}
