using CodeBase;
using System;
using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
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
using OpenDentalBusiness;

namespace OpenDentBusiness.ODSMS
{

    public static class ODSMS
    {
        // Configuration variables

        // If set to false, it will be as if this code doesn't exist.  SMS will be completely disabled.
        public static bool USE_ODSMS = true;
        // If set to false, it won't actually send the SMS to anyone. 
        // But it will update the database as if they were sent.  Be careful
        public static bool SEND_SMS = true;
        // If set to false, it won't write to the database that the SMS was sent.  
        // This makes it extremely easy to accidentially double-send to patients.  Be careful.
        public static bool WRITE_TO_DATABASE = true;

        // Internal state
        // Not used much.  Handy for identifying that SMS has just come back online and so we should notify reception
        public static bool wasSmsBroken = false;
        // Used to prevent notifying reception that SMS has just come back online when they're just starting up
        public static bool initialStartup = true;
        // Just for efficiency, saves spinning up a client every message
        public static HttpClient sharedClient = null;

        // Variables from the configuration file

        public static bool IsUsingProductionDatabase { get; private set; }
        public static bool IsUsingProductionSMS { get; private set; }
        public static bool IsRunningInDevelopmentEnvironment { get; private set; }

        // if set, all sent SMS can only be sent to testing phones
        // Somewhat overu
        public static bool DEBUG_MODE = true;

        // The domain name of the machine that runs the SMS bridge
        // Probably always OPENDENTAL
        public static string SMS_BRIDGE_NAME = "";

        // THe domain name of the machine that displays errors if SMS crash
        // And is responsible for running the scheduled tasks
        // Set IS_MAIN_SMS_MACHINE to true if this machine matches this name
        public static string SMS_RECEIVER_NAME = "";

        // TRUE IF we are the machine that runs the scheduling amd receives SMS.
        // NOTE: Because you can run multiple instances of OpenDental, this might be true but this process isn't the main one
        // We test that every scheduled task.
        public static bool IS_MAIN_SMS_MACHINE = false;  

        // I don't think this is used anywhere
        public static string PRACTICE_PHONE_NUMBER = "";

        // Read from the config file.   Used to prevent SMS spam
        public static string WEBSERVER_API_KEY = "";

        // This is the port that the SMS bridge listens on.  It's a constant
        public static string WEBSERVER_PORT = "5170";

        // Where to save a backup copy of all received SMS.
        // TODO: Move this logic out of OD and into the bridge
        public static string sms_folder_path = @"\\OPENDENTAL\OD Letters\msg_guids\";

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


