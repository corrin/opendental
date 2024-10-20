using CodeBase;
using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Xml;
using System.Windows.Forms;
using SystemTask = System.Threading.Tasks.Task;
using OpenDentBusiness;
using DataConnectionBase;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenDentBusiness.UI;
using System.Net.Sockets;
using System.Net;
using System.Web.Services.Description;

namespace OpenDentBusiness.ODSMS
{
    public static class ODSMS
    {
        // Configuration variables
        public static bool USE_ODSMS = true;
        public static bool SEND_SMS = true;
        public static bool WRITE_TO_DATABASE = true;

        // Internal state
        public static bool wasSmsBroken = false;
        public static bool initialStartup = true;
        public static HttpClient sharedClient = null;

        // Variables from the configuration file
        public static string DEBUG_NUMBER = ""; // if set, all sent SMS are sent here instead
        public static string SMS_BRIDGE_NAME = "";  // the name, e.g. CORRIN-ZEPHYRUS or RECEPTION-AIO of the 

        public static bool IS_SMS_BRIDGE_MACHINE = false;
        public static string PRACTICE_PHONE_NUMBER = "";
        public static string WEBSERVER_API_KEY = "HQWk7H3bFh8o8hAg";
        public static string WEBSERVER_PORT = "8585";

        public static string sms_folder_path = @"L:\msg_guids\";

        private static List<Def> _listDefsApptConfirmed;
        public static long _defNumTwoWeekConfirmed;
        public static long _defNumOneWeekConfirmed;
        public static long _defNumConfirmed;
        public static long _defNumNotCalled;
        public static long _defNumUnconfirmed;
        public static long _defNumTwoWeekSent;
        public static long _defNumOneWeekSent;
        public static long _defNumTexted;
        public static long _defNumWebSched;

        public static JustRemotePhoneBridge _bridgeInstance;

        static ODSMS()
        {
            string MachineName = Environment.MachineName;

            InitializeEventLog();

            string configPath = @"L:\odsms.txt";
            ValidateConfigPath(configPath);
            LoadConfiguration(configPath, MachineName);
            string baseUrl = $"http://{SMS_BRIDGE_NAME}:{ODSMS.WEBSERVER_PORT}/";

            sharedClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl)

            };
            sharedClient.DefaultRequestHeaders.Add("ApiKey", WEBSERVER_API_KEY);


