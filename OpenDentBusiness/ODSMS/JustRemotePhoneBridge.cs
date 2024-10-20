using OpenDentBusiness.ODSMS;
using System.Diagnostics;
using System.Net;
using System;
using System.Linq;
using System.Windows;
using JustRemotePhone.RemotePhoneService;
using System.Windows.Interop;
using Word;
using System.Collections.Generic;
using System.Net.Http;

namespace OpenDentBusiness.ODSMS
{
    public class JustRemotePhoneBridge
    {
        // Ensure a single shared instance of the JustRemotePhone application
        private static JustRemotePhone.RemotePhoneService.Application _appInstance = null;
        private static HttpListener listener;

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
            System.Threading.Tasks.Task.Run(() => TestHttpListener().Wait()).Wait();

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

        public static async System.Threading.Tasks.Task TestHttpListener()
        {
            ODSMSLogger.Instance.Log("Starting HTTP Listener Test", EventLogEntryType.Information);

            // Test root endpoint
            try
            {
                var response = await ODSMS.sharedClient.GetAsync("/");
                string content = await response.Content.ReadAsStringAsync();
                ODSMSLogger.Instance.Log($"Root endpoint response: {response.StatusCode}, Content: {content}", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error testing root endpoint: {ex.Message}", EventLogEntryType.Error);
            }

            // Test smsStatus endpoint
            try
            {
                var response = await ODSMS.sharedClient.GetAsync("/smsStatus");
                string content = await response.Content.ReadAsStringAsync();
                ODSMSLogger.Instance.Log($"SMS Status endpoint response: {response.StatusCode}, Content: {content}", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error testing smsStatus endpoint: {ex.Message}", EventLogEntryType.Error);
            }

            // Test sendSms endpoint
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
            new KeyValuePair<string, string>("phoneNumber", "+1234567890"),
            new KeyValuePair<string, string>("message", "Test SMS from HTTP Listener")
        });

                var response = await ODSMS.sharedClient.PostAsync("/sendSms", content);
                string responseContent = await response.Content.ReadAsStringAsync();
                ODSMSLogger.Instance.Log($"Send SMS endpoint response: {response.StatusCode}, Content: {responseContent}", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error testing sendSms endpoint: {ex.Message}", EventLogEntryType.Error);
            }

            ODSMSLogger.Instance.Log("HTTP Listener Test Completed", EventLogEntryType.Information);
        }

        // Local method for sending SMS using JustRemotePhone
        public static Guid SendSMSviaJustRemote(string phoneNumber, string message)
        {
            Guid sendSMSRequestId;
            _appInstance.Phone.SendSMS(new string[] { phoneNumber }, message, out sendSMSRequestId);
            ODSMSLogger.Instance.Log($"SMS Sent to {phoneNumber}: {message} - ID: {sendSMSRequestId}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            return sendSMSRequestId;
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
            ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);

            // Start processing the SMS asynchronously
            _ = ProcessSMSReceivedAsync(number, contactLabel, text);
        }

        private static async System.Threading.Tasks.Task ProcessSMSReceivedAsync(string number, string contactLabel, string text)
        {
            try
            {
                DateTime msgTime = DateTime.UtcNow;
                Guid msgGUID = Guid.NewGuid();

                await ReceiveSMS.ProcessSmsMessage(number, text, msgTime, msgGUID);

                ODSMSLogger.Instance.Log($"Successfully processed SMS from {number}", EventLogEntryType.Information);

                // TODO: Implement SMS deletion logic here if required
                // TODO: Handle asking JustRemote for the SMS message status
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error processing received SMS from {number}: {ex.Message}", EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
            }

        }


        // Launches the Web Server which lets a remote OpenDental client communicate with this instance
        public static void LaunchWebServer()
        {
            try
            {
                listener = new HttpListener();


                string baseUrl = $"http://{ODSMS.SMS_BRIDGE_NAME}:{ODSMS.WEBSERVER_PORT}/";
                listener.Prefixes.Add(baseUrl);

                listener.Start();
                ODSMSLogger.Instance.Log($"HTTP Listener started successfully on {baseUrl}", EventLogEntryType.Information);

                System.Threading.Tasks.Task.Run(HandleIncomingRequestsLoop);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Failed to start HTTP Listener: {ex.Message}", EventLogEntryType.Error, logToConsole: true, logToEventLog: true, logToFile: true);
                MessageBox.Show("Technical Issue sharing SMS with other computers in the practice");
                throw; // Rethrow to allow the application to handle startup failures appropriately
            }

        }


        private static async System.Threading.Tasks.Task HandleIncomingRequestsLoop()
        {
            while (listener.IsListening)
            {
                ODSMSLogger.Instance.Log("Waiting for incoming request...", EventLogEntryType.Information);
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                    ODSMSLogger.Instance.Log($"Request received from: {context.Request.RemoteEndPoint}", EventLogEntryType.Information);
                }
                catch (Exception ex)
                {
                    ODSMSLogger.Instance.Log($"Error receiving request: {ex.Message}", EventLogEntryType.Error);
                    continue;
                }

                await ProcessRequestAsync(context);
            }
        }

