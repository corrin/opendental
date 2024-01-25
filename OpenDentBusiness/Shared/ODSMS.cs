﻿using CodeBase;
using System;
using System.IO;
using System.Diagnostics;
using System.Windows.Shapes;

namespace OpenDental
{
    public static class ODSMS
    {
        public static string AUTH;  // username/password for diafaan
        public static string URL;   // URL for diafaan
        public static bool RECEIVE_SMS = false;   // should we receive text on this computer?

        static ODSMS()
        {

            // Set up event log -- must be run as Administrator
            try
            {
                EventLog.CreateEventSource("ODSMS", "Application");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.WriteLine("Event source already exists, or we don't have permission to create it.");
                EventLog.WriteEntry("ODSMS", "Event source already exists, or we don't have permission to create it", EventLogEntryType.Information, 101, 1, new byte[10]);
            }

            string configPath = @"L:\odsms.txt";
            try
            {
                foreach (string line in System.IO.File.ReadLines(configPath))
                {
                    if (line.StartsWith("AUTH:"))
                        AUTH = line.Replace("AUTH:", "");
                    else if (line.StartsWith("URL:"))
                        URL = line.Replace("URL:", "");
                    if (line.StartsWith("RECEIVER:"))
                    {
                        string receiver_name = line.Replace("RECEIVER:", "");
                        if (receiver_name == ODEnvironment.MachineName)
                        {
                            EventLog.WriteEntry("ODSMS", "Name matches, enabling SMS reception", EventLogEntryType.Information, 101, 1, new byte[10]);
                            RECEIVE_SMS = true;
                        }
                    }
                }
            }
            catch (FileNotFoundException) {
                EventLog.WriteEntry("ODSMS", "odsms.txt config file not found - stuff is about to break", EventLogEntryType.Error, 101, 1, new byte[10]);
                throw;
            }

            EventLog.WriteEntry("ODSMS", "Successfully loaded odsms.txt config file", EventLogEntryType.Information, 101, 1, new byte[10]);
        }
    }
}
