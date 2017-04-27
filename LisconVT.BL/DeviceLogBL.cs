using LisconVT.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.BL
{
    public class DeviceLogBL
    {
        public static void Add(DeviceLog devLog, LisconDbEntities db)
        {
            db.DeviceLog.Add(devLog);
            db.SaveChanges();
        }
    }
}
