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

        [JsonProperty("messageID")]
        public string MessageID { get; }

        public Result(bool success, string message, string messageID = null)
        {
            Success = success;
            Message = message;
            MessageID = messageID;
        }
    }

    public class ReceivedSmsMessage
    {
        [JsonProperty("messageId")]
        public string MessageID { get; set; }

        [JsonProperty("fromNumber")]
        public string FromNumber { get; set; }

        [JsonProperty("messageText")]
        public string MessageText { get; set; }

        [JsonProperty("receivedAt")]
        public DateTime ReceivedAt { get; set; } // Note, this is in UTC time
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
                var response = await ODSMS.sharedClient.PostAsync("send-sms", content);

                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    ODSMSLogger.Instance.Log($"HTTP response body: {responseBody}", EventLogEntryType.Warning);
                    return (false, string.Empty);
                }

                var result = JsonConvert.DeserializeObject<Result>(responseBody);
                return (true, result.MessageID);
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


        public static bool IsLowestProcessId()
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
                    $"PID Check - Current: {currentPid}, Lowest: {lowestPid}",
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
            int minutesToNextQuarter = (15 - now.Minute % 15) % 60;
            if (minutesToNextQuarter == 0)
            {
                minutesToNextQuarter = 60;
            }

            return Math.Max(1, minutesToNextQuarter);
        }

        public static async SystemTask ManageScheduledSMSSending()
        {
            while (true)
            {
                DateTime now = DateTime.Now;

                if (
                    // Debug Mode: Run every 5 minutes
                    (ODSMS.DEBUG_MODE && now.Minute % 5 == 0) ||

                    // Normal Mode: Run at quarter past the hour, between 8 AM and 5 PM
                    (!ODSMS.DEBUG_MODE &&
                     now.Minute >= 14 && now.Minute <= 16 && now.Hour >= 8 && now.Hour <= 17)
                )
                {
                    if (IsLowestProcessId())
                    {
                        SendSMS.SendReminderTexts();
                        SendSMS.SendBirthdayTexts();
                    }
                }

                int minutesUntilNextQuarterPast = GetMinutesUntilQuarterPast(now);
                await SystemTask.Delay(TimeSpan.FromMinutes(minutesUntilNextQuarterPast));
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
                        Guid.Parse(message.MessageID)
                    );

                    // Delete the processed message
                    await DeleteReceivedMessage(message.MessageID);
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

                if (IsLowestProcessId())
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
                    "SMS Bridge not running on OPENDENTAL!?\n\n" +
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