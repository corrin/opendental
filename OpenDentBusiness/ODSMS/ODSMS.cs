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
using OpenDentBusiness.ODSMS;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenDentBusiness.UI;
using System.Net.Sockets;
using System.Net;
using System.Web.Services.Description;
using OpenDentalBusiness;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;


namespace OpenDentBusiness.ODSMS
{
   public static class SmsTemplateKeys
   {
       public const string TwoWeekReminder = "TwoWeekReminder";
       public const string OneWeekReminder = "OneWeekReminder";
       public const string DayBeforeReminder = "DayBeforeReminder";
       public const string PostOp = "PostOpTxt";
       public const string Birthday = "Birthday";
   }

    public class SmsTemplateData
    {
        public string NoteID { get; set; }
        public string TemplateText { get; set; }
        public bool IsEnabled { get; set; }
    }
 
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
        // IMPORTANT: THis is set based on whether the app is compiled in DEBUG mode or not.
        // So 

#if DEBUG
        public const bool DEBUG_MODE = true;
#else
        public const bool DEBUG_MODE = false;
#endif


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
        public static long _defNumLeftMsg;
        public static long _defNumEmailed;

        public static Dictionary<string, SmsTemplateData> TemplateCache;

        // Template mapping for reminder types
        internal static readonly Dictionary<ReminderFilterType, string> ReminderTemplateKeys = new Dictionary<ReminderFilterType, string>
        {
            { ReminderFilterType.TwoWeeks, SmsTemplateKeys.TwoWeekReminder },
            { ReminderFilterType.OneWeek, SmsTemplateKeys.OneWeekReminder },
            { ReminderFilterType.OneDay, SmsTemplateKeys.DayBeforeReminder }
        };

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
            sharedClient.Timeout = TimeSpan.FromSeconds(15);
            // LoadTemplatesFromSheets();

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
            if (DEBUG_MODE)
            {
                ODSMSLogger.Instance.Log("Running in debug mode - no SMS to patients", EventLogEntryType.Information);
            }
            else
            {
                ODSMSLogger.Instance.Log("Running in production mode", EventLogEntryType.Information);
            }
            // No point checking for production SMS

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
            _defNumLeftMsg = GetAndCheckDefNum("LeftMsg", _listDefsApptConfirmed);
            _defNumEmailed = GetAndCheckDefNum("E-mailed", _listDefsApptConfirmed);
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
            ODSMSLogger.Instance.Log("Running custom build of Open Dental on " + Environment.MachineName, EventLogEntryType.Information, logToEventLog: true);
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
                ODSMSLogger.Instance.Log(message + " - the application will terminate.", EventLogEntryType.Error, logToEventLog: true);
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
                case "ETXT_API_KEY":
                case "ETXT_API_SECRET":
                case "ETXT_CALLBACK_KEY":
                    // handled in SMS Bridge
                    break;
                default:
                    ODSMSLogger.Instance.Log($"Unknown command in control file: {key}", EventLogEntryType.Information, logToEventLog: false);
                    break;

            }
        }

        private static bool ValidateSMSBridgeName()
        {
            if (string.IsNullOrWhiteSpace(SMS_BRIDGE_NAME))
            {
                throw new InvalidOperationException("BRIDGE is not set in the configuration file.");
            }

            IPAddress bridgeIp;

            // Case 1: BRIDGE is a literal IP
            if (IPAddress.TryParse(SMS_BRIDGE_NAME, out bridgeIp))
            {
                // silent — nothing worth logging
            }
            else
            {
                // Case 2: BRIDGE is a hostname — must resolve
                var hostEntry = Dns.GetHostEntry(SMS_BRIDGE_NAME);
                bridgeIp = hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

                if (bridgeIp == null)
                {
                    throw new InvalidOperationException($"Could not resolve an IPv4 address for {SMS_BRIDGE_NAME}");
                }

                ODSMSLogger.Instance.Log(
                    $"Resolved SMS bridge hostname '{SMS_BRIDGE_NAME}' to IP: {bridgeIp}",
                    EventLogEntryType.Information
                );
            }

            // Check if the resolved IP is within production range
            IsUsingProductionSMS = bridgeIp.ToString().StartsWith("192.168.192.");

            // Optional: if you need to know whether this machine IS the bridge, you can return or assign here
            var localIPs = Dns.GetHostEntry(Dns.GetHostName())
                              .AddressList
                              .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            if (localIPs.Any(ip => ip.Equals(bridgeIp)))
            {
                ODSMSLogger.Instance.Log("This machine is the configured SMS bridge.", EventLogEntryType.Information);
            }

            return true;
        }



        private static bool DoesReceiverMatchLocal(string receiver)
        {
            // We need to handle the possibility that receiver is alrady an IP address

            if (string.IsNullOrWhiteSpace(receiver))
                return false;

            try
            {
                // Resolve receiver to a usable IPv4 address
                IPAddress receiverIp;

                if (IPAddress.TryParse(receiver, out var parsedIp))
                {
                    receiverIp = parsedIp;
                }
                else
                {
                    var resolved = Dns.GetHostEntry(receiver)
                                      .AddressList
                                      .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

                    if (resolved == null)
                        return false;

                    receiverIp = resolved;
                }

                // Get all of this machine's IPv4 addresses
                var localIps = Dns.GetHostEntry(Dns.GetHostName())
                                  .AddressList
                                  .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);

                // Compare
                return localIps.Any(ip => ip.Equals(receiverIp));
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error resolving RECEIVER '{receiver}': {ex.Message}", EventLogEntryType.Error);
                return false;
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
            IS_MAIN_SMS_MACHINE = DoesReceiverMatchLocal(SMS_RECEIVER_NAME) || DEBUG_MODE;

            if (IS_MAIN_SMS_MACHINE)
            {
                ODSMSLogger.Instance.Log("Name matches, enabling SMS reception", EventLogEntryType.Information, logToEventLog: true, logToFile: true);
            }
            else
            {
                ODSMSLogger.Instance.Log("Not receiving SMS on this computer:" + MachineName, EventLogEntryType.Information, logToEventLog: true, logToFile: true);
            }

            ODSMSLogger.Instance.Log("Successfully loaded odsms.txt config file", EventLogEntryType.Information, logToEventLog: true, logToFile: true);
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
            try
            {
                var debugStatus = await ODSMSBridgeInterface.GetDebugStatus();

                ODSMSLogger.Instance.Log(
                                $"SMS Bridge debug mode set to {debugStatus}",
                                EventLogEntryType.Information,
                                logToEventLog: false,
                                logToFile: true
                            );
                LoadTemplatesFromSheets(); // This method now includes comprehensive validation

                LogConfigurationStatus(Environment.MachineName);

                ODSMSLogger.Instance.Log(
                    $"Open Dental SMS running from commit {BuildInfo.GitCommit}",
                    EventLogEntryType.Information,
                    logToEventLog: false,
                    logToFile: true
                );

                ValidateSMSBridgeName();
                await ValidateSMSBridge();
                DetectEnvironment();


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

            catch (Exception ex)
            {
                string msg = "SMS failed to initialize.\n\n" + ex.Message;
                ODSMSLogger.Instance.Log("SMS startup error: " + ex, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
                MessageBox.Show(msg, "SMS Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // Don't rethrow — app must continue
            }

        }

        public static void LoadTemplatesFromSheets()
        {

            string jsonPath = Path.Combine(AppContext.BaseDirectory, "google-service_account.json");
            var credential = GoogleCredential
                .FromFile(jsonPath)
                .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

            var sheetsService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ODSMS"
            });

            string spreadsheetId = "1iw_QxP9Isk3UuSEB3LXTUCZzQGXB8DedOttRBs6jJ0s";
            string range = "Messages!A1:D"; // Start from A1 to include headers

            var response = sheetsService.Spreadsheets.Values.Get(spreadsheetId, range).Execute();

            if (response.Values == null || response.Values.Count == 0)
            {
                ODSMSLogger.Instance.Log("No data found in spreadsheet", EventLogEntryType.Warning);
                TemplateCache = new Dictionary<string, SmsTemplateData>();
                return;
            }

            // Get header row and find column indices
            var headers = response.Values[0].Select(h => h?.ToString()?.Trim() ?? "").ToList();
            int noteIdIndex = headers.IndexOf("Note ID");
            int activeIndex = headers.IndexOf("Active");
            int noteTextIndex = headers.IndexOf("Note text");

            if (noteIdIndex == -1)
            {
                ODSMSLogger.Instance.Log($"Required column 'Note ID' not found. Available headers: {string.Join(", ", headers)}", EventLogEntryType.Error);
                TemplateCache = new Dictionary<string, OpenDentBusiness.ODSMS.SmsTemplateData>();
                return;
            }

            if (activeIndex == -1)
            {
                ODSMSLogger.Instance.Log($"Required column 'Active' not found. Available headers: {string.Join(", ", headers)}", EventLogEntryType.Error);
                TemplateCache = new Dictionary<string, OpenDentBusiness.ODSMS.SmsTemplateData>();
                return;
            }

            if (noteTextIndex == -1)
            {
                ODSMSLogger.Instance.Log($"Required column 'Note text' not found. Available headers: {string.Join(", ", headers)}", EventLogEntryType.Error);
                TemplateCache = new Dictionary<string, SmsTemplateData>();
                return;
            }

            TemplateCache = new Dictionary<string, SmsTemplateData>(StringComparer.OrdinalIgnoreCase);
            int skippedRows = 0;
            int loadedTemplates = 0;

            // Process data rows (skip header)
            for (int i = 1; i < response.Values.Count; i++)
            {
                var row = response.Values[i];

                if (row == null)
                {
                    ODSMSLogger.Instance.Log($"Row {i + 1}: Null row skipped", EventLogEntryType.Warning);
                    skippedRows++;
                    continue;
                }

                int maxIndex = Math.Max(noteIdIndex, Math.Max(activeIndex, noteTextIndex));
                if (row.Count <= maxIndex)
                {
                    ODSMSLogger.Instance.Log($"Row {i + 1}: Insufficient columns (has {row.Count}, needs {maxIndex + 1})", EventLogEntryType.Warning);
                    skippedRows++;
                    continue;
                }

                string noteId = row[noteIdIndex]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(noteId))
                {
                    ODSMSLogger.Instance.Log($"Row {i + 1}: Empty Note ID", EventLogEntryType.Warning);
                    skippedRows++;
                    continue;
                }

                string isActiveString = row[activeIndex]?.ToString()?.Trim();
                bool isEnabled = string.Equals(isActiveString, "TRUE", StringComparison.OrdinalIgnoreCase);

                string noteText = row[noteTextIndex]?.ToString();

                SmsTemplateData templateData = new SmsTemplateData
                {
                    NoteID = noteId,
                    TemplateText = noteText ?? "",
                    IsEnabled = isEnabled
                };

                TemplateCache[noteId] = templateData;
                loadedTemplates++;
                ODSMSLogger.Instance.Log($"Row {i + 1}: Loaded template '{noteId}' (Enabled: {isEnabled}, {noteText?.Length ?? 0} chars)", severity: EventLogEntryType.Information, logToEventLog: false);
            }

            // Validate all required templates exist and are enabled
            var requiredTemplates = new[]
            {
                SmsTemplateKeys.TwoWeekReminder,
                SmsTemplateKeys.OneWeekReminder,
                SmsTemplateKeys.DayBeforeReminder,
                SmsTemplateKeys.PostOp,
                SmsTemplateKeys.Birthday
            };

            foreach (var templateKey in requiredTemplates)
            {
                if (!TemplateCache.TryGetValue(templateKey, out var templateData) || string.IsNullOrWhiteSpace(templateData.TemplateText))
                {
                    throw new InvalidOperationException($"Required SMS template '{templateKey}' is missing or empty in the Google Sheet. SMS initialization failed.");
                }
            }

            ODSMSLogger.Instance.Log($"Template loading complete: {loadedTemplates} loaded, {skippedRows} skipped, {response.Values.Count - 1 - loadedTemplates - skippedRows} inactive", EventLogEntryType.Information);
        }

    }
}