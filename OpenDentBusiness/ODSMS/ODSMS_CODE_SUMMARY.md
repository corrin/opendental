# ODSMS Code Summary

This document provides a summary of the C# files in the `OpenDentBusiness/ODSMS` directory.

## AppointmentHelper.cs

This file contains a helper class for parsing appointment dates from strings.

- **`ParseDate(string datePart)`**: Parses a date string into a `DateTime` object. It can handle various date formats.
- **`ExtractAppointmentDateInternal(string note)`**: Extracts a date from a string using a regular expression.
- **`ExtractAppointmentDate(string note)`**: A public method that calls `ExtractAppointmentDateInternal` and also runs a suite of tests to ensure the parsing logic is correct.
- **`TestAppointmentHelper()`**: A method that contains a suite of tests for the `ExtractAppointmentDateInternal` method.

## ODSMS.cs

This is the main file for the ODSMS (Open Dental SMS) functionality. It contains the core logic for sending and receiving SMS messages.

- **`SmsTemplateKeys`**: A class that defines constants for the different types of SMS templates.
- **`SmsTemplateData`**: A class that represents an SMS template.
- **`ODSMS` class**:
  - **Configuration variables**: Contains several static variables for configuring the ODSMS functionality, such as `USE_ODSMS`, `SEND_SMS`, and `WRITE_TO_DATABASE`.
  - **Internal state**: Contains variables for managing the internal state of the ODSMS functionality, such as `wasSmsBroken` and `initialStartup`.
  - **Variables from the configuration file**: Contains variables that are loaded from the `odsms.txt` configuration file, such as `SMS_BRIDGE_NAME` and `WEBSERVER_API_KEY`.
  - **`InitializeSMS()`**: Initializes the ODSMS functionality by loading the necessary data from the database.
  - **`InitializeEventLog()`**: Initializes the event log for the ODSMS functionality.
  - **`ValidateConfigPath(string configPath)`**: Validates the path to the configuration file.
  - **`LoadConfiguration(string configPath, string machineName)`**: Loads the configuration from the `odsms.txt` file.
  - **`ProcessConfigLine(string line)`**: Processes a single line from the configuration file.
  - **`ValidateSMSBridgeName()`**: Validates the name of the SMS bridge.
  - **`DoesReceiverMatchLocal(string receiver)`**: Checks if the receiver matches the local machine.
  - **`ValidateSMSBridge()`**: Validates the SMS bridge.
  - **`ValidateConfiguration()`**: Validates the configuration.
  - **`LogConfigurationStatus(string MachineName)`**: Logs the configuration status.
  - **`WaitForDatabaseAndUserInitialization()`**: Waits for the database and user to be initialized.
  - **`RenderReminder(string reminderTemplate, Patient p, Appointment a, int? earlyMinutes = null)`**: Renders a reminder message.
  - **`InitializeAndRunSmsTasks()`**: Initializes and runs the SMS tasks.
  - **`LoadTemplatesFromSheets()`**: Loads the SMS templates from a Google Sheet.

## ODSMSBridgeInterface.cs

This file contains the interface for communicating with the SMS bridge.

- **`SendSmsRequest`**: A class that represents a request to send an SMS message.
- **`Result`**: A class that represents the result of an operation.
- **`ReceivedSmsMessage`**: A class that represents a received SMS message.
- **`DebugStatusResponse`**: A class that represents the debug status of the SMS bridge.
- **`MessageStatus`**: An enum that represents the status of an SMS message.
- **`MessageStatusResponse`**: A class that represents the status of an SMS message.
- **`ODSMSBridgeInterface` class**:
  - **`SendSmsViaHttp(string phoneNumber, string message)`**: Sends an SMS message via HTTP.
  - **`GetMessageStatus(string messageId)`**: Gets the status of an SMS message.
  - **`IsLowestProcessId(string callerType)`**: Checks if the current process has the lowest process ID.
  - **`GetMinutesUntilQuarterPast(DateTime now)`**: Gets the number of minutes until the next quarter past the hour.
  - **`GetSecondsUntilNextQuarterPast(DateTime now)`**: Gets the number of seconds until the next quarter past the hour.
  - **`ManageScheduledSMSSending()`**: Manages the sending of scheduled SMS messages.
  - **`WaitForMessageStatus(string messageId, int maxAttempts = 20, int delayMs = 1000)`**: Waits for the status of an SMS message to be updated.
  - **`CheckForReceivedMessages()`**: Checks for received SMS messages.
  - **`DeleteReceivedMessage(string messageId)`**: Deletes a received SMS message.
  - **`ManageScheduledSMSReceiving()`**: Manages the receiving of scheduled SMS messages.
  - **`GetDebugStatus()`**: Gets the debug status of the SMS bridge.

