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
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace OpenDentBusiness.ODSMS
{
    public class JustRemotePhoneBridge
    {
        // Ensure a single shared instance of the JustRemotePhone application
        private static JustRemotePhone.RemotePhoneService.Application _appInstance = null;
        private static HttpListener listener;

        private static JustRemotePhoneBridge _instance = null;
        private static readonly object _lock = new object();
        private DateTime _lastSentTime = DateTime.Now;

        private readonly Dictionary<Guid, TaskCompletionSource<bool>> _pendingSms = new Dictionary<Guid, TaskCompletionSource<bool>>();

        public static JustRemotePhoneBridge Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new JustRemotePhoneBridge();
                        }
                    }
                }
                return _instance;
            }
        }
        public int CooldownUntilNextSMS()
        {
            TimeSpan timeSinceLastSent = DateTime.Now - _lastSentTime;
            int cooldown = 30 - (int)timeSinceLastSent.TotalSeconds;

            if (cooldown < 0)
            {
                cooldown = 0; // No cooldown if enough time has passed
            }

            return cooldown; ;
        }



        // Constructor to initialize the JustRemotePhone application
        public static async System.Threading.Tasks.Task InitializeBridge()
        {
            if (_appInstance == null)
            {
                _appInstance = new JustRemotePhone.RemotePhoneService.Application("Open Dental");
                _appInstance.BeginConnect(true);
                await HandleInitialApplicationState();

                _appInstance.ApplicationStateChanged += OnApplicationStateChanged;
                _appInstance.Phone.SMSReceived += OnSmsReceived;  
                _appInstance.Phone.SMSSendResult += Instance.OnSmsStatusReceived;   
                _appInstance.Phone.PhoneStateChanged += OnPhoneStateChanged;
                //JustRemotePhoneTemporaryEventHandlers.RegisterAllTemporaryHandlers(_appInstance);

            }
        }

        // Handle the initial state of the application upon startup
        private static async System.Threading.Tasks.Task HandleInitialApplicationState()
        {
            int maxRetries = 30; // Maximum number of retries, e.g., 30 * 500 ms = 15 seconds total. // BUG: This is not waiting
            int currentRetries = 0;
            // Wait until the state changes from "StartingCallCentre"
            while (_appInstance.State == ApplicationState.StartingCallCenter && currentRetries < maxRetries)
            {
                await System.Threading.Tasks.Task.Delay(500); // Wait for 500 ms before checking again
                currentRetries++; // Decrement retry count
            }

            if (_appInstance.State == ApplicationState.StartingCallCenter)
            {
                // We never left the "StartingCallCenter" state
                ODSMSLogger.Instance.Log("Application did not leave the StartingCallCenter state after maximum retries.", EventLogEntryType.Error);
                return;
            }

            if (_appInstance.State != ApplicationState.Connected)
            {
                MessageBox.Show("We can't send SMS - check JustRemote on the phone.");
                ODSMSLogger.Instance.Log("SMS service is unavailable on startup.", EventLogEntryType.Error);
            }
            else
            {
                await System.Threading.Tasks.Task.Delay(1000); // Wait for 1s before checking the phone state

                if (_appInstance.Phone.State != PhoneState.Unknown)
                {
                    ODSMSLogger.Instance.Log("SMS service is operational on startup.", EventLogEntryType.Information);
                }
                else
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
            // System.Threading.Tasks.Task.Run(() => TestHttpListener()).Wait();
            TestBulkSend(5);
            //System.Threading.Tasks.Task.Run(() => TestSendSMS()).Wait();

            ODSMSLogger.Instance.Log("Debug test completed.", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
        }

        private static async System.Threading.Tasks.Task TestSendSMS(bool doDirect = true, bool doViaHTTP = true)
        {
            var testSmsHttp = new SmsToMobile
            {
                MobilePhoneNumber = "+6421467784",
                MsgText = "Debug test message from TestSendMessage() via HTTP"
            };

            if (doViaHTTP)
            {
                bool success_http = await SendSMS.SendSmsMessageAsync(testSmsHttp, forceHttpMode: true);
                if (success_http)
                {
                    ODSMSLogger.Instance.Log("Debug test SMS sent successfully via HTTP", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                }
                else
                {
                    ODSMSLogger.Instance.Log("Failed to send debug test SMS via HTTP", EventLogEntryType.Warning, logToConsole: true, logToEventLog: true, logToFile: true);
                }
                ODSMSLogger.Instance.Log($"Debug test SMS (HTTP) final status: {testSmsHttp.SmsStatus}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);

            }

            // Test direct mode
            var testSmsDirect = new SmsToMobile
            {
                MobilePhoneNumber = "+6421467784",
                MsgText = "Debug test message from TestSendMessage() directly"
            };
            if (doDirect)
            {
                bool success_direct = await SendSMS.SendSmsMessageAsync(testSmsDirect, forceHttpMode: false);
                if (success_direct)
                {
                    ODSMSLogger.Instance.Log("Debug test SMS sent successfully via direct mode", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                }
                else
                {
                    ODSMSLogger.Instance.Log("Failed to send debug test SMS via direct mode", EventLogEntryType.Warning, logToConsole: true, logToEventLog: true, logToFile: true);
                }
                ODSMSLogger.Instance.Log($"Debug test SMS (Direct) final status: {testSmsDirect.SmsStatus}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            }
        }

        public static void TestBulkSend(int n = 25)
        {
            if (n == 0)
                return;
            // Construct a list of 25 messages
            var listSmsToMobileMessages = new List<SmsToMobile>();

            for (int i = 1; i <= n; i++)
            {
                // Constructing the SmsToMobile message
                var msg = new SmsToMobile
                {
                    MobilePhoneNumber = "+64211626986",
                    MsgText = $"Test message from OD: {i} of {n}"
                };

                // Add to the list of messages
                listSmsToMobileMessages.Add(msg);
            }

            // Call the SendSms method with the list of messages
            List<SmsToMobile> successfulMessages = SmsToMobiles.SendSms(listSmsToMobileMessages);

            // Print out the result to confirm which messages were sent successfully
            Console.WriteLine($"Successfully sent {successfulMessages.Count} out of {n} messages.");
            foreach (var msg in successfulMessages)
            {
                Console.WriteLine($"Message successfully sent: {msg.MsgText}");
            }
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
                    new KeyValuePair<string, string>("phoneNumber", "+6421467784"),
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
        public Guid SendSMSviaJustRemote(string phoneNumber, string message)
        {
            Guid sendSMSRequestId;
            _appInstance.Phone.SendSMS(new string[] { phoneNumber }, message, out sendSMSRequestId);
            ODSMSLogger.Instance.Log($"SMS Sent to {phoneNumber}: {message} - ID: {sendSMSRequestId}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            var tcs = new TaskCompletionSource<bool>();
            _pendingSms[sendSMSRequestId] = tcs;
            _lastSentTime = DateTime.Now;
            return sendSMSRequestId;
        }

        public async Task<bool> WaitForSmsStatusAsync(Guid requestId)
        {
            if (_pendingSms.TryGetValue(requestId, out var tcs))
            {
                return await tcs.Task;  // Await the result (true if successful, false if not)
            }

            // If the requestId isn't found, consider it a failure
            return false;
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


        // We assume that a patient sending the same text on the same minute has already been handled.   
        // I feel that's a sensible balance between wanting to ignore a patient texting back OK twice and a patient replying OK twice in a conversation
        private static string GenerateMessageHash(string msgFrom, string msgText, DateTime msgTime)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                //                var combinedString = $"{msgFrom}|{msgText}|{msgTime.Date.ToString("yyyy-MM-dd")}";
                var combinedString = $"{msgFrom}|{msgText}|{msgTime.ToString("yyyy-MM-dd HH:mm")}";
                var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combinedString));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }


        // Event handler for receiving SMS
        private static void OnSmsReceived(string number, string contactLabel, string text)
        {
            ODSMSLogger.Instance.Log($"Received SMS from {number} ({contactLabel}): {text}", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
            DateTime msgTime = DateTime.UtcNow;
            string msgGUID = GenerateMessageHash(number, text, msgTime);

            // Start processing the SMS asynchronously
            _ = ReceiveSMS.ProcessOneReceivedSMS(text, msgTime, number, msgGUID);

        }

        private void OnSmsStatusReceived(Guid smsSendRequestId, string[] numbers, SMSSentResult[] results)
        {
            if (_pendingSms.TryGetValue(smsSendRequestId, out var tcs))
            {
                // Assume success if all numbers have a successful result
                bool isSuccess = results.All(r => r == SMSSentResult.Ok);
                tcs.TrySetResult(isSuccess);
                _pendingSms.Remove(smsSendRequestId);
            }
        }



        // Launches the Web Server which lets a remote OpenDental client communicate with this instance
        public static void LaunchWebServer()
        {
            try
            {
                listener = new HttpListener();


                string baseUrl;
                //                baseUrl = $"http://{ODSMS.SMS_BRIDGE_NAME}:{ODSMS.WEBSERVER_PORT}/";
                baseUrl = $"http://+:{ODSMS.WEBSERVER_PORT}/";
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
            try
            {
                // Read the request body to get the form-encoded content
                using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding))
                {
                    string requestBody = await reader.ReadToEndAsync();
                    var parsedFormData = System.Web.HttpUtility.ParseQueryString(requestBody);

                    string phoneNumber = parsedFormData["phoneNumber"];
                    string message = parsedFormData["message"];

                    // Log the received data to verify
                    ODSMSLogger.Instance.Log($"Attempting to send SMS to: {phoneNumber}, Message: {message}", EventLogEntryType.Information);

                    if (string.IsNullOrEmpty(phoneNumber) || string.IsNullOrEmpty(message))
                    {
                        ODSMSLogger.Instance.Log("Invalid SMS request: missing phone number or message", EventLogEntryType.Warning);
                        response.StatusCode = (int)HttpStatusCode.BadRequest; // Set status code before writing response
                        await WriteResponseAsync(response, "Missing phone number or message", HttpStatusCode.BadRequest);
                        return;
                    }
                    else
                    {
                        Instance.SendSMSviaJustRemote(phoneNumber, message);
                        await WriteResponseAsync(response, "SMS sent successfully", HttpStatusCode.OK);
                        return;
                    }
                }

            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Exception in HandleSendSmsRequest: {ex.Message}", EventLogEntryType.Error);
                response.StatusCode = (int)HttpStatusCode.InternalServerError; // Set status code before writing response
                await WriteResponseAsync(response, "Internal Server Error", HttpStatusCode.InternalServerError);
            }
        }



        private static async System.Threading.Tasks.Task WriteResponseAsync(HttpListenerResponse response, string message, HttpStatusCode statusCode)
        {
            try
            {
                // Set the HTTP status code
                response.StatusCode = (int)statusCode;

                // Convert the message to a byte array
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
                response.ContentLength64 = buffer.Length;

                // Write the response body
                using (var output = response.OutputStream)
                {
                    await output.WriteAsync(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                // Log any errors during response writing
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