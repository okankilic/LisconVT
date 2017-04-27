using LisconVT.Utils.Network;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace LisconVT.GatewaySvc
{
    public partial class GatewaySvc : ServiceBase
    {
        OragonTcpListener _server;

        public GatewaySvc()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            _server = new OragonTcpListener("Gateway Svc", 1234);
        }

        protected override void OnStop()
        {
        }
    }
}