## ODSMSLogger.cs

This file contains a logger for the ODSMS functionality.

- **`ODSMSLogger` class**:
  - **`Log(string message, EventLogEntryType severity = EventLogEntryType.Warning, bool logToConsole = true, bool logToEventLog = true, bool logToFile = true)`**: Logs a message to the console, event log, and a file.
  - **`Close()`**: Closes the log file.
  - **`LogToEventLog(string message, EventLogEntryType severity)`**: Logs a message to the event log.

## ReceiveSMS.cs

This file contains the logic for receiving SMS messages.

- **`ReceiveSMS` class**:
  - **`ProcessSmsMessage(string msgFrom, string msgText, DateTime msgTime, Guid msgGUID)`**: Processes a received SMS message.
  - **`IsAppointmentAlreadyConfirmed(Appointment appointment)`**: Checks if an appointment is already confirmed.
  - **`UpdateAppointmentStatus(Appointment originalAppt, long confirmationStatus)`**: Updates the status of an appointment.
  - **`GetConfirmationStatus(int daysUntilAppointment)`**: Gets the confirmation status for an appointment.
  - **`HandleAutomatedConfirmationInternal(List<Patient> patientList)`**: Handles automated appointment confirmations.
  - **`ProcessOneReceivedSMS(string msgText, DateTime msgTime, string msgFrom, string msgGUIDstr)`**: Processes a single received SMS message.
  - **`CreateCommlog(List<Patient> patients, string msgText, DateTime time)`**: Creates a communication log entry.
  - **`CreateSmsFromMobile(Commlog log, string msgFrom, DateTime time, string msgText, int patientCount)`**: Creates an `SmsFromMobile` object.
  - **`HandleAutomatedConfirmation(List<Patient> patients, SmsFromMobile sms)`**: Handles automated appointment confirmations.
  - **`SendConfirmationFailureMessage(Patient patient, string mobileNumber)`**: Sends a confirmation failure message.

## SendSMS.cs

This file contains the logic for sending SMS messages.

- **`ReminderFilterType`**: An enum that represents the type of reminder to send.
- **`SendSMS` class**:
  - **`GetPatientsWithBirthdayToday()`**: Retrieves patients with birthdays today.
  - **`GetPatientsWithCompletedProceduresYesterday()`**: Retrieves patients with completed procedures yesterday. This method now includes all patients, regardless of their active status, for post-op texts.
  - **`SendProcedureFollowupTexts()`**: Orchestrates sending post-op texts.
  - **`SendBirthdayTexts()`**: Orchestrates sending birthday texts.
  - **`PrepareBirthdayMessages(...)`**: Prepares birthday messages.
  - **`PrepareFollowupMessages(...)`**: Prepares post-op messages.
  - **`PrepareReminderMessages(...)`**: Prepares appointment reminder messages.
  - **`SendSmsViaHttp(...)`**: Sends a single SMS via HTTP to the bridge.
  - **`SendAndUpdateAppointments(...)`**: Sends reminder messages and updates appointment statuses.
  - **`GetUpdatedConfirmationStatus(...)`**: Determines the confirmation status after a reminder is sent.
  - **`SendReminderTexts()`**: Orchestrates sending appointment reminder texts.
  - **`GetPatientsWithAppointmentsTwoWeeks(...)`**: Retrieves patients with appointments for reminders. This method now includes WebSched appointments and does not filter by patient status.
  - **`ToSmsDeliveryStatus(...)`**: Converts message status from the bridge to internal SMS delivery status.
  - **`SendMultipleMessagesAsync(...)`**: Handles sending multiple SMS messages and confirming delivery.
  - **`GetAppointmentConfirmedWhereClause(...)`**: Constructs the SQL WHERE clause for appointment confirmation status, now including WebSched appointments.
