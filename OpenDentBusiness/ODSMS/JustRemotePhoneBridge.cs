using OpenDentBusiness.ODSMS;
using System.Diagnostics;
using System.Net;
using System;
using System.Linq;
using System.Windows;
using JustRemotePhone.RemotePhoneService;

namespace OpenDentBusiness.ODSMS
{
    public class JustRemotePhoneBridge
    {
        // Ensure a single shared instance of the JustRemotePhone application
        private static JustRemotePhone.RemotePhoneService.Application _appInstance = null;

        // Constructor to initialize the JustRemotePhone application
        public static void InitializeBridge()
        {
            if (_appInstance == null)
            {
                _appInstance = new JustRemotePhone.RemotePhoneService.Application("Open Dental");
                _appInstance.BeginConnect(true);
                HandleInitialApplicationState(_appInstance.State);

                _appInstance.ApplicationStateChanged += new ApplicationStateChangedDelegate(OnApplicationStateChanged);
                _appInstance.Phone.SMSReceived += OnSmsReceived;
            }
        }

        // Handle the initial state of the application upon startup
        private static void HandleInitialApplicationState(ApplicationState state)
        {
            if (state != ApplicationState.Connected)
            {
                MessageBox.Show("We can't send SMS - check JustRemote on the phone.");
                ODSMSLogger.Instance.Log("SMS service is unavailable on startup.", EventLogEntryType.Error);
            }
            else
            {
                ODSMSLogger.Instance.Log("SMS service is operational on startup.", EventLogEntryType.Information);
            }
        }

        // Test method for sending and receiving SMS messages
        public static void TestSendAndReceive()
        {
            try
            {
                ODSMSLogger.Instance.Log("Starting one-off test for sending SMS before receiving...", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                // Perform a one-off SMS send as a test
                SendSmsLocal("+64211626986", "Test message from TestSendAndReceive()");

                // Start receiving SMS messages indefinitely
                ReceiveSMSForever(); // No await, as this runs forever
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("An error occurred during testing: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }
        }

        // Local method for sending SMS using JustRemotePhone
        private static void SendSmsLocal(string phoneNumber, string message)
        {
            try
            {
                // Send SMS using the JustRemotePhone instance
                Guid sendSMSRequestId;
                _appInstance.Phone.SendSMS(new string[] { phoneNumber }, message, out sendSMSRequestId);
                ODSMSLogger.Instance.Log($"SMS Sent to {phoneNumber}: {message}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("Error Sending SMS: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }
        }

        // Event handler for ApplicationState changes (phone connected etc)
        private static void OnApplicationStateChanged(ApplicationState newState, ApplicationState oldState)
        {
            // Handle transition to Connected
            if (newState == ApplicationState.Connected && oldState != ApplicationState.Connected)
            {
                MessageBox.Show("We can send SMS again.");
                ODSMSLogger.Instance.Log("SMS service is connected and operational.", EventLogEntryType.Information);
            }
            // Handle transition to Disconnected or other non-working states
            else if (newState != ApplicationState.Connected && oldState == ApplicationState.Connected)
            {
                MessageBox.Show("We can no longer send SMS - check JustRemote on the phone.");
                ODSMSLogger.Instance.Log("SMS service is disconnected or unavailable.", EventLogEntryType.Error);
            }
        }

        // Event handler for receiving SMS
        private static void OnSmsReceived(string number, string contactLabel, string text)
        {
            try
            {
                ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                // Add logic to process received SMS messages here
                ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("Error processing received SMS: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
                ODSMSLogger.Instance.Log("Error processing received SMS: " + ex.Message, EventLogEntryType.Error);
            }
        }

        // Launches the Web Server which lets a remote OpenDental client communicate with this instance
        public static void LaunchWebServer()
        {
            try
            {
                StartHttpListener();
            }
            catch (Exception ex)
            {
                // Add proper logging or handling here as needed
                ODSMSLogger.Instance.Log("Error Launching Web Server: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }
        }

        // Starts an HTTP listener to handle incoming requests
        private static void StartHttpListener()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/");
            listener.Start();
            ODSMSLogger.Instance.Log("Web Server started at http://localhost:8080/", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);

            System.Threading.Tasks.Task.Run(() =>
            {
                while (true)
                {
                    HttpListenerContext context = listener.GetContext();
                    HttpListenerRequest request = context.Request;
                    HttpListenerResponse response = context.Response;

                    // Validate API Key Header
                    if (!request.Headers.AllKeys.Contains("ApiKey") || request.Headers["ApiKey"] != ODSMS.WEBSERVER_API_KEY)
                    {
                        response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        using (var writer = new System.IO.StreamWriter(response.OutputStream))
                        {
                            writer.Write("Unauthorized");
                        }
                        continue;
                    }

                    // Handle SMS sending endpoint
                    if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/sendSms")
                    {
                        string phoneNumber = request.QueryString["phoneNumber"];
                        string message = request.QueryString["message"];

                        if (string.IsNullOrEmpty(phoneNumber) || string.IsNullOrEmpty(message))
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            using (var writer = new System.IO.StreamWriter(response.OutputStream))
                            {
                                writer.Write("Missing phone number or message");
                            }
                            continue;
                        }

                        // Use local method to send SMS
                        SendSmsLocal(phoneNumber, message);
                        response.StatusCode = (int)HttpStatusCode.OK;
                        using (var writer = new System.IO.StreamWriter(response.OutputStream))
                        {
                            writer.Write("SMS Sent Successfully");
                        }
                    }
                    else
                    {
                        // Default response for unsupported endpoints
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        using (var writer = new System.IO.StreamWriter(response.OutputStream))
                        {
                            writer.Write("Endpoint Not Found");
                        }
                    }
                }
            });
        }

        // Method to poll for incoming SMS messages indefinitely
        public static async System.Threading.Tasks.Task ReceiveSMSForever()
        {

            while (true)
            {
                await System.Threading.Tasks.Task.Delay(60 * 1000); // Check SMS once a minute
                ODSMSLogger.Instance.Log("Checking for new SMS now", EventLogEntryType.Information, logToEventLog: false, logToFile: false);

                try
                {
                    bool smsIsWorking = await ODSMS.CheckSMSConnection();
                    if (smsIsWorking)
                    {
                        try
                        {
                            ODSMS.EnsureSmsFolderExists();
                            bool success = await FetchAndProcessSmsMessages();
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Receiving patient texts failed.");
                            ODSMSLogger.Instance.Log(ex.ToString(), EventLogEntryType.Error);
                        }
                    }
                    else
                    {
                        HandleSMSDowntime();
                    }
                }
                catch (Exception ex)
                {
                    ODSMSLogger.Instance.Log("Error checking SMS connection: " + ex.Message, EventLogEntryType.Error);
                }
            }
        }

        // Placeholder method for fetching and processing SMS messages
        private static async System.Threading.Tasks.Task<bool> FetchAndProcessSmsMessages()
        {
            // Add logic to fetch and process SMS messages here
            await System.Threading.Tasks.Task.CompletedTask;
            ODSMSLogger.Instance.Log("Fetched and processed new SMS messages", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            return true;
        }

        // Placeholder method to handle SMS downtime
        private static void HandleSMSDowntime()
        {
            ODSMSLogger.Instance.Log("SMS connection is down. Handling downtime accordingly.", EventLogEntryType.Warning, logToConsole: true, logToEventLog: true, logToFile: true);
        }
    }
}