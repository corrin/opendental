using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Threading;
using System.Threading.Tasks;
using SystemTask = System.Threading.Tasks.Task;

namespace OpenDentBusiness.ODSMS
{
    public class SendSmsRequest
    {
        [JsonProperty("phoneNumber")]
        public string PhoneNumber { get; }

        [JsonProperty("message")]
        public string Message { get; }

        public SendSmsRequest(string phoneNumber, string message)
        {
            PhoneNumber = phoneNumber;
            Message = message;
        }
    }

    public class Result
    {
        [JsonProperty("success")]
        public bool Success { get; }

        [JsonProperty("message")]
        public string Message { get; }

        [JsonProperty("smsBridgeID")]
        public string SmsBridgeID { get; }

        [JsonIgnore]
        public Guid MessageID => Guid.Parse(SmsBridgeID);


        public Result(bool success, string message, string smsBridgeID = null)
        {
            Success = success;
            Message = message;
            SmsBridgeID = smsBridgeID;
        }
    }

    public class ReceivedSmsMessage
    {
        [JsonProperty("messageID")]
        public Guid MessageID { get; set; }

        [JsonProperty("providerMessageID")]
        public Guid ProviderMessageID { get; set; }

        [JsonProperty("fromNumber")]
        public string FromNumber { get; set; }

        [JsonProperty("messageText")]
        public string MessageText { get; set; }

        [JsonProperty("receivedAt")]
        public DateTime ReceivedAt { get; set; }
    }


    public class DebugStatusResponse
    {
        [JsonProperty("isDebugMode")]
        public bool IsDebugMode { get; set; }

        [JsonProperty("testingPhoneNumber")]
        public string TestingPhoneNumber { get; set; }

        [JsonProperty("allowedTestNumbers")]
        public string[] AllowedTestNumbers { get; set; }
    }

    public enum MessageStatus
    {
        Pending,
        Delivered,
        Failed
    }


    public class MessageStatusResponse
    {
        [JsonProperty("messageId")]
        public string MessageID { get; set; }

        [JsonProperty("status")]
        public MessageStatus Status { get; set; }
    }


    public class ODSMSBridgeInterface
    {
        private static DateTime lastBulkSent = DateTime.MinValue;

        public static async System.Threading.Tasks.Task<(bool Success, string MessageId)> SendSmsViaHttp(string phoneNumber, string message)
        {
            ODSMSLogger.Instance.Log($"Initiating HTTP SMS send to {phoneNumber}",
                EventLogEntryType.Information,
                logToEventLog: false);
            try
            {
                var request = new SendSmsRequest(phoneNumber, message);
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                ODSMSLogger.Instance.Log("Sending HTTP POST request to SMS server", EventLogEntryType.Information, logToEventLog: false);
                var response = await ODSMS.sharedClient
                    .PostAsync("send-sms", content)
                    .ConfigureAwait(false);
                var responseBody = await response.Content
                                .ReadAsStringAsync()
                                .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    ODSMSLogger.Instance.Log($"Failed to send SMS to {phoneNumber}. HTTP response: {responseBody}", EventLogEntryType.Warning);
                    return (false, string.Empty);
                }

                var result = JsonConvert.DeserializeObject<Result>(responseBody);

                if (result.Success)
                {
                    ODSMSLogger.Instance.Log($"SMS sent to {phoneNumber} at {DateTime.Now:d/MM/yyyy h:mm:ss tt} with body \"{message}\" - GUID: {result.SmsBridgeID}", EventLogEntryType.Information);
                }
                else
                {
                    ODSMSLogger.Instance.Log($"Failed to send SMS to {phoneNumber}. Bridge response: {responseBody}", EventLogEntryType.Warning);
                }

                return (result.Success, result.SmsBridgeID);
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Exception in SendSmsViaHttp: {ex.Message}", EventLogEntryType.Error);
                return (false, string.Empty);
            }
        }

