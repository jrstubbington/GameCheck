using System;
using System.Data;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Management;
using MySql.Data.MySqlClient;
using uPLibrary.Networking.M2Mqtt;
using System.IO;
using uPLibrary.Networking.M2Mqtt.Messages;
using System.Diagnostics;
using System.Collections;
using System.Net;
using System.Collections.Generic;
using System.Security.Permissions;

namespace MyNewService
{
    public partial class GameCheck : ServiceBase
    {

        private DataSet exes = new DataSet();
        private Dictionary<string, string> activeGames = new Dictionary<string, string>();
        //private ArrayList activeGamesID = new ArrayList();

        ManagementEventWatcher startWatch = null;
        ManagementEventWatcher stopWatch = null;

        private static readonly DateTime Jan1st1970 = new DateTime
        (1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public enum ServiceState
        {
            SERVICE_STOPPED = 0x00000001,
            SERVICE_START_PENDING = 0x00000002,
            SERVICE_STOP_PENDING = 0x00000003,
            SERVICE_RUNNING = 0x00000004,
            SERVICE_CONTINUE_PENDING = 0x00000005,
            SERVICE_PAUSE_PENDING = 0x00000006,
            SERVICE_PAUSED = 0x00000007,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus
        {
            public long dwServiceType;
            public ServiceState dwCurrentState;
            public long dwControlsAccepted;
            public long dwWin32ExitCode;
            public long dwServiceSpecificExitCode;
            public long dwCheckPoint;
            public long dwWaitHint;
        };

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus serviceStatus);

        public GameCheck()
        {
            InitializeComponent();
            CanHandleSessionChangeEvent = true;
            // TODO send wakeup flag to IoTA
            eventLog1 = new System.Diagnostics.EventLog();
            if (!System.Diagnostics.EventLog.SourceExists("Application"))
            {
                System.Diagnostics.EventLog.CreateEventSource("Application", "GameCheck");
            }
            eventLog1.Source = "GameCheck";
            eventLog1.Log = "Application";
            

            startWatch = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            startWatch.EventArrived += new EventArrivedEventHandler(StartWatch_EventArrived);

            stopWatch = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            stopWatch.EventArrived += new EventArrivedEventHandler(StopWatch_EventArrived);

            //shutdownWatch = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM SystemEvents"))



            eventLog1.WriteEntry("INIT COMPLETED");
        }


        override protected void OnSessionChange(SessionChangeDescription changeDescription)
        {
            eventLog1.WriteEntry("Session changing: " + changeDescription.Reason.ToString());
            //SessionUnlock
            //SessionLock
        }

        protected override void OnShutdown()
        {
            // TODO send shutdown flag to IoTA
            eventLog1.WriteEntry("Legion shutting down");
            base.OnShutdown();
        }

        protected override void OnStart(string[] args)
        {
            // Update the service state to Start Pending.  
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_START_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            eventLog1.WriteEntry("In OnStart");

            startWatch.Start();
            stopWatch.Start();

            UpdateFromDb();

            MqttSslProtocols mqttProtocols = new MqttSslProtocols();
            MqttClient client = new MqttClient("MQTT_CLIENT_ADDR", 8883, false, mqttProtocols, null, null);
            // register to message received
            client.MqttMsgPublishReceived += Client_MqttMsgPublishReceived;

            string clientId = Guid.NewGuid().ToString();
            client.Connect(clientId, "james", "USER_PASSWORD");

            // subscribe to the topic "/home/temperature" with QoS 2
            client.Subscribe(new string[] { "/home/games" }, new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });

            eventLog1.WriteEntry("INIT COMPLETED");

            // Update the service state to Running.  
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        void Client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            if (e.Topic.Contains("/home/games"))
            {
                UpdateFromDb();
            }
        }

