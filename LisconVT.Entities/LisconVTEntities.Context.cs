﻿//------------------------------------------------------------------------------
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
    using System.Data.Entity;
    using System.Data.Entity.Infrastructure;
    
    public partial class LisconDbEntities : DbContext
    {
        public LisconDbEntities()
            : base("name=LisconDbEntities")
        {
        }
    
        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            throw new UnintentionalCodeFirstException();
        }
    
        public virtual DbSet<DevGroup> DevGroup { get; set; }
        public virtual DbSet<Device> Device { get; set; }
        public virtual DbSet<DeviceLog> DeviceLog { get; set; }
        public virtual DbSet<DeviceRuntime> DeviceRuntime { get; set; }
        public virtual DbSet<User> User { get; set; }
    }
}
