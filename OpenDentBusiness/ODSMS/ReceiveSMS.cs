﻿using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml;
using SystemTask = System.Threading.Tasks.Task;
using System.Threading;
using System.Text.RegularExpressions;
using OpenDentBusiness;
using OpenDentBusiness.Crud;
using OpenDentBusiness.Mobile;
using Google.Apis.Gmail.v1.Data;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CodeBase;

namespace OpenDentBusiness.ODSMS
{
    public static class ReceiveSMS
    {
        private static readonly SemaphoreSlim fetchAndProcessSemaphore = new SemaphoreSlim(1, 1);



        public static async SystemTask ProcessSmsMessage(string msgFrom, string msgText, DateTime msgTime, Guid msgGUID)
        {
            string msgGUIDString = msgGUID.ToString();
            string msgTimeString = msgTime.ToString();
            string guidFilePath = Path.Combine(ODSMS.sms_folder_path, msgGUIDString);

            if (File.Exists(guidFilePath))
            {
                ODSMSLogger.Instance.Log("Must've already processed this SMS", EventLogEntryType.Information, logToEventLog: false);
            }
            else
            {
                string logMessage = $"SMS from {msgFrom} at time {msgTimeString} with body {msgText} - GUID: {msgGUIDString}";
                ODSMSLogger.Instance.Log(logMessage, EventLogEntryType.Information);

                try
                {
                    await ProcessOneReceivedSMS(msgText, msgTime, msgFrom, msgGUID);
                }
                catch (Exception ex)
                {
                    string fullLogMessage = $"An exception occurred: {ex.Message}\nStack Trace: {ex.StackTrace}";
                    ODSMSLogger.Instance.Log(fullLogMessage, EventLogEntryType.Error);
                }
            }
        }

        private static bool IsAppointmentAlreadyConfirmed(Appointment appointment)
        {
            return appointment.Confirmed != ODSMS._defNumTexted &&
                    appointment.Confirmed != ODSMS._defNumOneWeekSent &&
                    appointment.Confirmed != ODSMS._defNumTwoWeekSent;
        }

        private static bool UpdateAppointmentStatus(Appointment originalAppt, long confirmationStatus)
        {
            Appointment updatedAppt = originalAppt.Copy();
            updatedAppt.Confirmed = confirmationStatus;

            bool updateSucceeded = AppointmentCrud.Update(updatedAppt, originalAppt);
            if (updateSucceeded)
            {
                Console.WriteLine("Appointment status updated successfully.");
                return true;
            }
            else
            {
                ODSMSLogger.Instance.Log("Failure updating appointment status!", EventLogEntryType.Error);
                return false;
            }
        }

