using LisconVT.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;

namespace LisconVT.BL
{
    public class DeviceBL
    {
        public static void Register(string devIDNO, DeviceRuntime runtime, LisconDbEntities db)
        {
            using (var ts = new TransactionScope())
            {
                var device = new Device();

                device.IDNO = devIDNO;
                device.Plate = string.Empty;
                device.CreateUserID = -1;
                device.CreateTime = DateTime.UtcNow;
                device.UpdateUserID = -1;
                device.UpdateTime = DateTime.UtcNow;

                db.Device.Add(device);
                db.SaveChanges();

                DeviceRuntimeBL.Create(runtime, db);

                ts.Complete();
            }
        }

        public static Device TryGet(string devIDNO, LisconDbEntities db)
        {
            return db.Device.SingleOrDefault(q => q.IDNO == devIDNO);
        }
    }
}
