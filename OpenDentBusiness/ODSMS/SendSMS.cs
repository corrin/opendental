﻿using OpenDentBusiness.Crud;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SystemTask = System.Threading.Tasks.Task;

namespace OpenDentBusiness.ODSMS
{

    public enum ReminderFilterType
    {
        OneDay,
        OneWeek,
        TwoWeeks
    };


    public static class SendSMS
    {


        private static int GetMinutesUntilQuarterPast(DateTime now)
        {
            int minutesToNextQuarter = (15 - now.Minute % 15) % 60;
            if (minutesToNextQuarter == 0)
            {
                minutesToNextQuarter = 60;
            }

            return Math.Max(1, minutesToNextQuarter);
        }


        private static List<Patient> GetPatientsWithBirthdayToday()
        {
            string select = "SELECT p.* ";
            string from = "FROM patient AS p ";
            string where_true = "WHERE TRUE ";
            string where_active = $"AND p.PatStatus IN ({(int)OpenDentBusiness.PatientStatus.Patient}) ";
            string where_allow_sms = "AND p.TxtMsgOk < 2 ";
            string where_birthday = "AND MONTH(p.Birthdate) = MONTH(CURRENT_DATE()) AND DAY(p.Birthdate) = DAY(CURRENT_DATE()) ";
            string where_not_contacted = @"AND NOT EXISTS (
                                    SELECT 1 
                                    FROM CommLog m 
                                    WHERE m.PatNum = p.PatNum 
                                    AND m.Note LIKE '%Birthday%' 
                                    AND m.CommDateTime > DATE_SUB(NOW(), INTERVAL 3 DAY)) ";
            string where_mobile_phone = "AND LENGTH(COALESCE(p.WirelessPhone,'')) > 7 ";

            string command = select + from + where_true + where_active + where_birthday + where_not_contacted + where_allow_sms + where_mobile_phone;
            Console.WriteLine(command);

            List<Patient> listPats = OpenDentBusiness.Crud.PatientCrud.SelectMany(command);
            return listPats;
        }

        private static string GetReminderMessageTemplate(ReminderFilterType filterType)
        {
            return filterType switch
            {
                ReminderFilterType.OneDay => PrefC.GetString(PrefName.ConfirmTextMessage),
                ReminderFilterType.OneWeek => PrefC.GetString(PrefName.ConfirmPostcardMessage),
                ReminderFilterType.TwoWeeks => PrefC.GetString(PrefName.ConfirmPostcardFamMessage),
                _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value."),
            };
        }



        public static async SystemTask PerformRegularSendSMSTasks()
        {
            bool smsIsWorking = ODSMS.CheckSMSConnection();
            bool remindersSent = false;
            bool birthdaySent = false;

            ODSMSLogger.Instance.Log("Performing regular SMS sending", EventLogEntryType.Information);


            SendReminderTexts();
            SendBirthdayTexts();
        }

        private static void SendBirthdayTexts()
        {
            var currentTime = DateTime.Now;

            ODSMS.SanityCheckConstants();


            string birthdayMessageTemplate = PrefC.GetString(PrefName.BirthdayPostcardMsg);
            var patientsWithBirthday = GetPatientsWithBirthdayToday();

            List<SmsToMobile> messagesToSend = PrepareBirthdayMessages(patientsWithBirthday, birthdayMessageTemplate);

            if (messagesToSend.Any())
            {
                foreach (var sms in messagesToSend)
                {
                    Console.WriteLine($"To: {sms.MobilePhoneNumber}, Message: {sms.MsgText}");
                }
                if (ODSMS.SEND_SMS)
                {
                    SmsToMobiles.SendSmsMany(messagesToSend);
                }
                else
                {
                    ODSMSLogger.Instance.Log("SMS sending is disabled. Not sending any messages", EventLogEntryType.Warning);
                }
            }
            return;
        }

        private static List<SmsToMobile> PrepareBirthdayMessages(List<Patient> patientsWithBirthday, string birthdayMessageTemplate)
        {
            return patientsWithBirthday.Select(patient =>
                new SmsToMobile
                {
                    PatNum = patient.PatNum,
                    SmsPhoneNumber = ODSMS.PRACTICE_PHONE_NUMBER,
                    MobilePhoneNumber = patient.WirelessPhone,
                    MsgText = ODSMS.RenderReminder(birthdayMessageTemplate, patient, null),
                    MsgType = SmsMessageSource.GeneralMessage,
                    SmsStatus = SmsDeliveryStatus.Pending,
                    MsgParts = 1,
                }).ToList();
        }

