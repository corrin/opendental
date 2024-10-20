using OpenDentBusiness.ODSMS;
using System.Diagnostics;
using System.Net;
using System;
using System.Linq;
using System.Windows;
using JustRemotePhone.RemotePhoneService;
using System.Windows.Interop;

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
                HandleInitialApplicationState(_appInstance.State, _appInstance.Phone.State);

                _appInstance.ApplicationStateChanged += OnApplicationStateChanged;
                _appInstance.Phone.SMSReceived += OnSmsReceived;
                _appInstance.Phone.PhoneStateChanged += OnPhoneStateChanged;
                //JustRemotePhoneTemporaryEventHandlers.RegisterAllTemporaryHandlers(_appInstance);

            }
        }

        // Handle the initial state of the application upon startup
        private static async void HandleInitialApplicationState(ApplicationState appState, PhoneState phoneState)
        {
            int maxRetries = 30; // Maximum number of retries, e.g., 30 * 500 ms = 15 seconds total

            // Wait until the state changes from "StartingCallCentre"
            while (appState == ApplicationState.StartingCallCenter && maxRetries > 0)
            {
                await System.Threading.Tasks.Task.Delay(500); // Wait for 500 ms before checking again
                appState = _appInstance.State; // Update the state after waiting
                maxRetries--; // Decrement retry count
            }

            if (appState != ApplicationState.Connected)
            {
                MessageBox.Show("We can't send SMS - check JustRemote on the phone.");
                ODSMSLogger.Instance.Log("SMS service is unavailable on startup.", EventLogEntryType.Error);
            }
            else
            {
                if (phoneState != PhoneState.Unknown)
                {
                    MessageBox.Show("SMS working correctly on startup.");
                    ODSMSLogger.Instance.Log("SMS service is operational on startup.", EventLogEntryType.Information);
                } else
                {
                    MessageBox.Show("JustRemote can't find the phone");
                    ODSMSLogger.Instance.Log("Connected to JustRemote on startup, but JustRemote can't connect to the phone.", EventLogEntryType.Information);
                }
            }
        }

        public bool IsConnected()
        {
            return _appInstance != null &&
                   _appInstance.State == ApplicationState.Connected &&
                   _appInstance.Phone.State != PhoneState.Unknown;
        }


        // Test method for sending and receiving SMS messages
        public static void TestSendMessage()
        {
            ODSMSLogger.Instance.Log("Starting debug test for sending SMS ...", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);

            try
            {
                var testSmsHttp = new SmsToMobile
                {
                    MobilePhoneNumber = "+6421467784",
                    MsgText = "Debug test message from TestSendMessage() via HTTP"
                };
                bool success_http = SendSMS.SendSmsMessageAsync(testSmsHttp, forceHttpMode: true).GetAwaiter().GetResult();

                // Test direct mode
                var testSmsDirect = new SmsToMobile
                {
                    MobilePhoneNumber = "+6421467784",
                    MsgText = "Debug test message from TestSendMessage() directly"
                };
                bool success_direct = SendSMS.SendSmsMessageAsync(testSmsDirect, forceHttpMode: false).GetAwaiter().GetResult();

                // Log results for HTTP mode
                if (success_http)
                {
                    ODSMSLogger.Instance.Log("Debug test SMS sent successfully via HTTP", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                }
                else
                {
                    ODSMSLogger.Instance.Log("Failed to send debug test SMS via HTTP", EventLogEntryType.Warning, logToConsole: true, logToEventLog: true, logToFile: true);
                }

                // Log results for direct mode
                if (success_direct)
                {
                    ODSMSLogger.Instance.Log("Debug test SMS sent successfully via direct mode", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                }
                else
                {
                    ODSMSLogger.Instance.Log("Failed to send debug test SMS via direct mode", EventLogEntryType.Warning, logToConsole: true, logToEventLog: true, logToFile: true);
                }

                // Log final statuses
                ODSMSLogger.Instance.Log($"Debug test SMS (HTTP) final status: {testSmsHttp.SmsStatus}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                ODSMSLogger.Instance.Log($"Debug test SMS (Direct) final status: {testSmsDirect.SmsStatus}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("An error occurred during debug testing: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }

            ODSMSLogger.Instance.Log("Debug test completed.", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
        }
        // Local method for sending SMS using JustRemotePhone
        public static void SendSmsLocal(string phoneNumber, string message)
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
            ODSMSLogger.Instance.Log($"Application State changed from {oldState} to {newState}", EventLogEntryType.Information);

            // Guard clause: ignore transitions involving "StartingCallCentre" as old state
            if (oldState == ApplicationState.StartingCallCenter)
            {
                return;
            }

            if (newState == ApplicationState.Connected && oldState != ApplicationState.Connected)
            {
                MessageBox.Show("JustRemote has been reconnected.");
                ODSMSLogger.Instance.Log("Reconnected with JustRemote app.", EventLogEntryType.Information);
            }
            // Handle transition to Disconnected or other non-working states
            else if (newState != ApplicationState.Connected && oldState == ApplicationState.Connected)
            {
                MessageBox.Show("Is the JustRemote app running?");
                ODSMSLogger.Instance.Log("JustRemote is disconnected or unavailable.", EventLogEntryType.Error);
            }
        }

        private static void OnPhoneStateChanged(PhoneState newState, PhoneState oldState)
        {
            ODSMSLogger.Instance.Log($"Phone State changed from {oldState} to {newState}", EventLogEntryType.Information);

            if (newState == PhoneState.Idle)
            {
                ODSMSLogger.Instance.Log("Phone is now idle and ready for SMS operations.", EventLogEntryType.Information);
            }
            else if (newState == PhoneState.Unknown)
            {
                ODSMSLogger.Instance.Log("Phone state is unknown. SMS operations may be affected.", EventLogEntryType.Warning);
            }
        }


        private static async System.Threading.Tasks.Task ProcessSmsAsync(string number, string contactLabel, string text)
        {
            try
            {
                // TODO: Fix placeholders
                // Assuming msgTime and msgGUID need to be generated here
                DateTime msgTime = DateTime.UtcNow; // Placeholder for message time
                Guid msgGUID = Guid.NewGuid(); // Placeholder for message GUID

                await ReceiveSMS.ProcessSmsMessage(number, text, msgTime, msgGUID);

                // TODO: Consider deleting the SMS after successful processing
                // TODO: Handle asking JustRemote for the SMS message status
            }
            catch (Exception ex)
            {
                // Log any error that occurs during the message processing
                ODSMSLogger.Instance.Log("Error processing received SMS: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }
            return;
        }


        // Event handler for receiving SMS
        private static void OnSmsReceived(string number, string contactLabel, string text)
        {
            try
            {
                // TODO: What about deleting the SMS after processing it?
                ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                _ = ProcessSmsAsync(number, contactLabel, text);

            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("Error processing received SMS: " + ex.Message, EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
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

            if (string.IsNullOrEmpty(ODSMS.SMS_BRIDGE_NAME))
            {
                throw new InvalidOperationException("SMS_BRIDGE_NAME is not set. Check your configuration.");
            }

            string baseUrl = $"http://{ODSMS.SMS_BRIDGE_NAME}:8080/";
            listener.Start();
            ODSMSLogger.Instance.Log($"Initialized shared HttpClient with base address: {baseUrl}", EventLogEntryType.Information);

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

    }


public static class JustRemotePhoneTemporaryEventHandlers
    {
        // Register all event handlers for easy addition and removal
        public static void RegisterAllTemporaryHandlers(JustRemotePhone.RemotePhoneService.Application appInstance)
        {
            appInstance.ApplicationStateChanged += OnApplicationStateChanged;
            appInstance.Phone.PhoneActionStateChanged += OnPhoneActionStateChanged;
            appInstance.Phone.SMSReceived += OnSMSReceived;
            appInstance.Phone.PhoneStateChanged += OnPhoneStateChanged;
            appInstance.Phone.PropertyChanged += OnPropertyChanged;
            appInstance.Phone.ActiveNumberChanged += OnActiveNumberChanged;
            appInstance.Phone.NumbersForCreateSMSPendingChanged += OnNumbersForCreateSMSPendingChanged;
            appInstance.Phone.SMSSendResult += OnSMSSendResult;
        }

        private static void OnApplicationStateChanged(ApplicationState newState, ApplicationState oldState)
        {
            ODSMSLogger.Instance.Log($"Application State changed from {oldState} to {newState}", EventLogEntryType.Information);
        }

        private static void OnPhoneActionStateChanged(PhoneActionState newState, PhoneActionState oldState)
        {
            ODSMSLogger.Instance.Log($"Phone action state changed from {oldState} to {newState}", EventLogEntryType.Information);
        }


        private static void OnSMSReceived(string number, string contactLabel, string text)
        {
            ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information);
        }

        private static void OnPhoneStateChanged(PhoneState newState, PhoneState oldState)
        {
            ODSMSLogger.Instance.Log($"Phone State changed from {oldState} to {newState}", EventLogEntryType.Information);
        }

        private static void OnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            ODSMSLogger.Instance.Log($"Property {e.PropertyName} changed.", EventLogEntryType.Information);
        }

        private static void OnActiveNumberChanged(string newActiveNumber, string oldActiveNumber)
        {
            ODSMSLogger.Instance.Log($"Active number changed from {oldActiveNumber} to {newActiveNumber}", EventLogEntryType.Information);
        }
        private static void OnNumbersForCreateSMSPendingChanged(string[] newNumbers, string[] oldNumbers)
        {
            ODSMSLogger.Instance.Log($"Numbers for creating SMS changed. New: {string.Join(", ", newNumbers)}, Old: {string.Join(", ", oldNumbers)}", EventLogEntryType.Information);
        }

        private static void OnSMSSendResult(Guid smsSendRequestId, string[] numbers, SMSSentResult[] results)
        {
            for (int i = 0; i < numbers.Length; i++)
            {
                string result = results[i].ToString();
                ODSMSLogger.Instance.Log($"SMS send result for {numbers[i]}: {result}", EventLogEntryType.Information);
            }
        }
    }


}