        /// <summary>
        /// Determines the appropriate confirmation status for an appointment.
        /// </summary>
        /// <param name="daysUntilAppointment">The number of days remaining until the appointment.</param>
        /// <returns>
        /// The confirmation status to set based on the number of days:
        /// - Two-week reminder has been confirmed.
        /// - One-week reminder has been confirmed.
        /// - The appointment has been confirmed.
        /// Returns 0 if the days until the appointment are outside expected ranges.
        /// </returns>
        private static long GetConfirmationStatus(int daysUntilAppointment)
        {
            if (daysUntilAppointment >= 14 && daysUntilAppointment < 22)
            {
                return ODSMS._defNumTwoWeekConfirmed;
            }
            else if (daysUntilAppointment >= 7)
            {
                return ODSMS._defNumOneWeekConfirmed;
            }
            else if (daysUntilAppointment >= 0 && daysUntilAppointment <= 4)
            {
                return ODSMS._defNumConfirmed;
            }
            else
            {
                string logMessage = $"Received YES to an appointment that is {daysUntilAppointment} days away. This is unexpected.";
                ODSMSLogger.Instance.Log(logMessage, EventLogEntryType.Warning);
                return 0;
            }
        }
        public static bool HandleAutomatedConfirmationInternal(List<Patient> patientList)
        {
            string patNums = String.Join(",", patientList.Select(p => p.PatNum.ToString()).ToArray());
            string latestSMS = "SELECT * from CommLog where PatNum IN (" + patNums + ") AND Note REGEXP 'Text message sent.*(reply|respond).*YES' AND CommDateTime >= DATE_SUB(NOW(), INTERVAL 21 DAY) ORDER BY CommDateTime DESC LIMIT 1";
            Commlog latestComm = OpenDentBusiness.Crud.CommlogCrud.SelectOne(latestSMS);

            if (latestComm != null)
            {
                long PatNum = latestComm.PatNum;
                Patient p = patientList.FirstOrDefault(p => p.PatNum == latestComm.PatNum);
                if (p == null)
                {
                    ODSMSLogger.Instance.Log("No matching patient found.", EventLogEntryType.Information, logToEventLog: false);
                    return false;
                }

                DateTime? appointmentTime = AppointmentHelper.ExtractAppointmentDate(latestComm.Note);

                if (appointmentTime.HasValue)
                {
                    TimeSpan timeUntilAppointment = appointmentTime.Value - latestComm.CommDateTime;
                    int daysUntilAppointment = (int)Math.Ceiling(timeUntilAppointment.TotalDays);

                    long confirmationStatus = GetConfirmationStatus(daysUntilAppointment);
                    if (confirmationStatus == 0)
                    {
                        ODSMSLogger.Instance.Log("Confirmation status is 0. Ignoring.", EventLogEntryType.Warning);
                        return false;
                    }

                    string appointmentQuery = $"SELECT * FROM Appointment WHERE PatNum = {PatNum} AND AptDateTime = '{appointmentTime.Value.ToString("yyyy-MM-dd HH:mm:ss")}'";
                    Appointment originalAppt = AppointmentCrud.SelectOne(appointmentQuery);

                    if (originalAppt != null)
                    {
                        if (IsAppointmentAlreadyConfirmed(originalAppt))
                        {
                            ODSMSLogger.Instance.Log("OOPS! Patient just replied yes to an appointment that is already confirmed. Ignoring", EventLogEntryType.Warning);
                            return false;
                        }
                        else
                        {
                            bool updateSuccess = UpdateAppointmentStatus(originalAppt, confirmationStatus);
                            if (!updateSuccess)
                            {
                                ODSMSLogger.Instance.Log("Failed to update appointment status.", EventLogEntryType.Warning);
                            }
                            return updateSuccess;
                        }
                    }
                    else
                    {
                        string logMessage = $"No matching appointment found for patient {PatNum} at {appointmentTime.Value}.";
                        ODSMSLogger.Instance.Log(logMessage, EventLogEntryType.Warning);
                    }
                }
                else
                {
                    ODSMSLogger.Instance.Log($"Error parsing date from communication log note. {latestComm.Note ?? "Note is null"}", EventLogEntryType.Error);
                }
            }
            else
            {
                string logMessage = $"'Yes' received, but no matching appointment found for any of the patients {patNums}.";
                ODSMSLogger.Instance.Log(logMessage, EventLogEntryType.Warning);
            }

            return false;
        }

        private static async SystemTask ProcessOneReceivedSMS(string msgText, DateTime msgTime, string msgFrom, Guid msgGUID)
        {
            var msgTimeStr = msgTime.ToString();
            var msgGUIDstr = msgGUID.ToString();
            ODSMSLogger.Instance.Log("SMS inner loop - downloaded a single SMS", EventLogEntryType.Information, logToEventLog: false);
            string guidFilePath = Path.Combine(ODSMS.sms_folder_path, msgGUIDstr);
            string cleanedText = Regex.Replace(msgText.ToUpper(), "[^A-Z]", "");

            byte[] bytesToWrite = Encoding.UTF8.GetBytes(msgText);

            using (var fileStream = File.Create(guidFilePath))
            {
                await fileStream.WriteAsync(bytesToWrite, 0, bytesToWrite.Length);
            }

            var patients = Patients.GetPatientsByPhone(msgFrom.Substring(3), "+64", new List<PhoneType> { PhoneType.WirelessPhone });

            ODSMSLogger.Instance.Log($"Number of matching patients: {patients.Count}", EventLogEntryType.Information, logToEventLog: false);

            if (patients.Count > 20)
            {
                ODSMSLogger.Instance.Log("Too many patients match this number! Assuming a dummy entry", EventLogEntryType.Information);
                return;
            }

            Commlog log = CreateCommlog(patients, msgText, msgTime);
            SmsFromMobile sms = CreateSmsFromMobile(log, msgFrom, msgTime, msgText, patients.Count);

            if (cleanedText == "YES" || cleanedText == "Y")
            {
                if (patients.Count < 10)
                {
                    await HandleAutomatedConfirmation(patients, sms);
                }
                else
                {
                    ODSMSLogger.Instance.Log("'YES' received matching more than ten patients - process manually", EventLogEntryType.Information);
                }
            }

            OpenDentBusiness.SmsFromMobiles.Insert(sms);

            Console.WriteLine("Finished OD New Text Message");
        }