        protected override void OnStop()
        {
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_STOP_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            eventLog1.WriteEntry("In onStop.");
            startWatch.Stop();
            stopWatch.Stop();

            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        protected void StopWatch_EventArrived(object sender, EventArrivedEventArgs e)
        {
            string id = e.NewEvent.Properties["ProcessID"].Value.ToString();
            //Console.WriteLine(name + " | " + activeGames.Count);
            //eventLog1.WriteEntry(name);

            if (activeGames.ContainsKey(id))
            {
                String game = activeGames[id];
                eventLog1.WriteEntry("Stopping " + game);
                HttpRequest("http://192.168.1.201:8080/services/games/stopsession?exe=" + game);
                activeGames.Remove(id);
            }
            /*foreach (KeyValuePair<string, string> pair in activeGames)
            {
                if (pair.Key.Equals(id))
                {
                    eventLog1.WriteEntry("Stopping " + pair.Value);
                    HttpRequest("http://192.168.1.201:8080/services/games/stopsession?exe=" + pair.Value);
                    activeGames.Remove(pair);
                }
            }*/
        }

        
        protected void StartWatch_EventArrived(object sender, EventArrivedEventArgs e)
        {
            string name = e.NewEvent.Properties["ProcessName"].Value.ToString();
            string id = e.NewEvent.Properties["ProcessID"].Value.ToString();
            Process p = Process.GetProcessById(int.Parse(id));
            string fullPath = p.MainModule.FileName;

            if (fullPath.StartsWith("S:\\SteamLibrary\\steamapps\\common"))
            {
                eventLog1.WriteEntry("ACTIVE GAMES BEFORE START: " + activeGames.Count);
                Console.WriteLine("This is a game to be checked");
                Console.WriteLine("\t" + fullPath);
                bool matches = false;
                try
                {
                    DataTable table = exes.Tables[0];
                    DataView dv = new DataView
                    {
                        RowFilter = "EXECUTABLES = '" + name + "'"
                    };

                    DataRow[] rows = table.Select(dv.RowFilter);

                    foreach (DataRow row in rows)
                    {
                        if (row[1].ToString().Equals("0"))
                        {
                            return;
                        }
                        else if (row[0].ToString().Equals(name))
                        {
                            matches = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

                string remove = "S:\\SteamLibrary\\steamapps\\common\\";
                string commonPath = fullPath.Replace(remove, "");
                string mainFolder = commonPath.Split(Path.DirectorySeparatorChar)[0];
                string exe = Path.GetFileName(commonPath);

                if (matches)
                {
                    
                    Console.WriteLine(activeGames.Count);
                    //Signal start of session for app
                    eventLog1.WriteEntry("Starting session for " + name + " | " + exe);
                    HttpRequest("http://192.168.1.201:8080/services/games/startsession?exe=" + exe);
                }
                else
                {
                    //Definitely a game that isn't represented in the db. Need to verify name against list of apps or list for manual processing later

                    //Trigger db refresh from steam to look for new purchases
                    HttpRequest("http://192.168.1.201:8080/services/games/update");
                    //Add folder name and exe name to db
                    HttpRequest("http://192.168.1.201:8080/services/games/processexe?folder=" + mainFolder + "&exe=" + exe);
                    //Attempt to symbolically link folder name to application name
                    //if linking fails, notify user to update db through interface later
                    HttpRequest("http://192.168.1.201:8080/services/games/startsession?exe=" + exe);
                    UpdateFromDb();
                }
                activeGames.Add(id, exe);
                eventLog1.WriteEntry("ACTIVE GAMES AFTER START: " + activeGames.Count);
            }

            //else this didn't launch from a defined game folder and will therefore be ignored
        }

        protected void UpdateFromDb()
        {
            string myConnectionString = "Server=tau.trullingham.home;Database=GAMES;Uid=UID;Pwd=PASSWORD;";
            MySqlConnection connection = new MySqlConnection(myConnectionString);
            MySqlCommand cmd;
            
            try
            {
                connection.Open();
                cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT EXECUTABLES, DIRECTORY_ID FROM GAMES.EXECUTABLES";// WHERE NOT DIRECTORY_ID=0;";
                MySqlDataAdapter adap = new MySqlDataAdapter(cmd);
                adap.Fill(exes);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {

                connection.Close();
                //connection.Dispose();
            }
        }

        public string HttpRequest(String addresss)
        {
            string webResponse = "";
            Uri uri = new Uri(addresss);
            WebResponse response = null;
            StreamReader reader = null;
            try
            {
                WebRequest request = WebRequest.Create(uri);
                response = request.GetResponse();
                Stream dataStream = response.GetResponseStream();
                reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();
                Console.WriteLine("RESPONSE: " + responseFromServer);
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("FAILED TO SEND HTTP REQUEST: " + e);
            }
            finally
            {
                if (reader != null)
                {
                    reader.Close();
                }
                if (response != null)
                {
                    response.Close();
                }
            }
            Console.WriteLine(webResponse);
            return webResponse;
        }

        /*protected string GetUserAccountFromSid(byte[] sid)
        {
            SecurityIdentifier si = new SecurityIdentifier(sid, 0);
            //NTAccount acc = (NTAccount)si.Translate(typeof(NTAccount));
            return si.ToString();// acc.Value;
        }*/

        protected static long CurrentTimeMillis()
        {
            return (long)(DateTime.UtcNow - Jan1st1970).TotalMilliseconds;
        }
        
        public void ProcessExectubles()
        {
            string[] directories = Directory.GetDirectories("C:\\");

            foreach (String dir in directories)
            {
                string[] files = Directory.GetFiles("C:\\", "*.dll");
            }
        }
    }
}