            LogConfigurationStatus(MachineName);
        }

        public static bool SanityCheckConstants()
        {
            var defNumList = new List<long>
            {
                _defNumTexted,
                _defNumTwoWeekSent,
                _defNumOneWeekSent,
                _defNumTwoWeekConfirmed,
                _defNumOneWeekConfirmed,
                _defNumConfirmed,
                _defNumNotCalled,
                _defNumUnconfirmed,
                _defNumWebSched
            };

            // Create a HashSet from the list
            var defNumSet = new HashSet<long>(defNumList);

            // Compare the size of the list to the size of the HashSet
            bool allUnique = defNumList.Count == defNumSet.Count;
            if (allUnique)
            {
                return true;
            }
            else
            {
                ODSMSLogger.Instance.Log("Database constants like _defNumOneWeekConfirmed have an issue", EventLogEntryType.Error);
                System.Windows.MessageBox.Show("Attempt to send SMS without the database!?.");
                return false;
            }
        }
        private static long GetAndCheckDefNum(string itemName, List<OpenDentBusiness.Def> listDefs)
        {
            var def = listDefs
                .FirstOrDefault(d => string.Equals(d.ItemName, itemName, StringComparison.OrdinalIgnoreCase));

            long defNum = def?.DefNum ?? 0;

            if (defNum == 0)
            {
                string s = $"The '{itemName}' appointment status was not found.";
                ODSMSLogger.Instance.Log(s, EventLogEntryType.Error);
                System.Windows.MessageBox.Show(s);
                throw new Exception(s);
            }

            return defNum;
        }


        public static async SystemTask InitializeSMS()
        {
            while (!DataConnection.HasDatabaseConnection)
            {
                Console.WriteLine("Waiting for database connection...");
                await SystemTask.Delay(5000);
            }

            _listDefsApptConfirmed = Defs.GetDefsForCategory(DefCat.ApptConfirmed, isShort: true);
            _defNumTexted = GetAndCheckDefNum("texted", _listDefsApptConfirmed);
            _defNumTwoWeekSent = GetAndCheckDefNum("2 week sent", _listDefsApptConfirmed);
            _defNumOneWeekSent = GetAndCheckDefNum("1 week sent", _listDefsApptConfirmed);
            _defNumTwoWeekConfirmed = GetAndCheckDefNum("2 week confirmed", _listDefsApptConfirmed);
            _defNumOneWeekConfirmed = GetAndCheckDefNum("1 week confirmed", _listDefsApptConfirmed);
            _defNumConfirmed = GetAndCheckDefNum("Appointment Confirmed", _listDefsApptConfirmed);
            _defNumNotCalled = GetAndCheckDefNum("not called", _listDefsApptConfirmed);
            _defNumUnconfirmed = GetAndCheckDefNum("unconfirmed", _listDefsApptConfirmed);
            _defNumWebSched = GetAndCheckDefNum("Created from Web Sched", _listDefsApptConfirmed);
            SanityCheckConstants();

            if (IS_SMS_BRIDGE_MACHINE && _bridgeInstance == null)
            {
                _bridgeInstance = new JustRemotePhoneBridge();
                JustRemotePhoneBridge.InitializeBridge(); 
            }
        }

        private static void InitializeEventLog()
        {
            if (!EventLog.SourceExists("ODSMS"))
            {
                EventLog.CreateEventSource("ODSMS", "Application");
                Console.WriteLine("Event source 'ODSMS' created successfully.");
            }
            else
            {
                Console.WriteLine("Event source 'ODSMS' already exists.");
            }

            EventLog.WriteEntry("ODSMS", "Running custom build of Open Dental on " + Environment.MachineName, EventLogEntryType.Information, 101, 1, new byte[10]);
        }

        private static void ValidateConfigPath(string configPath)
        {
            if (!Directory.Exists(Path.GetDirectoryName(configPath)))
            {
                throw new DirectoryNotFoundException($"Directory not found: {Path.GetDirectoryName(configPath)}");
            }

            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException("Config file not found", configPath);
            }
        }

        private static void LoadConfiguration(string configPath, string MachineName)
        {
            try
            {
                foreach (string line in File.ReadLines(configPath))
                {
                    if (line.StartsWith("DISABLE:"))
                        USE_ODSMS = false;
                    else if (line.StartsWith("DEBUG:"))
                        DEBUG_NUMBER = line.Replace("DEBUG:", "");
                    else if (line.StartsWith("PHONE:"))
                        PRACTICE_PHONE_NUMBER = line.Replace("PHONE:", "");
                    else if (line.StartsWith("RECEIVER:"))
                    {
                        string receiver_name = line.Replace("RECEIVER:", "");
                        SMS_BRIDGE_NAME = receiver_name;
                        if (receiver_name == MachineName)
                        {
                            IS_SMS_BRIDGE_MACHINE = true;
                        }
                        ValidateSMSBridgeName();
                    }
                    else if (line.StartsWith("#"))
                    {
                        Console.WriteLine("Ignoring comment line in control file");
                    } else
                    {
                        Console.WriteLine("Unknown command in control file");
                        Console.WriteLine(line);
                        EventLog.WriteEntry("ODSMS", $"Invalid row in control file: {line}", EventLogEntryType.Warning, 101, 1, new byte[10]);

                    }
                }
            }
            catch (FileNotFoundException)
            {
                EventLog.WriteEntry("ODSMS", "odsms.txt config file could not be read - stuff is about to break", EventLogEntryType.Error, 101, 1, new byte[10]);
                throw;
            }

            ValidateConfiguration();
        }

        private static void ValidateSMSBridgeName()
        {
            if (string.IsNullOrEmpty(SMS_BRIDGE_NAME))
            {
                throw new InvalidOperationException("RECEIVER is not set in the configuration file.");
            }

            try
            {
                var hostEntry = Dns.GetHostEntry(SMS_BRIDGE_NAME);
                var ipAddress = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (ipAddress != null)
                {
                    ODSMSLogger.Instance.Log($"Successfully resolved {SMS_BRIDGE_NAME} to IP: {ipAddress}", EventLogEntryType.Information);
                }
                else
                {
                    throw new InvalidOperationException($"Could not resolve an IPv4 address for {SMS_BRIDGE_NAME}.");
                }
            }
            catch (SocketException ex)
            {
                string errorMessage = $"Failed to resolve {SMS_BRIDGE_NAME}. Error: {ex.Message}";
                ODSMSLogger.Instance.Log(errorMessage, EventLogEntryType.Error);
                throw new InvalidOperationException(errorMessage, ex);
            }
            catch (Exception ex)
            {
                string errorMessage = $"Unexpected error while validating {SMS_BRIDGE_NAME}. Error: {ex.Message}";
                ODSMSLogger.Instance.Log(errorMessage, EventLogEntryType.Error);
                throw new InvalidOperationException(errorMessage, ex);
            }
        }
        private static void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(SMS_BRIDGE_NAME))
            {
                throw new ArgumentNullException("Forgot to set RECEIVER: in the configuration file");
            }

            if (string.IsNullOrEmpty(PRACTICE_PHONE_NUMBER))
            {
                throw new ArgumentNullException("Forgot to set PHONE: in the configuration file");
            }
        }

        private static void LogConfigurationStatus(string MachineName)
        {
            if (IS_SMS_BRIDGE_MACHINE)
            {
                EventLog.WriteEntry("ODSMS", "Name matches, enabling SMS reception", EventLogEntryType.Information, 101, 1, new byte[10]);
            }
            else
            {
                EventLog.WriteEntry("ODSMS", "Not receiving SMS on this computer:" + MachineName, EventLogEntryType.Information, 101, 1, new byte[10]);
            }

            EventLog.WriteEntry("ODSMS", "Successfully loaded odsms.txt config file", EventLogEntryType.Information, 101, 1, new byte[10]);
        }

        public static bool CheckSMSConnection()
        {
            if (ODSMS.IS_SMS_BRIDGE_MACHINE)
            {
                if (_bridgeInstance != null && _bridgeInstance.IsConnected())
                {

                    return true;
                }

            }
            return false;
        }

        public static async SystemTask WaitForDatabaseAndUserInitialization()
        {
            while (!DataConnection.HasDatabaseConnection)
            {
                ODSMSLogger.Instance.Log("Waiting for database connection...", EventLogEntryType.Information, logToEventLog: false, logToFile: false);
                await SystemTask.Delay(5000);
            }

            while (Security.CurUser == null || Security.CurUser.UserNum == 0)
            {
                ODSMSLogger.Instance.Log("Waiting for user information to be initialized...", EventLogEntryType.Information, logToEventLog: false, logToFile: false);
                await SystemTask.Delay(5000);
            }
        }

        public static string RenderReminder(string reminderTemplate, Patient p, Appointment a)
        {
            string s = reminderTemplate
                .Replace("[NamePreferredOrFirst]", p.GetNameFirstOrPreferred())
                .Replace("?NamePreferredOrFirst", p.GetNameFirstOrPreferred())
                .Replace("[FName]", p.FName)
                .Replace("?FName", p.FName);

            if (a != null)
            {
                s = s.Replace("[date]", a.AptDateTime.ToString("dddd, d MMMM yyyy"))
                     .Replace("[time]", a.AptDateTime.ToString("h:mm tt"));
            }
            return s;
        }

        public static void EnsureSmsFolderExists()
        {
            if (!Directory.Exists(sms_folder_path))
            {
                ODSMSLogger.Instance.Log("SMS MSG GUIDs folder not found - creating", EventLogEntryType.Warning);
                System.Windows.MessageBox.Show("SMS folder not found - creating. If this is at the practice then quit OpenDental and contact Corrin");
                Directory.CreateDirectory(sms_folder_path);
            }
        }

        // This is the core SMS handling including setup.
        public static async void InitializeAndRunSmsTasks()
        {
            // Asynchronously wait for database and user initialization
            await ODSMS.WaitForDatabaseAndUserInitialization();

            // Asynchronously wait for SMS initialization
            await ODSMS.InitializeSMS();

            // Now SMS is initialized, proceed with dependent tasks
            if (ODSMS.IS_SMS_BRIDGE_MACHINE)
            {
                MessageBox.Show("This computer will send/receive SMS");
                await System.Threading.Tasks.Task.Factory.StartNew(() => OpenDentBusiness.ODSMS.JustRemotePhoneBridge.LaunchWebServer(), TaskCreationOptions.LongRunning);
            }

            if (!ODSMS.DEBUG_NUMBER.IsNullOrEmpty())
            {
                MessageBox.Show("DEBUG MODE!!");
                await System.Threading.Tasks.Task.Run(() => {
                    JustRemotePhoneBridge.TestSendMessage();
                });

            }

        }

    }
}