        public static async Task<MessageStatus> GetMessageStatus(string messageId)
        // NOTE: This gives the status RIGHT NOW, including pending
        // You probably want to call WaitForMessageStatus.
        {
            try
            {
                var response = await ODSMS.sharedClient.GetAsync($"sms-status/{messageId}");
                if (!response.IsSuccessStatusCode)
                {
                    ODSMSLogger.Instance.Log($"Failed to get status: {response.StatusCode}", EventLogEntryType.Error);
                    return MessageStatus.Failed;
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<MessageStatusResponse>(json);
                return result.Status;
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error checking message status: {ex.Message}", EventLogEntryType.Error);
                return MessageStatus.Failed;
            }
        }


        public static bool IsLowestProcessId(string callerType)
        {
            try
            {
                // Get our process ID
                int currentPid = Process.GetCurrentProcess().Id;

                // Get all OpenDental processes
                var odProcesses = Process.GetProcessesByName("OpenDental");

                // Find the lowest PID
                int lowestPid = odProcesses.Min(p => p.Id);

                ODSMSLogger.Instance.Log(
                    $"PID Check from {callerType} - Current: {currentPid}, Lowest: {lowestPid}",
                    EventLogEntryType.Information, logToEventLog: false);

                return currentPid == lowestPid;
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log(
                    $"Error checking process IDs: {ex.Message}",
                    EventLogEntryType.Error);
                return false;
            }
        }



        private static int GetMinutesUntilQuarterPast(DateTime now)
        {
            // If it's already past :15, wait until next hour's :15
            if (now.Minute >= 15)
            {
                int minutesUntilNextHour = 60 - now.Minute;
                return minutesUntilNextHour + 15;
            }

            // Otherwise, it's before :15 — just count up to 15
            return 15 - now.Minute;
        }

        private static int GetSecondsUntilNextQuarterPast(DateTime now)
        {
            // Find the next hour where :15 will happen
            DateTime nextQuarterPast = new DateTime(now.Year, now.Month, now.Day, now.Hour, 15, 0);

            if (now.Minute >= 15)
            {
                // We've already passed :15 this hour, go to next hour's :15
                nextQuarterPast = nextQuarterPast.AddHours(1);
            }

            TimeSpan span = nextQuarterPast - now;
            return Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
        }


        public static async SystemTask ManageScheduledSMSSending()
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                int secondsToSleep;

                if (ODSMS.DEBUG_MODE)
                {
                    int secondsUntilNext5Min = ((5 - (now.Minute % 5)) * 60) - now.Second;
                    secondsToSleep = Math.Min(3600, Math.Max(1, secondsUntilNext5Min));  // not needed.  Safeguard in case secondsUntil gets a nonsense value
                    //int secondsUntilNextQuarterPast = GetSecondsUntilNextQuarterPast(now);
                    //secondsToSleep = Math.Min(3600, Math.Max(1, secondsUntilNextQuarterPast));  // not needed.  Safeguard in case secondsUntil gets a nonsense value

                }
                else
                {
                    int secondsUntilNextQuarterPast = GetSecondsUntilNextQuarterPast(now);
                    secondsToSleep = Math.Min(3600, Math.Max(1, secondsUntilNextQuarterPast));  // not needed.  Safeguard in case secondsUntil gets a nonsense value
                }

                await SystemTask.Delay(TimeSpan.FromSeconds(secondsToSleep));
                now = DateTime.Now; // Reset 'now', as it might have changed a lot during the sleep

                var minutesSince = now - lastBulkSent;

                // Only send if we haven't already sent this minute (ultra-conservative, but robust)
                if (now.Hour >= 8 && now.Hour < 17 && minutesSince >= TimeSpan.FromMinutes(50))
                {
                    if (IsLowestProcessId("sender"))
                    {
                        ODSMSLogger.Instance.Log("Checking and sending scheduled SMS", EventLogEntryType.Information, logToConsole: true, logToEventLog: true, logToFile: true);

                        SendSMS.SendReminderTexts();
                        SendSMS.SendBirthdayTexts();
                        SendSMS.SendProcedureFollowupTexts();
                        lastBulkSent = now;
                    }
                    else
                    {
                        ODSMSLogger.Instance.Log("Not sending SMS as multiple instances running", EventLogEntryType.Information, logToConsole: true, logToEventLog: false, logToFile: true);
                    }
                }
            }
        }

        public static async System.Threading.Tasks.Task<MessageStatus> WaitForMessageStatus(string messageId, int maxAttempts = 20, int delayMs = 1000)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                var status = await GetMessageStatus(messageId);
                ODSMSLogger.Instance.Log(
                    $"Status check attempt {i + 1}/{maxAttempts}: MessageId={messageId}, Status={status}",
                    EventLogEntryType.Information,
                    logToConsole: true,
                    logToEventLog: false,
                    logToFile: true);

                if (status != MessageStatus.Pending)
                {
                    return status;
                }
                await SystemTask.Delay(delayMs);
            }
            ODSMSLogger.Instance.Log(
                $"Status check timed out after {maxAttempts} attempts: MessageId={messageId}",
                EventLogEntryType.Warning);

            return MessageStatus.Failed; // Timeout
        }



        public static async SystemTask CheckForReceivedMessages()
        {
            try
            {
                var response = await ODSMS.sharedClient.GetAsync("received-sms");
                if (!response.IsSuccessStatusCode)
                {
                    ODSMSLogger.Instance.Log($"Failed to check for SMS: {response.StatusCode}", EventLogEntryType.Error);
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var messages = JsonConvert.DeserializeObject<List<ReceivedSmsMessage>>(json);
                // Bug in time handling.  Convert to datetimeoffset
                foreach (var message in messages)
                {
                    // Process each message
                    await ReceiveSMS.ProcessSmsMessage(
                        message.FromNumber,
                        message.MessageText,
                        message.ReceivedAt,
                        message.MessageID
                    );

                    // Delete the processed message
                    await DeleteReceivedMessage(message.MessageID.ToString());
                }
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error checking for new SMS: {ex.Message}", EventLogEntryType.Error);
            }
        }

        private static async SystemTask DeleteReceivedMessage(string messageId)
        {
            try
            {
                await ODSMS.sharedClient.GetAsync($"delete-received-sms/{messageId}");
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error deleting message {messageId}: {ex.Message}", EventLogEntryType.Error);
            }
        }


        public static async SystemTask ManageScheduledSMSReceiving()
        {
            while (true)
            {

                if (IsLowestProcessId("receiver"))
                {
                    await CheckForReceivedMessages();
                }

                await SystemTask.Delay(TimeSpan.FromMinutes(1));
            }
        }

        public static async Task<DebugStatusResponse> GetDebugStatus()
        {
            try
            {
                var response = await ODSMS.sharedClient.GetAsync("debug-status");
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to get debug status: {response.StatusCode}");
                }
                var json = await response.Content.ReadAsStringAsync();
                var debugStatus = JsonConvert.DeserializeObject<DebugStatusResponse>(json);
                if (debugStatus == null)
                {
                    throw new InvalidOperationException("Failed to deserialize debug status response");
                }
                return debugStatus;
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                      ex is TaskCanceledException ||
                                      ex is InvalidOperationException)
            {
                ODSMSLogger.Instance.Log($"Error checking debug status: {ex.Message}", EventLogEntryType.Error);

                // Display a clear error message to the user
                System.Windows.Forms.MessageBox.Show(
                    "SMS Bridge not running on the bridge machine!?\n\n" +
                    "Quit Open Dental if possible and fix this immediately.\n\n" +
                    "No SMS can be sent until fixed",
                    "SMS Bridge Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);

                // Important: Return IsDebugMode = false instead of true to prevent unexpected behavior
                return new DebugStatusResponse
                {
                    IsDebugMode = true,
                    TestingPhoneNumber = "",
                    AllowedTestNumbers = Array.Empty<string>()
                };
            }
        }

    }
}