        private static async System.Threading.Tasks.Task ProcessRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            ODSMSLogger.Instance.Log($"Request received: {request.HttpMethod} {request.Url.AbsolutePath}", EventLogEntryType.Information);

            switch (request.Url.AbsolutePath)
            {
                case "/":
                    ODSMSLogger.Instance.Log("Root path accessed", EventLogEntryType.Information);
                    await WriteResponseAsync(response, "This web server is up and running", HttpStatusCode.OK);
                    break;

                case "/smsStatus":
                    ODSMSLogger.Instance.Log("SMS Status endpoint accessed", EventLogEntryType.Information);
                    if (ValidateApiKey(request))
                    {
                        string status = ODSMS.CheckSMSConnection() ? "SMS service is connected and operational" : "SMS service is currently unavailable";
                        ODSMSLogger.Instance.Log($"SMS Status: {status}", EventLogEntryType.Information);
                        await WriteResponseAsync(response, status, HttpStatusCode.OK);
                    }
                    else
                    {
                        ODSMSLogger.Instance.Log("Unauthorized access attempt to SMS Status endpoint", EventLogEntryType.Warning);
                        await WriteResponseAsync(response, "Unauthorized", HttpStatusCode.Unauthorized);
                    }
                    break;

                case "/sendSms":
                    ODSMSLogger.Instance.Log("Send SMS endpoint accessed", EventLogEntryType.Information);
                    if (ValidateApiKey(request))
                    {
                        await HandleSendSmsRequest(request, response);
                    }
                    else
                    {
                        ODSMSLogger.Instance.Log("Unauthorized access attempt to Send SMS endpoint", EventLogEntryType.Warning);
                        await WriteResponseAsync(response, "Unauthorized", HttpStatusCode.Unauthorized);
                    }
                    break;

                default:
                    ODSMSLogger.Instance.Log($"Unsupported endpoint accessed: {request.Url.AbsolutePath}", EventLogEntryType.Warning);
                    await WriteResponseAsync(response, "Endpoint Not Found", HttpStatusCode.NotFound);
                    break;
            }
        }

        private static bool ValidateApiKey(HttpListenerRequest request)
        {
            bool isValid = request.Headers.AllKeys.Contains("ApiKey") && request.Headers["ApiKey"] == ODSMS.WEBSERVER_API_KEY;
            ODSMSLogger.Instance.Log($"API Key validation: {(isValid ? "Successful" : "Failed")}", isValid ? EventLogEntryType.Information : EventLogEntryType.Warning);
            return isValid;
        }

        private static async System.Threading.Tasks.Task HandleSendSmsRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            string phoneNumber = request.QueryString["phoneNumber"];
            string message = request.QueryString["message"];

            ODSMSLogger.Instance.Log($"Attempting to send SMS to: {phoneNumber}", EventLogEntryType.Information);

            if (string.IsNullOrEmpty(phoneNumber) || string.IsNullOrEmpty(message))
            {
                ODSMSLogger.Instance.Log("Invalid SMS request: missing phone number or message", EventLogEntryType.Warning);
                await WriteResponseAsync(response, "Missing phone number or message", HttpStatusCode.BadRequest);
                return;
            }

            try
            {
                Guid smsRequestID;
                smsRequestID = SendSMSviaJustRemote(phoneNumber, message);
                if (smsRequestID == null)
                {
                    ODSMSLogger.Instance.Log($"SMS send unsuccessful to {phoneNumber}", EventLogEntryType.Information);
                }
                else
                {
                    ODSMSLogger.Instance.Log($"SMS sent successfully to {phoneNumber}", EventLogEntryType.Information);
                }
                await WriteResponseAsync(response, "SMS Sent Successfully", HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error sending SMS: {ex.Message}", EventLogEntryType.Error);
                await WriteResponseAsync(response, "Error sending SMS", HttpStatusCode.InternalServerError);
            }
        }

        private static async System.Threading.Tasks.Task WriteResponseAsync(HttpListenerResponse response, string message, HttpStatusCode statusCode)
        {
            try
            {
                response.StatusCode = (int)statusCode;
                response.ContentType = "text/plain";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();

                ODSMSLogger.Instance.Log($"Response sent: Status {statusCode}, Message: {message}", EventLogEntryType.Information);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error writing response: {ex.Message}", EventLogEntryType.Error);
            }
        }


        private static async System.Threading.Tasks.Task WriteResponseAsync(HttpListenerResponse response, string message)
        {
            try
            {
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
                response.ContentLength64 = buffer.Length;
                using (var output = response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error writing response: {ex.Message}", EventLogEntryType.Error);
            }
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