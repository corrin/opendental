using Newtonsoft.Json;
using OpenDentBusiness.Crud;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Channels;
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
            ODSMSLogger.Instance.Log($"Executing SQL: {command}",
                EventLogEntryType.Information,
                logToEventLog: false,
                logToFile: true);

            List<Patient> listPats = OpenDentBusiness.Crud.PatientCrud.SelectMany(command);
            return listPats;
        }

        private static List<Patient> GetPatientsWithCompletedProceduresYesterday()
        {

            string command = @"
                SELECT p.* 
                FROM patient AS p 
                WHERE TRUE 
                -- AND p.PatStatus = 0  -- Post-op texts should be sent regardless of patient status. 
                AND p.TxtMsgOk < 2 
                AND LENGTH(COALESCE(p.WirelessPhone,'')) > 7 
                AND DAYNAME(CURRENT_DATE()) != 'Sunday' -- safety net since it shouldn't trigger anyway
                AND EXISTS (
                    SELECT 1 
                    FROM procedurelog pl 
                    INNER JOIN procedurecode pc ON pl.CodeNum = pc.CodeNum 
                    INNER JOIN procedure_followup_sms pfs ON pc.ProcCode = pfs.ProcCode 
                    WHERE pl.PatNum = p.PatNum 
                    AND pl.ProcStatus = 2  -- Completed procedures only
                    AND DATE(pl.ProcDate) = 
                        CASE DAYNAME(CURRENT_DATE())
                            WHEN 'Monday' THEN DATE_SUB(CURRENT_DATE(), INTERVAL 2 DAY)    -- Check Saturday
                            ELSE DATE_SUB(CURRENT_DATE(), INTERVAL 1 DAY)                  -- Check yesterday
                        END
                    AND pfs.IsActive = 1
                )
                AND NOT EXISTS (
                    SELECT 1 
                    FROM commlog cl 
                    WHERE cl.PatNum = p.PatNum 
                    AND DATE(cl.CommDateTime) = CURRENT_DATE()  -- Haven't contacted them today
                    AND cl.Note LIKE '%checking in%'
                )";

            // Note.  We don't check commtype.  Otherwise we would send an automated followup after a manually sent text
            // Andrea: You almost want to skip if ANY sms has been sent to this patient.

            // 
            ODSMSLogger.Instance.Log($"Executing SQL: {command}",
                EventLogEntryType.Information,
                logToEventLog: false,
                logToFile: true);

            List<Patient> listPats = OpenDentBusiness.Crud.PatientCrud.SelectMany(command);
            return listPats;
        }





        public static void SendProcedureFollowupTexts()
        {
            var currentTime = DateTime.Now;

            ODSMS.SanityCheckConstants();


            SmsTemplateData procedureTemplateData = ODSMS.TemplateCache[SmsTemplateKeys.PostOp];
            if (!procedureTemplateData.IsEnabled)
            {
                ODSMSLogger.Instance.Log("PostOp SMS template is disabled. Skipping sending procedure follow-up texts.", EventLogEntryType.Information);
                return;
            }
            var patientsWithProcedureYesterday = GetPatientsWithCompletedProceduresYesterday();

            List<SmsToMobile> messagesToSend = PrepareFollowupMessages(patientsWithProcedureYesterday, procedureTemplateData.TemplateText);

            if (messagesToSend.Any())
            {
                // foreach (var sms in messagesToSend)
                // {
                //     Console.WriteLine($"To: {sms.MobilePhoneNumber}, Message: {sms.MsgText}");
                // }
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

        public static void SendBirthdayTexts()
        {
            var currentTime = DateTime.Now;

            ODSMS.SanityCheckConstants();


            SmsTemplateData birthdayTemplateData = ODSMS.TemplateCache[SmsTemplateKeys.Birthday];
            if (!birthdayTemplateData.IsEnabled)
            {
                ODSMSLogger.Instance.Log("Birthday SMS template is disabled. Skipping sending birthday texts.", EventLogEntryType.Information);
                return;
            }
            var patientsWithBirthday = GetPatientsWithBirthdayToday();

            List<SmsToMobile> messagesToSend = PrepareBirthdayMessages(patientsWithBirthday, birthdayTemplateData.TemplateText);

            if (messagesToSend.Any())
            {
                // foreach (var sms in messagesToSend)
                // {
                //     Console.WriteLine($"To: {sms.MobilePhoneNumber}, Message: {sms.MsgText}");
                // }
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
            var messagesToSend = new List<SmsToMobile>();
            foreach (var patient in patientsWithBirthday)
            {
                string renderedText = ODSMS.RenderReminder(birthdayMessageTemplate, patient, null);
                if (string.IsNullOrWhiteSpace(renderedText))
                {
                    ODSMSLogger.Instance.Log($"Rendered birthday message for PatNum {patient.PatNum} was empty. Skipping.", EventLogEntryType.Warning);
                    continue;
                }
                messagesToSend.Add(new SmsToMobile
                {
                    PatNum = patient.PatNum,
                    SmsPhoneNumber = ODSMS.PRACTICE_PHONE_NUMBER,
                    MobilePhoneNumber = patient.WirelessPhone,
                    MsgText = renderedText,
                    MsgType = SmsMessageSource.GeneralMessage,
                    SmsStatus = SmsDeliveryStatus.Pending,
                    MsgParts = 1,
                });
            }
            return messagesToSend;
        }

        private static List<SmsToMobile> PrepareFollowupMessages(List<Patient> patientsWithProcedureYesterday, string procedureMessageTemplate)
        {
            var messagesToSend = new List<SmsToMobile>();
            foreach (var patient in patientsWithProcedureYesterday)
            {
                string renderedText = ODSMS.RenderReminder(procedureMessageTemplate, patient, null);
                if (string.IsNullOrWhiteSpace(renderedText))
                {
                    ODSMSLogger.Instance.Log($"Rendered follow-up message for PatNum {patient.PatNum} was empty. Skipping.", EventLogEntryType.Warning);
                    continue;
                }
                messagesToSend.Add(new SmsToMobile
                {
                    PatNum = patient.PatNum,
                    SmsPhoneNumber = ODSMS.PRACTICE_PHONE_NUMBER,
                    MobilePhoneNumber = patient.WirelessPhone,
                    MsgText = renderedText,
                    MsgType = SmsMessageSource.GeneralMessage,
                    SmsStatus = SmsDeliveryStatus.Pending,
                    MsgParts = 1,
                });
            }
            return messagesToSend;
        }

        private static List<SmsToMobile> PrepareReminderMessages(List<PatientAppointment> patientsNeedingApptReminder, string reminderMessageTemplate, ReminderFilterType filterType)
        {
            var messagesToSend = new List<SmsToMobile>();
            foreach (var pat_appt in patientsNeedingApptReminder)
            {
                string renderedText = ODSMS.RenderReminder(
                    reminderMessageTemplate,
                    pat_appt.Patient,
                    pat_appt.Appointment,
                    pat_appt.Patient.AskToArriveEarly);

                if (string.IsNullOrWhiteSpace(renderedText))
                {
                    ODSMSLogger.Instance.Log($"Rendered reminder message for PatNum {pat_appt.Patient.PatNum} (AptNum {pat_appt.Appointment.AptNum}) was empty. Skipping.", EventLogEntryType.Warning);
                    continue;
                }
                messagesToSend.Add(new SmsToMobile
                {
                    PatNum = pat_appt.Patient.PatNum,
                    SmsPhoneNumber = ODSMS.PRACTICE_PHONE_NUMBER,
                    MobilePhoneNumber = pat_appt.Patient.WirelessPhone,
                    MsgText = renderedText,
                    MsgType = SmsMessageSource.Reminder,
                    SmsStatus = SmsDeliveryStatus.Pending,
                    MsgParts = 1,
                });
            }
            return messagesToSend;
        }


        private static async Task<bool> SendSmsViaHttp(string phoneNumber, string message)
        {
            ODSMSLogger.Instance.Log($"Initiating HTTP SMS send to {phoneNumber}", EventLogEntryType.Information);
            try
            {
                var request = new SendSmsRequest(phoneNumber, message);
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                ODSMSLogger.Instance.Log("Sending HTTP POST request to SMS server", EventLogEntryType.Information);
                var response = await ODSMS.sharedClient.PostAsync("send-sms", content);

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
            if (ODSMS.DEBUG_MODE)
            {
                ODSMSLogger.Instance.Log("Running in debug mode. Not actually sending.  Would have sent the following:", EventLogEntryType.Information);
                foreach (var sms in messagesToSend)
                {
                    ODSMSLogger.Instance.Log($"Would have sent To: {sms.MobilePhoneNumber}, Message: {sms.MsgText}", EventLogEntryType.Information);
                }   

                return true; // Exit, indicating success for the dry run
            }
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
            else // ODSMS.SEND_SMS is false, so SMS sending is generally disabled.
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

        public static void SendReminderTexts()
        {
            var currentTime = DateTime.Now;

            ODSMS.SanityCheckConstants();


            var potentialReminderMessages = Enum.GetValues(typeof(ReminderFilterType));

            foreach (ReminderFilterType currentReminder in potentialReminderMessages)
            {
                List<PatientAppointment> patientsNeedingApptReminder = GetPatientsWithAppointmentsTwoWeeks(filterType: currentReminder, currentDate: DateTime.Now);
                SmsTemplateData reminderTemplateData = ODSMS.TemplateCache[ODSMS.ReminderTemplateKeys[currentReminder]];
                if (!reminderTemplateData.IsEnabled)
                {
                    ODSMSLogger.Instance.Log($"Reminder SMS template for {currentReminder} is disabled. Skipping sending these reminder texts.", EventLogEntryType.Information);
                    continue;
                }

                List<SmsToMobile> messagesToSend = PrepareReminderMessages(patientsNeedingApptReminder, reminderTemplateData.TemplateText, currentReminder);

                if (messagesToSend.Any())
                {
                    SendAndUpdateAppointments(messagesToSend, patientsNeedingApptReminder, currentReminder);
                }
            }

            return;
        }

        private static List<PatientAppointment> GetPatientsWithAppointmentsTwoWeeks(ReminderFilterType filterType, DateTime currentDate)
        {
            /*
            Manual Test Scenarios for GetPatientsWithAppointmentsTwoWeeks:

            This function determines which patients should receive appointment reminders based on filter type and time-based conditions.

            Conditional Logic to Test:
            1.  `filterType == ReminderFilterType.TwoWeeks` (controls `where_scheduled_long_ago` and `where_not_moved_recently`)
            2.  `filterType` in `aptDateTimeRange` switch (OneDay, OneWeek, TwoWeeks, and OneDay when Friday)

            Test Scenarios (2^n approach for key conditionals):

            Scenario 1: TwoWeeks Reminder, Appointment booked/moved > 8 weeks ago
            *   Input: `filterType = ReminderFilterType.TwoWeeks`
            *   Expected `aptDateTimeRange`: "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 2 WEEK))"
            *   Expected `where_scheduled_long_ago`: "AND a.DateTStamp < (NOW() - INTERVAL 8 WEEK)"
            *   Expected `where_not_moved_recently`: SQL for "NOT EXISTS securitylog..." (LogDateTime < (NOW() - INTERVAL 8 WEEK))
            *   Expected Action: Query includes both time-based filters.

            Scenario 2: TwoWeeks Reminder, Appointment booked/moved <= 8 weeks ago
            *   Input: `filterType = ReminderFilterType.TwoWeeks`
            *   Expected `aptDateTimeRange`: "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 2 WEEK))"
            *   Expected `where_scheduled_long_ago`: "AND a.DateTStamp < (NOW() - INTERVAL 8 WEEK)" (This clause is always added for TwoWeeks filterType)
            *   Expected `where_not_moved_recently`: SQL for "NOT EXISTS securitylog..." (This clause is always added for TwoWeeks filterType)
            *   Expected Action: Query includes both time-based filters. (The actual filtering of recent appointments happens in the DB, not by omitting the clause here).

            Scenario 3: OneDay Reminder (Not Friday)
            *   Input: `filterType = ReminderFilterType.OneDay`, `now.DayOfWeek != DayOfWeek.Friday`
            *   Expected `aptDateTimeRange`: "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 1 DAY))"
            *   Expected `where_scheduled_long_ago`: "" (empty string)
            *   Expected `where_not_moved_recently`: "" (empty string)
            *   Expected Action: Query does NOT include time-based filters for recent booking/moving.

            Scenario 4: OneDay Reminder (Friday)
            *   Input: `filterType = ReminderFilterType.OneDay`, `now.DayOfWeek == DayOfWeek.Friday`
            *   Expected `aptDateTimeRange`: "DATE(a.AptDateTime) IN (DATE(DATE_ADD(NOW(), INTERVAL 1 DAY)), DATE(DATE_ADD(NOW(), INTERVAL 3 DAY)))"
            *   Expected `where_scheduled_long_ago`: "" (empty string)
            *   Expected `where_not_moved_recently`: "" (empty string)
            *   Expected Action: Query targets Friday and Sunday appointments, does NOT include time-based filters for recent booking/moving.

            Scenario 5: OneWeek Reminder
            *   Input: `filterType = ReminderFilterType.OneWeek`
            *   Expected `aptDateTimeRange`: "DATE(a.AptDateTime) = DATE(DATE_ADD(NOW(), INTERVAL 1 WEEK))"
            *   Expected `where_scheduled_long_ago`: "" (empty string)
            *   Expected `where_not_moved_recently`: "" (empty string)
            *   Expected Action: Query does NOT include time-based filters for recent booking/moving.

            */
            int textMessageValue = (int)OpenDentBusiness.ContactMethod.TextMessage;
            int wirelessPhoneValue = (int)OpenDentBusiness.ContactMethod.WirelessPh;
            int noPreferenceValue = (int)OpenDentBusiness.ContactMethod.None;
            const int WEEKS_RECENT = 8;
            string where_scheduled_long_ago = "";
            string where_not_moved_recently = "";


            string currentDateStr = $"'{currentDate:yyyy-MM-dd}'";

            string aptDateTimeRange = filterType switch
            {
                ReminderFilterType.OneDay when currentDate.DayOfWeek == DayOfWeek.Friday =>
                    $"DATE(a.AptDateTime) IN (DATE(DATE_ADD({currentDateStr}, INTERVAL 1 DAY)), DATE(DATE_ADD({currentDateStr}, INTERVAL 3 DAY)))",
                ReminderFilterType.OneDay =>
                    $"DATE(a.AptDateTime) = DATE(DATE_ADD({currentDateStr}, INTERVAL 1 DAY))",
                ReminderFilterType.OneWeek => $"DATE(a.AptDateTime) = DATE(DATE_ADD({currentDateStr}, INTERVAL 1 WEEK))",
                ReminderFilterType.TwoWeeks => $"DATE(a.AptDateTime) = DATE(DATE_ADD({currentDateStr}, INTERVAL 2 WEEK))",
                _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value."),
            };

            // Query components
            string select = "SELECT p.*, a.* ";
            string from = "FROM patient AS p JOIN Appointment as a using (PatNum) ";
            string where_true = "WHERE TRUE ";

            // Time-based filters
            string where_appointment_date = $"AND {aptDateTimeRange} ";
            if (filterType == ReminderFilterType.TwoWeeks) // The two-week reminder is skipped if the appointment was booked or moved in the last 8 weeks
            {
                where_scheduled_long_ago = $"AND a.DateTStamp < ({currentDateStr} - INTERVAL {WEEKS_RECENT} WEEK) ";

                where_not_moved_recently = $@"
    AND NOT EXISTS (
        SELECT 1 FROM securitylog s 
        WHERE s.PermType = 26
        AND s.FKey = a.AptNum 
        AND s.LogDateTime >= ({currentDateStr} - INTERVAL {WEEKS_RECENT} WEEK)
    )";
            }




            // Patient communication preferences
            string where_allow_sms = "AND p.TxtMsgOk < 2 ";
            string where_confirm_not_sms = $"AND p.PreferConfirmMethod IN ({noPreferenceValue}, {wirelessPhoneValue}, {textMessageValue}) ";
            string where_mobile_phone = "AND LENGTH(COALESCE(p.WirelessPhone,'')) > 7 ";

            // Appointment-based filters
            string where_no_intermediate_appointments = @"
    AND NOT EXISTS (
        SELECT 1 FROM Appointment a2 
        WHERE a2.AptDateTime > NOW() 
        AND a2.AptDateTime < a.AptDateTime 
        AND a2.PatNum = a.PatNum
    )";
            string where_appointment_confirmed = GetAppointmentConfirmedWhereClause(filterType);
            string where_scheduled = $"AND a.AptStatus = {(int)OpenDentBusiness.ApptStatus.Scheduled} ";
            // Reminders are sent to all patients regardless of their PatStatus.

            // Construct final query
            string command = string.Join(" ",
                select, from, where_true,
                where_appointment_date, where_appointment_confirmed, where_mobile_phone,
                where_allow_sms, where_confirm_not_sms, where_no_intermediate_appointments,
                where_scheduled, where_scheduled_long_ago, where_not_moved_recently
            );

            ODSMSLogger.Instance.Log($"Executing SQL: {command}",
                EventLogEntryType.Information,
                logToEventLog: false,
                logToFile: true);

            List<PatientAppointment> listPatAppts = OpenDentBusiness.Crud.PatientApptCrud.SelectMany(command);
            return listPatAppts;
        }


        public static SmsDeliveryStatus ToSmsDeliveryStatus(this MessageStatus status)
        {
            return status switch
            {
                MessageStatus.Pending => SmsDeliveryStatus.Pending,
                MessageStatus.Delivered => SmsDeliveryStatus.DeliveryConf,  // Since bridge confirms delivery
                MessageStatus.Failed => SmsDeliveryStatus.FailNoCharge,     // Immediate failure from provider
                _ => SmsDeliveryStatus.None
            };
        }


        public static async System.Threading.Tasks.Task<List<SmsToMobile>> SendMultipleMessagesAsync(List<SmsToMobile> listSmsToMobileMessages)
        {
            ODSMSLogger.Instance.Log(
                $"Performing regular SMS sending: About to send {listSmsToMobileMessages.Count} messages.",
                EventLogEntryType.Information,
                logToConsole: true,
                logToEventLog: true,
                logToFile: true);

            // One message is usually an interactive send
            bool isInteractiveSend = listSmsToMobileMessages.Count == 1;
            bool requireDeliveryConfirmation = false; // We are going to try getting everything confirmed
                                                      // HACK FOR NOW.  Disable confirmation

            // Set timeout values based on whether this is an interactive or bulk send
            int maxAttempts;
            int delayMs;

            if (isInteractiveSend)
            {
                // For interactive sends, use a shorter timeout (30 seconds total)
                maxAttempts = 15;
                delayMs = 2000; // 2 seconds between attempts
                ODSMSLogger.Instance.Log(
                    "Using interactive send timeout of 30 seconds",
                    EventLogEntryType.Information,
                    logToFile: true);
            }
            else
            {
                // For bulk sends, use a longer timeout (10 minutes total)
                maxAttempts = 60;
                delayMs = 10000; // 10 seconds between attempts
                ODSMSLogger.Instance.Log(
                    "Using bulk send timeout of 10 minutes",
                    EventLogEntryType.Information,
                    logToFile: true);
            }

            foreach (var msg in listSmsToMobileMessages)
            {

                var (success, messageId) = await ODSMSBridgeInterface
                    .SendSmsViaHttp(msg.MobilePhoneNumber, msg.MsgText)
                    .ConfigureAwait(false);
                msg.GuidMessage = messageId;
                if (success)
                {
                    // If we need confirmation
                    if (requireDeliveryConfirmation)
                    {
                        var status = await ODSMSBridgeInterface.WaitForMessageStatus(
                            msg.GuidMessage,
                            maxAttempts: maxAttempts,
                            delayMs: delayMs);
                        msg.SmsStatus = status.ToSmsDeliveryStatus();
                    }
                    else
                    {
                        // For bulk sends, we'll mark as sent when queued successfully
                        msg.SmsStatus = SmsDeliveryStatus.DeliveryUnconf;
                    }
                }
                else
                {
                    msg.SmsStatus = SmsDeliveryStatus.FailNoCharge;
                }
            }

            return listSmsToMobileMessages;
        }

        private static string GetAppointmentConfirmedWhereClause(ReminderFilterType filterType)
        {
            if (!ODSMS.AppointmentStatusToReminderMap.TryGetValue(filterType, out List<long> allowedStatuses))
            {
                throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "Invalid ReminderFilterType value.");
            }
            string statusList = string.Join(",", allowedStatuses.Select(s => (int)s));
            return $"AND a.Confirmed IN ({statusList})";
        }
    }
}