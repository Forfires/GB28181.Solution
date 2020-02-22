﻿using GB28181.Logger4Net;
using GB28181.Server.Message;
using GB28181.Server.Utils;
using GB28181.Service;
using GB28181.Service.Protos.AsClient.SystemConfig;
using GB28181.Service.Protos.DeviceCatalog;
using GB28181.Service.Protos.DeviceFeature;
using GB28181.Service.Protos.Ptz;
using GB28181.Service.Protos.Video;
using GB28181.Service.Protos.VideoRecord;
using GB28181.SIPSorcery.Servers;
using GB28181.SIPSorcery.Servers.SIPMessage;
using GB28181.SIPSorcery.Servers.SIPMonitor;
using GB28181.SIPSorcery.SIP;
using GB28181.SIPSorcery.Sys;
using GB28181.SIPSorcery.Sys.Cache;
using GB28181.SIPSorcery.Sys.Config;
using GB28181.SIPSorcery.Sys.Model;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GB28181.Server.Message
{
    public class MainProcess : IDisposable
    {
        private static readonly ILog logger = AppState.GetLogger("Process");
        //interface IDisposable implementation
        private bool _already_disposed = false;

        // Thread signal for stop work.
        private readonly ManualResetEvent _eventStopService = new ManualResetEvent(false);

        // Thread signal for infor thread is over.
        private readonly ManualResetEvent _eventThreadExit = new ManualResetEvent(false);

        //signal to exit the main Process
        private readonly AutoResetEvent _eventExitMainProcess = new AutoResetEvent(false);

        private Task _mainTask = null;
        private Task _sipTask = null;
        private Task _registerTask = null;
        private Task _deviceStatusTask = null;

        private Task _mainWebSocketRpcTask = null;

        private DateTime _keepaliveTime;
        private Queue<HeartBeatEndPoint> _keepAliveQueue = new Queue<HeartBeatEndPoint>();

        private Queue<GB28181.SIPSorcery.Sys.XML.Catalog> _catalogQueue = new Queue<GB28181.SIPSorcery.Sys.XML.Catalog>();

        private readonly ServiceCollection servicesContainer = new ServiceCollection();

        private ServiceProvider _serviceProvider = null;


        public MainProcess()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        #region IDisposable interface

        public void Dispose()
        {
            Dispose(true);
            // tell the GC that the Finalize process no longer needs to be run for this object.
            GC.SuppressFinalize(this);
            
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (this)
            {
                if (_already_disposed)
                    return;

                if (disposing)
                {
                    _eventStopService.Close();
                    _eventThreadExit.Close();
                }
            }

            _already_disposed = true;
        }

        #endregion

        public void Run()
        {
            _eventStopService.Reset();
            _eventThreadExit.Reset();

            // config info for .net core https://www.cnblogs.com/Leo_wl/p/5745772.html#_label3
            var builder = new ConfigurationBuilder();
            //.SetBasePath(Directory.GetCurrentDirectory())
            //.AddXmlFile("Config/gb28181.xml", false, reloadOnChange: true);
            var config = builder.Build();//Console.WriteLine(config["sipaccount:ID"]);
            //var sect = config.GetSection("sipaccounts");

            //Consul Register Close Consul
           // ServiceRegister();


            //InitServer
            SipAccountStorage.RPCGBServerConfigReceived += SipAccountStorage_RPCGBServerConfigReceived;

            //Config Service & and run
            ConfigServices(config);

            //Start the main serice
            _mainTask = Task.Factory.StartNew(() => MainServiceProcessing());

            //wait the process exit of main
            _eventExitMainProcess.WaitOne();
        }

        public void Stop()
        {
            // signal main service exit
            _eventStopService.Set();
            _eventThreadExit.WaitOne();
        }

        private void ConfigServices(IConfigurationRoot configuration)
        {
            //we should initialize resource here then use them.
            servicesContainer.AddSingleton(configuration)  // add configuration 
                            .AddSingleton<ILog, Logger>()
                            .AddSingleton<ISipAccountStorage, SipAccountStorage>()
                            .AddSingleton<MediaEventSource>()
                            .AddSingleton<MessageHub>()
                            .AddSingleton<CatalogEventsProc>()
                            .AddSingleton<AlarmEventsProc>()
                              .AddSingleton<DeviceEventsProc>()
                            .AddScoped<ISIPServiceDirector, SIPServiceDirector>()
                            .AddTransient<ISIPMonitorCore, SIPMonitorCoreService>()
                            .AddSingleton<ISipMessageCore, SIPMessageCoreService>()
                            .AddSingleton<ISIPTransport, SIPTransport>()
                            .AddTransient<ISIPTransactionEngine, SIPTransactionEngine>()
                            .AddSingleton<ISIPRegistrarCore, SIPRegistrarCoreService>()
                            .AddSingleton<IMemoCache<Camera>, DeviceObjectCache>()
 //                           .AddSingleton<IRpcService, RpcServer>()
                            .AddScoped<VideoSession.VideoSessionBase, SSMediaSessionImpl>()
                            .AddScoped<PtzControl.PtzControlBase, PtzControlImpl>()
                            .AddScoped<DeviceCatalog.DeviceCatalogBase, DeviceCatalogImpl>()
//                            .AddScoped<Manage.Manage.ManageBase, DeviceManageImpl>()
                            .AddScoped<DeviceFeature.DeviceFeatureBase, DeviceFeatureImpl>()
                            .AddScoped<VideoOnDemand.VideoOnDemandBase, VideoOnDemandImpl>()
                            .AddSingleton<IServiceCollection>(servicesContainer); // add itself 
            _serviceProvider = servicesContainer.BuildServiceProvider();
        }

        private void MainServiceProcessing()
        {
            _keepaliveTime = DateTime.Now;
            try
            {
                var _mainSipService = _serviceProvider.GetRequiredService<ISipMessageCore>();
                //Get meassage Handler
                var messageHandler = _serviceProvider.GetRequiredService<MessageHub>();
                // start the Listening SipService in main Service
                _sipTask = Task.Factory.StartNew(() =>
                {
                    _mainSipService.OnKeepaliveReceived += messageHandler.OnKeepaliveReceived;
                    _mainSipService.OnServiceChanged += messageHandler.OnServiceChanged;
                    _mainSipService.OnCatalogReceived += messageHandler.OnCatalogReceived;
                    //_mainSipService.OnNotifyCatalogReceived += messageHandler.OnNotifyCatalogReceived;
                    _mainSipService.OnAlarmReceived += messageHandler.OnAlarmReceived;
                    //_mainSipService.OnRecordInfoReceived += messageHandler.OnRecordInfoReceived;
                    _mainSipService.OnDeviceStatusReceived += messageHandler.OnDeviceStatusReceived;
                    _mainSipService.OnDeviceInfoReceived += messageHandler.OnDeviceInfoReceived;
                    _mainSipService.OnMediaStatusReceived += messageHandler.OnMediaStatusReceived;
                    _mainSipService.OnPresetQueryReceived += messageHandler.OnPresetQueryReceived;
                    _mainSipService.OnDeviceConfigDownloadReceived += messageHandler.OnDeviceConfigDownloadReceived;
                    _mainSipService.OnResponseCodeReceived += messageHandler.OnResponseCodeReceived;
                    _mainSipService.Start();

                });

                // run the register service
                var _registrarCore = _serviceProvider.GetRequiredService<ISIPRegistrarCore>();
                _registerTask = Task.Factory.StartNew(() =>
                {
                    _registrarCore.ProcessRegisterRequest();
                });

                //Run the Rpc Server End
                var _mainWebSocketRpcServer = _serviceProvider.GetRequiredService<IGrpcServer>();
                _mainWebSocketRpcTask = Task.Factory.StartNew(() =>
                {
                    _mainWebSocketRpcServer.AddIPAdress("0.0.0.0");
                    _mainWebSocketRpcServer.AddPort(EnvironmentVariables.GBServerGrpcPort);//50051
                    _mainWebSocketRpcServer.Run();
                });

                //Device Status Report
                _deviceStatusTask = Task.Factory.StartNew(() =>
                {
                    messageHandler.DeviceStatusReport();
                });

                //video session alive
                var videosessionalive = VideoSessionKeepAlive();

                ////test code will be removed
                //var abc = WaitUserCmd();
                //abc.Wait();

                //wait main service exit
                _eventStopService.WaitOne();

                //signal main process exit
                _eventExitMainProcess.Set();
            }
            catch (Exception exMsg)
            {
                logger.Error(exMsg.Message);
                _eventExitMainProcess.Set();
            }
            
        }

        #region Init Server
        private List<GB28181.SIPSorcery.SIP.App.SIPAccount> SipAccountStorage_RPCGBServerConfigReceived()
        {
            try
            {
                string SystemConfigurationServiceAddress = EnvironmentVariables.SystemConfigurationServiceAddress ?? "systemconfigurationservice:8080";
                logger.Debug("System Configuration Service Address: " + SystemConfigurationServiceAddress);
                //var channel = new  Channel(SystemConfigurationServiceAddress, ChannelCredentials.Insecure);
                var channel = GrpcChannel.ForAddress(SystemConfigurationServiceAddress);
                var client = new Manage.ManageClient(channel);
                GetIntegratedPlatformConfigRequest req = new GetIntegratedPlatformConfigRequest();
                GetIntegratedPlatformConfigResponse rep = client.GetIntegratedPlatformConfig(req);
                logger.Debug("GetIntegratedPlatformConfigResponse: " + rep.Config.ToString());
                GBPlatformConfig item = rep.Config;
                List<GB28181.SIPSorcery.SIP.App.SIPAccount> _lstSIPAccount = new List<GB28181.SIPSorcery.SIP.App.SIPAccount>();
                GB28181.SIPSorcery.SIP.App.SIPAccount obj = new GB28181.SIPSorcery.SIP.App.SIPAccount();
                obj.Id = Guid.NewGuid();
                //obj.Owner = item.Name;
                obj.GbVersion = string.IsNullOrEmpty(item.GbVersion) ? "GB-2016" : item.GbVersion;
                obj.LocalID = string.IsNullOrEmpty(item.LocalID) ? "42010000002100000002" : item.LocalID;
                obj.LocalIP = HostsEnv.GetRawIP();
                obj.LocalPort = string.IsNullOrEmpty(item.LocalPort) ? Convert.ToUInt16(5061) : Convert.ToUInt16(item.LocalPort);
                obj.RemotePort = string.IsNullOrEmpty(item.RemotePort) ? Convert.ToUInt16(5060) : Convert.ToUInt16(item.RemotePort);
                obj.Authentication = string.IsNullOrEmpty(item.Authentication) ? false : bool.Parse(item.Authentication);
                obj.SIPUsername = string.IsNullOrEmpty(item.SIPUsername) ? "admin" : item.SIPUsername;
                obj.SIPPassword = string.IsNullOrEmpty(item.SIPPassword) ? "123456" : item.SIPPassword;
                obj.MsgProtocol = System.Net.Sockets.ProtocolType.Udp;
                obj.StreamProtocol = System.Net.Sockets.ProtocolType.Udp;
                obj.TcpMode = GB28181.SIPSorcery.Net.RTP.TcpConnectMode.passive;
                obj.MsgEncode = string.IsNullOrEmpty(item.MsgEncode) ? "GB2312" : item.MsgEncode;
                obj.PacketOutOrder = string.IsNullOrEmpty(item.PacketOutOrder) ? true : bool.Parse(item.PacketOutOrder);
                obj.KeepaliveInterval = string.IsNullOrEmpty(item.KeepaliveInterval) ? Convert.ToUInt16(5000) : Convert.ToUInt16(item.KeepaliveInterval);
                obj.KeepaliveNumber = string.IsNullOrEmpty(item.KeepaliveNumber) ? Convert.ToByte(3) : Convert.ToByte(item.KeepaliveNumber);
                _lstSIPAccount.Add(obj);
                logger.Debug("GetIntegratedPlatformConfigResponse SIPAccount: {LocalID:" + obj.LocalID + ", LocalIP:" + obj.LocalIP + ", LocalPort:" + obj.LocalPort + ", RemotePort:"
                    + obj.RemotePort + ", SIPUsername:" + obj.SIPUsername + ", SIPPassword:" + obj.SIPPassword + ", KeepaliveInterval:" + obj.KeepaliveInterval + "}");
                return _lstSIPAccount;
            }
            catch (Exception ex)
            {
                logger.Warn("GetIntegratedPlatformConfigRequest: " + ex.Message);
                //logger.Debug("Can't get gb info from device-mgr, it will get gb info from config.");
                return null;
            }
        }

        //for test
        private async Task VideoSessionKeepAlive()
        {
            await Task.Run(() =>
             {
                 var mockCaller = _serviceProvider.GetService<ISIPServiceDirector>();
                 while (true)
                 {
                     for (int i = 0; i < mockCaller.VideoSessionAlive.ToArray().Length; i++)
                     {
                         Dictionary<string, DateTime> dict = mockCaller.VideoSessionAlive[i];
                         foreach (string key in dict.Keys)
                         {
                             TimeSpan ts1 = new TimeSpan(DateTime.Now.Ticks);
                             TimeSpan ts2 = new TimeSpan(Convert.ToDateTime(dict[key]).Ticks);
                             TimeSpan ts = ts1.Subtract(ts2).Duration();
                             if (ts.Seconds > 30)
                             {
                                 mockCaller.Stop(key.ToString().Split(',')[0], key.ToString().Split(',')[1]);
                                 mockCaller.VideoSessionAlive.RemoveAt(i);
                             }
                         }
                     }
                 }
             });
        }

        //for test
        //private async Task WaitUserCmd()
        //{
        //    await Task.Run(() =>
        //     {
        //         while (true)
        //         {
        //             Console.WriteLine("\ninput command : I -Invite, E -Exit");
        //             var inputkey = Console.ReadKey();
        //             switch (inputkey.Key)
        //             {
        //                 case ConsoleKey.I:
        //                     {
        //                         var mockCaller = _serviceProvider.GetService<ISIPServiceDirector>();
        //                         mockCaller.MakeVideoRequest("42010000001180000184", new int[] { 5060 }, EnvironmentVariables.LocalIp);
        //                     }
        //                     break;
        //                 case ConsoleKey.E:
        //                     Console.WriteLine("\nexit Process!");
        //                     break;
        //                 default:
        //                     break;
        //             }
        //             if (inputkey.Key == ConsoleKey.E)
        //             {
        //                 return 0;
        //             }
        //             else
        //             {
        //                 continue;
        //             }
        //         }
        //     });
        //}

        #endregion
    }
}