        private static List<SmsToMobile> PrepareReminderMessages(List<PatientAppointment> patientsNeedingApptReminder, string reminderMessageTemplate, ReminderFilterType filterType)
        {
            return patientsNeedingApptReminder.Select(pat_appt =>
                new SmsToMobile
                {
                    PatNum = pat_appt.Patient.PatNum,
                    SmsPhoneNumber = ODSMS.PRACTICE_PHONE_NUMBER,
                    MobilePhoneNumber = pat_appt.Patient.WirelessPhone,
                    MsgText = ODSMS.RenderReminder(reminderMessageTemplate, pat_appt.Patient, pat_appt.Appointment),
                    MsgType = SmsMessageSource.Reminder,
                    SmsStatus = SmsDeliveryStatus.Pending,
                    MsgParts = 1,
                }).ToList();
        }

        public static async Task<bool> SendSmsMessageAsync(SmsToMobile msg, bool? forceHttpMode=null)
        {
            try
            {
                ODSMSLogger.Instance.Log($"Preparing to send SMS to {msg.MobilePhoneNumber}", EventLogEntryType.Information);

                // Handle debug number
                if (!string.IsNullOrEmpty(ODSMS.DEBUG_NUMBER))
                {
                    if (msg.MobilePhoneNumber != ODSMS.DEBUG_NUMBER)
                    {
                        ODSMSLogger.Instance.Log($"Debug mode: Redirecting SMS to {OpenDentBusiness.ODSMS.ODSMS.DEBUG_NUMBER}", EventLogEntryType.Warning);
                        msg.MobilePhoneNumber = ODSMS.DEBUG_NUMBER;
                    }
                }

                // Format phone number
                string originalNumber = msg.MobilePhoneNumber;
                if (msg.MobilePhoneNumber[0] == '+')
                {
                    msg.MobilePhoneNumber = msg.MobilePhoneNumber.Substring(1);
                }
                else if (msg.MobilePhoneNumber[0] == '0')
                {
                    msg.MobilePhoneNumber = "64" + msg.MobilePhoneNumber.Substring(1);
                }
                if (originalNumber != msg.MobilePhoneNumber)
                {
                    ODSMSLogger.Instance.Log($"Phone number formatted from {originalNumber} to {msg.MobilePhoneNumber}", EventLogEntryType.Information);
                }

                bool isSuccess = false;
                bool useHttpMode = forceHttpMode ?? !ODSMS.IS_SMS_BRIDGE_MACHINE;

                // Check if we're on the SMS bridge machine
                if (useHttpMode)
                {
                    ODSMSLogger.Instance.Log("Sending SMS via HTTP", EventLogEntryType.Information);
                    isSuccess = await SendSmsViaHttp(msg.MobilePhoneNumber, msg.MsgText);
                }
                else
                {
                    ODSMSLogger.Instance.Log("Sending SMS via local bridge", EventLogEntryType.Information);
                    var requestId = JustRemotePhoneBridge.Instance.SendSMSviaJustRemote(msg.MobilePhoneNumber, msg.MsgText);
                    if (requestId != null)
                    {
                        isSuccess = true;
                    }
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2));
                    //Success = await JustRemotePhoneBridge.Instance.WaitForSmsStatusAsync(requestId);  // Corrin: This is too expensive to wait for
                }

                ODSMSLogger.Instance.Log($"SMS send attempt result: {(isSuccess ? "Success" : "Failure")}",
                    isSuccess ? EventLogEntryType.Information : EventLogEntryType.Warning);

                // Update SmsStatus
                msg.SmsStatus = isSuccess ? SmsDeliveryStatus.DeliveryConf : SmsDeliveryStatus.FailNoCharge;