        static ODSMS()
        {
            string MachineName = Environment.MachineName;

            InitializeEventLog();

            string configPath = @"\\OPENDENTAL\OD Letters\odsms.txt";
            ValidateConfigPath(configPath);
            LoadConfiguration(configPath, MachineName);
            string baseUrl = $"http://{SMS_BRIDGE_NAME}:{ODSMS.WEBSERVER_PORT}/smsgateway/";

            sharedClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl)

            };
            sharedClient.DefaultRequestHeaders.Add("X-API-Key", WEBSERVER_API_KEY);
            sharedClient.Timeout = TimeSpan.FromSeconds(60);

        }

        private static void DetectProductionDatabase()
        {
            try
            {
                const string PRODUCTION_DB_IP = "192.168.192.30";

                // Get the hostname for the database
                string dbServer = "opendental";
                if (string.IsNullOrEmpty(dbServer))
                {
                    ODSMSLogger.Instance.Log(
                        "Could not determine database server name from connection",
                        EventLogEntryType.Warning,
                        logToEventLog: false,
                        logToFile: true);
                    return;
                }

                // Resolve the hostname to IP address
                IPHostEntry dbEntry = Dns.GetHostEntry(dbServer);
                var ipv4Addresses = dbEntry.AddressList
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip => ip.ToString())
                    .ToList();

                // Check if any of the resolved IPs match production
                IsUsingProductionDatabase = ipv4Addresses.Contains(PRODUCTION_DB_IP);

                ODSMSLogger.Instance.Log(
                    $"Database server '{dbServer}' resolves to: {string.Join(", ", ipv4Addresses)}",
                    EventLogEntryType.Information,
                    logToEventLog: false,
                    logToFile: true);

                ODSMSLogger.Instance.Log(
                    $"Database: {(IsUsingProductionDatabase ? "PRODUCTION" : "TEST/DEV")}",
                    EventLogEntryType.Information,
                    logToEventLog: false);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log(
                    $"Error detecting database environment: {ex.Message}",
                    EventLogEntryType.Warning,
                    logToEventLog: false);
                IsUsingProductionDatabase = false;
            }
        }

        private static void DetectProductionSMS()
        {
            try
            {
                const string PRODUCTION_SMS_IP = "192.168.192.30";

                // Get the hostname for the SMS bridge
                string smsServer = SMS_BRIDGE_NAME;
                if (string.IsNullOrEmpty(smsServer))
                {
                    ODSMSLogger.Instance.Log(
                        "SMS bridge server name is not set",
                        EventLogEntryType.Warning,
                        logToEventLog: false,
                        logToFile: true);
                    return;
                }

                // Resolve the hostname to IP address
                IPHostEntry smsEntry = Dns.GetHostEntry(smsServer);
                var ipv4Addresses = smsEntry.AddressList
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip => ip.ToString())
                    .ToList();

                // Check if any of the resolved IPs match production
                IsUsingProductionSMS = ipv4Addresses.Contains(PRODUCTION_SMS_IP);

                ODSMSLogger.Instance.Log(
                    $"SMS bridge server '{smsServer}' resolves to: {string.Join(", ", ipv4Addresses)}",
                    EventLogEntryType.Information,
                    logToEventLog: false,
                    logToFile: true);

                ODSMSLogger.Instance.Log(
                    $"SMS Bridge: {(IsUsingProductionSMS ? "PRODUCTION" : "TEST/DEV")}",
                    EventLogEntryType.Information,
                    logToEventLog: false);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log(
                    $"Error detecting SMS environment: {ex.Message}",
                    EventLogEntryType.Warning,
                    logToEventLog: false);
                IsUsingProductionSMS = false;
            }
        }

        private static void LogEnvironmentStatus()
        {
            string environmentInfo =
                $"Environment Status:\n" +
                $"• Running in development: {IsRunningInDevelopmentEnvironment}\n" +
                $"• Using production database: {IsUsingProductionDatabase}\n" +
                $"• Using production SMS bridge: {IsUsingProductionSMS}";

            ODSMSLogger.Instance.Log(
                environmentInfo,
                EventLogEntryType.Information,
                logToEventLog: false,
                logToFile: true);
        }

        private static void DisplayEnvironmentWarnings()
        {
            // Warn if in a mixed environment
            if (IsUsingProductionDatabase != IsUsingProductionSMS)
            {
                string message = "WARNING: Mixed environment detected!\n" +
                    $"Database: {(IsUsingProductionDatabase ? "PRODUCTION" : "TEST/DEV")}\n" +
                    $"SMS Bridge: {(IsUsingProductionSMS ? "PRODUCTION" : "TEST/DEV")}\n\n" +
                    "This configuration may cause unexpected behavior.";

                MessageBox.Show(message, "Environment Warning",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);

                ODSMSLogger.Instance.Log(message, EventLogEntryType.Warning);
            }

            // Warn if in development but using production
            if (IsRunningInDevelopmentEnvironment && (IsUsingProductionDatabase || IsUsingProductionSMS))
            {
                List<string> productionComponents = new List<string>();

                if (IsUsingProductionDatabase)
                    productionComponents.Add("Database");

                if (IsUsingProductionSMS)
                    productionComponents.Add("SMS Bridge");

                string componentsText = string.Join("\n• ", productionComponents);

                string message = "CAUTION: Development environment using PRODUCTION:\n" +
                    $"• {componentsText}\n\n" +
                    "Changes will affect the production environment!";

                MessageBox.Show(message, "Development Warning",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);

                ODSMSLogger.Instance.Log(message, EventLogEntryType.Warning);
            }
        }

        private static void DetectEnvironment()
        {
            // Determine if we're in a development environment
            #if DEBUG
                IsRunningInDevelopmentEnvironment = true;
            #else
                        IsRunningInDevelopmentEnvironment = System.Diagnostics.Debugger.IsAttached;
            #endif

            // Check database and SMS separately
            DetectProductionDatabase();
            DetectProductionSMS();

            // Log the environment status
            LogEnvironmentStatus();

            // Display warnings if needed
            DisplayEnvironmentWarnings();
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
                ODSMSLogger.Instance.Log("Waiting for database connection...",
                    EventLogEntryType.Information,
                    logToEventLog: false);
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
            string directory = Path.GetDirectoryName(configPath);

            if (!Directory.Exists(directory))
            {
                string message = $"Directory not found: {directory}. Please check the path and network connectivity.";
                MessageBox.Show(message); 
                throw new DirectoryNotFoundException(message);
            }

            if (!File.Exists(configPath))
            {
                string message = $"Config file not found: {configPath}. Please ensure the file exists and is accessible.";
                MessageBox.Show(message); 
                throw new FileNotFoundException(message, configPath);
            }
        }

        private static void LoadConfiguration(string configPath, string machineName)
        {
            try
            {
                foreach (string line in File.ReadLines(configPath))
                {
                    ProcessConfigLine(line);
                }
            }
            catch (FileNotFoundException)
            {
                string message = "The configuration file 'odsms.txt' could not be read. Please check if the file exists and is accessible.";
                MessageBox.Show(message); // Show a message box to the user
                EventLog.WriteEntry("ODSMS", message + " - the application will terminate.", EventLogEntryType.Error, 101, 1, new byte[10]);
                throw;
            }

            ValidateConfiguration();
        }

        private static void ProcessConfigLine(string line)
        {
            // Remove inline comments (everything after '#')
            int commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
            {
                line = line.Substring(0, commentIndex);
            }

            line = line.Trim(); // Handle extra whitespace early

            // Skip empty or comment-only lines
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            // Split the line into key and value based on the first colon
            string[] parts = line.Split(':'); 
            if (parts.Length < 2)
            {
                ODSMSLogger.Instance.Log($"Invalid configuration line: {line}", EventLogEntryType.Information, logToEventLog: false);
                return;
            }

            string key = parts[0].Trim();
            string value = parts[1].Trim();

            // Handle known configuration keys
            switch (key)
            {
                case "DISABLE":
                    USE_ODSMS = false;
                    break;
                case "API_KEY":
                    WEBSERVER_API_KEY = value;
                    break;
                case "PHONE":
                    PRACTICE_PHONE_NUMBER = value;
                    break;
                case "BRIDGE":
                    SMS_BRIDGE_NAME = value;
                    break;
                case "RECEIVER":
                    SMS_RECEIVER_NAME = value;
                    break;
                default:
                    ODSMSLogger.Instance.Log($"Unknown command in control file: {key}",EventLogEntryType.Information,logToEventLog: false);
                    break;

            }
        }
     
        private static bool ValidateSMSBridgeName()
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
                    return true;
                }
                throw new InvalidOperationException($"Could not resolve an IPv4 address for {SMS_BRIDGE_NAME}.");
            }
            catch (Exception ex)
            {
                string errorMessage = $"Failed to resolve {SMS_BRIDGE_NAME}. Error: {ex.Message}";
                ODSMSLogger.Instance.Log(errorMessage, EventLogEntryType.Error);
                throw new InvalidOperationException(errorMessage, ex);
            }
        }

        private static async SystemTask ValidateSMSBridge()
        {
            try
            {
                // Check if the bridge service is responding
                var response = await sharedClient.GetAsync("gateway-status");
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"SMS Bridge service returned error: {response.StatusCode}");
                }

                ODSMSLogger.Instance.Log("SMS Bridge service is responding correctly", EventLogEntryType.Information);
            }
            catch (HttpRequestException ex)
            {
                string errorMessage = $"Could not connect to SMS Bridge service. Error: {ex.Message}";
                ODSMSLogger.Instance.Log(errorMessage, EventLogEntryType.Error);
                throw new InvalidOperationException(errorMessage, ex);
            }
        }

        private static void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(SMS_BRIDGE_NAME))
            {
                throw new ArgumentNullException("Forgot to set BRIDGE: in the configuration file");
            }
            if (string.IsNullOrEmpty(SMS_RECEIVER_NAME))
            {
                throw new ArgumentNullException("Forgot to set RECEIVER: in the configuration file");
            }
            if (string.IsNullOrEmpty(WEBSERVER_API_KEY))
            {
                throw new ArgumentNullException("Forgot to set API_KEY: in the configuration file");
            }

        }

        private static void LogConfigurationStatus(string MachineName)
        {
            if (SMS_RECEIVER_NAME == MachineName || DEBUG_MODE)
            {
                IS_MAIN_SMS_MACHINE = true;
            } else
            {
                IS_MAIN_SMS_MACHINE = false;
            }
            if (IS_MAIN_SMS_MACHINE)
            {
                EventLog.WriteEntry("ODSMS", "Name matches, enabling SMS reception", EventLogEntryType.Information, 101, 1, new byte[10]);
            }
            else
            {
                EventLog.WriteEntry("ODSMS", "Not receiving SMS on this computer:" + MachineName, EventLogEntryType.Information, 101, 1, new byte[10]);
            }

            EventLog.WriteEntry("ODSMS", "Successfully loaded odsms.txt config file", EventLogEntryType.Information, 101, 1, new byte[10]);
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

        public static string RenderReminder(string reminderTemplate, Patient p, Appointment a, int? earlyMinutes = null)
        {
            string s = reminderTemplate
                .Replace("[NamePreferredOrFirst]", p.GetNameFirstOrPreferred())
                .Replace("?NamePreferredOrFirst", p.GetNameFirstOrPreferred())
                .Replace("[FName]", p.FName)
                .Replace("?FName", p.FName);

            if (a != null)
            {
                DateTime displayTime = a.AptDateTime;

                // If earlyMinutes is provided and greater than 0, adjust the time for display purposes only
                if (earlyMinutes.HasValue && earlyMinutes.Value > 0)
                {
                    displayTime = displayTime.AddMinutes(-earlyMinutes.Value);

                    ODSMSLogger.Instance.Log(
                        $"Rendering reminder with adjusted time for patient {p.PatNum}: " +
                        $"Original: {a.AptDateTime:yyyy-MM-dd HH:mm}, " +
                        $"Adjusted: {displayTime:yyyy-MM-dd HH:mm} (arrive {earlyMinutes.Value} minutes early)",
                        EventLogEntryType.Information,
                        logToFile: true);
                }

                s = s.Replace("[date]", displayTime.ToString("dddd, d MMMM yyyy"))
                     .Replace("[time]", displayTime.ToString("h:mm tt"));
            }

            return s;
        }


        // This is the core SMS handling including setup.
        public static async SystemTask InitializeAndRunSmsTasks()
        {
            var debugStatus = await ODSMSBridgeInterface.GetDebugStatus();
            DEBUG_MODE = debugStatus.IsDebugMode;
            LogConfigurationStatus(Environment.MachineName);

            ValidateSMSBridgeName();
            await ValidateSMSBridge();
            DetectEnvironment();

            ODSMSLogger.Instance.Log(
                $"Open Dental SMS running from commit {BuildInfo.GitCommit}",
                EventLogEntryType.Information,
                logToEventLog: false,
                logToFile: true
            );

            // Now SMS is initialized, proceed with dependent tasks
            if (ODSMS.IS_MAIN_SMS_MACHINE)    // This is the computer for scheduled SMS and for receiving SMS
            {
                await ODSMS.WaitForDatabaseAndUserInitialization();  // Can't access SMS constants without DB access
                await ODSMS.InitializeSMS();                         // Load the enum constants
                if (DEBUG_MODE)
                {

                    if (!string.IsNullOrEmpty(debugStatus.TestingPhoneNumber))
                    {
                        MessageBox.Show($"SMS is in DEBUG mode - messages redirected to {debugStatus.TestingPhoneNumber}\nAllowed test numbers: {string.Join(", ", debugStatus.AllowedTestNumbers)}");

                        await ODSMSBridgeInterface.SendSmsViaHttp(
                            debugStatus.TestingPhoneNumber,
                            "Test message from Open Dental startup"
                        );
                    }
                    else
                    {
                        throw new InvalidOperationException("SMS is in DEBUG mode, but no testing phone number is configured.");
                    }
                }
                else
                {
                    MessageBox.Show("This computer will send/receive SMS");
                }

                _ = System.Threading.Tasks.Task.Run(async () => await ODSMSBridgeInterface.ManageScheduledSMSSending());
                _ = System.Threading.Tasks.Task.Run(async () => await ODSMSBridgeInterface.ManageScheduledSMSReceiving());
            }


        }

    }
}