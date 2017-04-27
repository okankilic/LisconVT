using LisconVT.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.BL
{
    public class DeviceRuntimeBL
    {
        internal static void Create(DeviceRuntime runtime, LisconDbEntities db)
        {
            // Todo: Check Editability

            runtime.CreateUserID = -1;
            runtime.CreateTime = DateTime.UtcNow;
            runtime.UpdateUserID = -1;
            runtime.UpdateTime = DateTime.UtcNow;

            db.DeviceRuntime.Add(runtime);
            db.SaveChanges();
        }

        public static void Update(DeviceRuntime runtime, LisconDbEntities db)
        {
            var existingRuntime = db.DeviceRuntime.Single(q => q.DevIDNO == runtime.DevIDNO);

            existingRuntime.GpsTime = runtime.GpsTime;
            existingRuntime.Latitude = runtime.Latitude;
            existingRuntime.Longitude = runtime.Longitude;
            existingRuntime.Altitude = runtime.Altitude;
            existingRuntime.Speed = runtime.Speed;
            existingRuntime.Course = runtime.Course;

            db.SaveChanges();
        }
    }
}
