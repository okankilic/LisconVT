using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.Domain.Models
{
    /// <summary>
    /// $$dc0129,214,V114,34561,,170427 162444,A,+29,2,509033216,+41,1,457910144,0,0,0001000000107780,0000000000000000,0.00,0.00,0.00,,-1,0,0,0,1#
    /// </summary>
    public class V114Message: MdvrMessageBase
    {
        public int DriveFlag { get; set; }

        public V114Message(): base()
        {

        }
    }
}
