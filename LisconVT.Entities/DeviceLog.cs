//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated from a template.
//
//     Manual changes to this file may cause unexpected behavior in your application.
//     Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace LisconVT.Entities
{
    using System;
    using System.Collections.Generic;
    
    public partial class DeviceLog
    {
        public int ID { get; set; }
        public System.DateTime LogTime { get; set; }
        public int LogType { get; set; }
        public string DevIDNO { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public decimal Altitude { get; set; }
        public int Speed { get; set; }
        public int Course { get; set; }
    
        public virtual Device Device { get; set; }
    }
}