        private static Commlog CreateCommlog(List<Patient> patients, string msgText, DateTime time)
        {
            Commlog log = new Commlog
            {
                PatNum = patients.Count != 0 ? patients[0].PatNum : 0,
                Note = "Text message received: " + msgText,
                Mode_ = CommItemMode.Text,
                CommDateTime = time,
                SentOrReceived = CommSentOrReceived.Received,
                CommType = Commlogs.GetTypeAuto(CommItemTypeAuto.TEXT),
                UserNum = 1 // Admin user
            };
            return log;
        }

        private static SmsFromMobile CreateSmsFromMobile(Commlog log, string msgFrom, DateTime time, string msgText, int patientCount)
        {
            SmsFromMobile sms = new SmsFromMobile
            {
                CommlogNum = Commlogs.Insert(log),
                MobilePhoneNumber = msgFrom,
                PatNum = log.PatNum,
                DateTimeReceived = time,
                MsgText = msgText,
                SmsStatus = SmsFromStatus.ReceivedUnread,
                MsgTotal = 1,
                MatchCount = patientCount,
                ClinicNum = 0,
                MsgPart = 1,
                Flags = " "
            };
            return sms;
        }

        private static async SystemTask HandleAutomatedConfirmation(List<Patient> patients, SmsFromMobile sms)
        {
            ODSMSLogger.Instance.Log("About to handle automated SMS", EventLogEntryType.Information, logToEventLog: false);

            bool wasHandled = ReceiveSMS.HandleAutomatedConfirmationInternal(patients);
            if (wasHandled)
            {
                sms.SmsStatus = SmsFromStatus.ReceivedRead;
                ODSMSLogger.Instance.Log("Success handling automated SMS", EventLogEntryType.Information, logToEventLog: false);
            }
            else
            {
                ODSMSLogger.Instance.Log("Failure handling automated SMS", EventLogEntryType.Warning);
                await SendConfirmationFailureMessage(patients[0], sms.MobilePhoneNumber);
            }
        }

        private static SystemTask SendConfirmationFailureMessage(Patient patient, string mobileNumber)
        {
            string matchSMSmessage = "Thank you for your response.\nWe couldn't find any appointments that need confirmation.\n" +
                                     "If this doesn’t seem right, please give us a call.";
            ODSMSLogger.Instance.Log("Sending single SMS", EventLogEntryType.Information, logToEventLog: true);

            SmsToMobile sentSms = null;
            try
            {
                sentSms = SmsToMobiles.SendSmsSingle(
                    patNum: patient.PatNum,
                    wirelessPhone: mobileNumber,
                    message: matchSMSmessage,
                    clinicNum: 0, // We don't use clinics
                    smsMessageSource: SmsMessageSource.ConfirmationAutoReply,
                    makeCommLog: true, // Ensuring that a communication log is always written
                    canCheckBal: false // Explicitly passing false as requested
                );
            }
            catch (Exception ex)
            {
                ODSMSLogger.Instance.Log("Error sending SMS: " + ex.Message, EventLogEntryType.Error, logToEventLog: true);
                throw new ODException("Failed to send SMS", ex);
            }

            // Handle the sent SMS response
            if (sentSms.SmsStatus == SmsDeliveryStatus.FailNoCharge)
            {
                throw new ODException(sentSms.CustErrorText);
            }

            return SystemTask.CompletedTask;
        }


        private static void InsertConfirmationFailureCommlog(SmsToMobile matchSMS)
        {
            Commlogs.Insert(new Commlog()
            {
                CommDateTime = matchSMS.DateTimeSent,
                Mode_ = CommItemMode.Text,
                Note = "Text message sent: " + matchSMS.MsgText,
                PatNum = matchSMS.PatNum,
                CommType = Commlogs.GetTypeAuto(CommItemTypeAuto.TEXT),
                SentOrReceived = CommSentOrReceived.Sent,
                UserNum = 0
            });
        }
    }
}