                return isSuccess;
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Error sending SMS: {ex.Message}", EventLogEntryType.Error);
                msg.SmsStatus = SmsDeliveryStatus.FailNoCharge;
                return false;
            }
        }

        private static async Task<bool> SendSmsViaHttp(string phoneNumber, string message)
        {
            ODSMSLogger.Instance.Log($"Initiating HTTP SMS send to {phoneNumber}", EventLogEntryType.Information);

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("phoneNumber", phoneNumber),
                    new KeyValuePair<string, string>("message", message)
                 });

                ODSMSLogger.Instance.Log("Sending HTTP POST request to SMS server", EventLogEntryType.Information);
                var response = await ODSMS.sharedClient.PostAsync("sendSms", content);

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    ODSMSLogger.Instance.Log($"HTTP response body: {responseBody}", EventLogEntryType.Warning);
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log($"Exception in SendSmsViaHttp: {ex.Message}", EventLogEntryType.Error);
                return false;
            }
        }
        private static bool SendAndUpdateAppointments(List<SmsToMobile> messagesToSend, List<PatientAppointment> patientsNeedingApptReminder, ReminderFilterType filterType)
        {
            foreach (var sms in messagesToSend)
            {
                ODSMSLogger.Instance.Log($"To: {sms.MobilePhoneNumber}, Message: {sms.MsgText}", EventLogEntryType.Information);
            }

            if (ODSMS.SEND_SMS)
            {
                List<SmsToMobile> sentMessages = SmsToMobiles.SendSmsMany(
                    listSmsToMobilesMessages: messagesToSend,
                    makeCommLog: ODSMS.WRITE_TO_DATABASE,
                    userod: null, // No user context available, passing null
                    canCheckBal: false
                );
                List<Appointment> appts = patientsNeedingApptReminder
                    .Where(patapt => sentMessages.Any(msg => msg.PatNum == patapt.Patient.PatNum &&
                        (msg.SmsStatus == SmsDeliveryStatus.Pending ||
                         msg.SmsStatus == SmsDeliveryStatus.DeliveryConf ||
                         msg.SmsStatus == SmsDeliveryStatus.DeliveryUnconf)))
                    .Select(patapt => patapt.Appointment)
                    .ToList();

                foreach (Appointment originalAppt in appts)
                {
                    Appointment updatedAppt = originalAppt.Copy();
                    updatedAppt.Confirmed = GetUpdatedConfirmationStatus(filterType);

                    bool updateSucceeded = true;

                    if (ODSMS.WRITE_TO_DATABASE)
                    {
                        updateSucceeded = AppointmentCrud.Update(updatedAppt, originalAppt);
                    }
                    else
                    {
                        ODSMSLogger.Instance.Log($"Not updating appointment {originalAppt.AptNum} on patient {originalAppt.PatNum} from {originalAppt.Confirmed} to {updatedAppt.Confirmed} as running in debug mode", EventLogEntryType.Warning);
                    }
                    if (updateSucceeded)
                    {
                        ODSMSLogger.Instance.Log($"Updated {originalAppt.AptNum} on patient {originalAppt.PatNum} from {originalAppt.Confirmed} to {updatedAppt.Confirmed}", EventLogEntryType.Information);
                    }
                    else
                    {
                        ODSMSLogger.Instance.Log("Failure updating patient details!", EventLogEntryType.Warning);
                    }
                }

                return appts.Any();
            }
            else
            {
                ODSMSLogger.Instance.Log("SMS sending is disabled. Not sending any messages", EventLogEntryType.Warning);
                return false;
            }
        }

        private static int GetUpdatedConfirmationStatus(ReminderFilterType filterType)
        {
            return filterType switch
            {
                ReminderFilterType.OneDay => (int)ODSMS._defNumTexted,
                ReminderFilterType.OneWeek => (int)ODSMS._defNumOneWeekSent,
                ReminderFilterType.TwoWeeks => (int)ODSMS._defNumTwoWeekSent,
                _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value."),
            };
        }

        private static void SendReminderTexts()
        {
            var currentTime = DateTime.Now;

            ODSMS.SanityCheckConstants();


            var potentialReminderMessages = Enum.GetValues(typeof(ReminderFilterType));

            foreach (ReminderFilterType currentReminder in potentialReminderMessages)
            {
                List<PatientAppointment> patientsNeedingApptReminder = GetPatientsWithAppointmentsTwoWeeks(currentReminder);
                string reminderMessageTemplate = GetReminderMessageTemplate(currentReminder);

                List<SmsToMobile> messagesToSend = PrepareReminderMessages(patientsNeedingApptReminder, reminderMessageTemplate, currentReminder);

                if (messagesToSend.Any())
                {
                    SendAndUpdateAppointments(messagesToSend, patientsNeedingApptReminder, currentReminder);
                }
            }

            return;
        }

        private static List<PatientAppointment> GetPatientsWithAppointmentsTwoWeeks(ReminderFilterType filterType)
        {
            int textMessageValue = (int)OpenDentBusiness.ContactMethod.TextMessage;
            int wirelessPhoneValue = (int)OpenDentBusiness.ContactMethod.WirelessPh;
            int noPreferenceValue = (int)OpenDentBusiness.ContactMethod.None;
            DateTime now = DateTime.Now;
            string aptDateTimeRange = filterType switch
            {
                ReminderFilterType.OneDay when now.DayOfWeek == DayOfWeek.Friday =>
                    "DATE(a.AptDateTime) IN (DATE(DATE_ADD(NOW(), INTERVAL 1 DAY)), DATE(DATE_ADD(NOW(), INTERVAL 3 DAY)))",
                ReminderFilterType.OneDay =>
                    "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 1 DAY))",
                ReminderFilterType.OneWeek => "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 1 WEEK))",
                ReminderFilterType.TwoWeeks => "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 2 WEEK))",
                _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value."),
            };
            string select = "SELECT p.*, a.* ";
            string from = "FROM patient AS p JOIN Appointment as a using (PatNum) ";
            string where_true = "WHERE TRUE ";
            string where_allow_sms = "AND p.TxtMsgOk < 2 ";
            string where_confirm_not_sms = $"AND p.PreferConfirmMethod IN ({noPreferenceValue}, {wirelessPhoneValue}, {textMessageValue}) ";
            string where_no_intermediate_appointments = "AND NOT EXISTS (SELECT 1 FROM Appointment a2 WHERE a2.AptDateTime > NOW() AND a2.AptDateTime < a.AptDateTime AND a2.PatNum = a.PatNum) ";
            string where_mobile_phone = "AND LENGTH(COALESCE(p.WirelessPhone,'')) > 7 ";
            string where_appointment_date = $"AND {aptDateTimeRange} ";
            string where_appointment_confirmed = GetAppointmentConfirmedWhereClause(filterType);
            string where_scheduled = $"AND a.AptStatus = {(int)OpenDentBusiness.ApptStatus.Scheduled} ";

            string command = select + from + where_true + where_appointment_date + where_appointment_confirmed + where_mobile_phone + where_allow_sms + where_confirm_not_sms + where_no_intermediate_appointments + where_scheduled;
            ODSMSLogger.Instance.Log(command, EventLogEntryType.Information, logToEventLog: false);
            Console.WriteLine(command);
            List<PatientAppointment> listPatAppts = OpenDentBusiness.Crud.PatientApptCrud.SelectMany(command);
            return listPatAppts;
        }

        public static async SystemTask ManageScheduledSMSSending()
        {
            while (true)
            {
                DateTime now = DateTime.Now;

                if (
                    // Debug Mode: Run every 5 minutes
                    (!string.IsNullOrEmpty(ODSMS.DEBUG_NUMBER) && now.Minute % 5 == 0) ||

                    // Normal Mode: Run at quarter past the hour, between 8 AM and 5 PM
                    (string.IsNullOrEmpty(ODSMS.DEBUG_NUMBER) &&
                     now.Minute >= 14 && now.Minute <= 16 && now.Hour >= 8 && now.Hour <= 17)
                )
                {
                    SendReminderTexts();
                    SendBirthdayTexts();
                }

                int minutesUntilNextQuarterPast = GetMinutesUntilQuarterPast(now);
                await SystemTask.Delay(TimeSpan.FromMinutes(minutesUntilNextQuarterPast));
            }
        }

        public static async System.Threading.Tasks.Task<List<SmsToMobile>> SendMultipleMessagesAsync(List<SmsToMobile> listSmsToMobileMessages)
        {
            // Log the number of messages that are about to be sent using ODSMSLogger
            ODSMSLogger.Instance.Log($"Performing regular SMS sending: About to bulk send {listSmsToMobileMessages.Count} messages.",
                                      EventLogEntryType.Information,
                                      logToConsole: true,
                                      logToEventLog: true,
                                      logToFile: true);

            // Step 1: Create a list to hold all the send tasks
            var sendTasks = new List<System.Threading.Tasks.Task<bool>>();
            var messageLogs = new Dictionary<SmsToMobile, DateTime>();

            foreach (var msg in listSmsToMobileMessages)
            {
                var sendTask = SendSMS.SendSmsMessageAsync(msg);
                sendTasks.Add(sendTask);
            }

            // Step 2: Wait for all send tasks to complete in parallel
            await System.Threading.Tasks.Task.WhenAll(sendTasks);

            // Step 3: Collect successful messages and log time taken
            var successfulMessages = new List<SmsToMobile>();

            for (int i = 0; i < listSmsToMobileMessages.Count; i++)
            {
                var msg = listSmsToMobileMessages[i];
                var sendTask = sendTasks[i];


                if (sendTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && sendTask.Result)
                {
                    successfulMessages.Add(msg);
                }

            }

            return successfulMessages;
        }

        private static string GetAppointmentConfirmedWhereClause(ReminderFilterType filterType)
        {
            return filterType switch
            {
                ReminderFilterType.OneDay => $"AND a.Confirmed IN ({ODSMS._defNumWebSched},{ODSMS._defNumNotCalled}, {ODSMS._defNumUnconfirmed}, {ODSMS._defNumOneWeekConfirmed}, {ODSMS._defNumTwoWeekConfirmed}, {ODSMS._defNumOneWeekSent}, {ODSMS._defNumTwoWeekSent}) ",
                ReminderFilterType.OneWeek => $"AND a.Confirmed IN ({ODSMS._defNumWebSched},{ODSMS._defNumNotCalled}, {ODSMS._defNumUnconfirmed}, {ODSMS._defNumTwoWeekConfirmed}, {ODSMS._defNumTwoWeekSent}) ",
                ReminderFilterType.TwoWeeks => $"AND a.Confirmed IN ({ODSMS._defNumWebSched},{ODSMS._defNumNotCalled}, {ODSMS._defNumUnconfirmed}) ",
                _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value."),
            };
        }
    }
};

