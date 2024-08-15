// <copyright file="WaarbleLogger.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Core.Logging
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using IBC.Application;
    using IBC.Logging;
    using IBC.Logging.Configuration;

    /// <summary>
    /// Waarble specific implementation of IbcLogger.
    /// </summary>
    public class MailQueueNetLogger : IbcLogger, IStaticLogger
    {
        /// <summary>
        /// Default log buffer size = 32k.
        /// </summary>
        public const int DefaultLogBufferSize = 32768;

        /// <summary>
        /// The is configured.
        /// </summary>
        private static bool isConfigured = false;

        /// <summary>
        /// The is initialised.
        /// </summary>
        private static bool isInitialised = false;

        /// <summary>
        /// The log level.
        /// </summary>
        private static LogLevel logLevel = LogLevel.Exceptions;

        /// <summary>
        /// static access to the configuration loggign settings.
        /// </summary>
        private static LoggingSettings? loggingSettings = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="MailQueueNetLogger"/> class.
        /// Default constructor. Instantiate a WaarbleLogger instance.
        /// </summary>
        public MailQueueNetLogger()
            : base()
        {
            if (IStaticLogger.CurrentLogger == null)
            {
                IStaticLogger.CurrentLogger = this;
            }

            // initialising the LoggingSettings will create the config.
            LoggingSettings config = new LoggingSettings();
        }

        /// <summary>
        /// Gets the SingletonInstance of a Waarble Logger;.
        /// </summary>
        public static MailQueueNetLogger? GetInstance
        {
            get
            {
                MailQueueNetLogger? retVal = null;

                if (IStaticLogger.CurrentLogger != null)
                {
                    retVal = (MailQueueNetLogger)IStaticLogger.CurrentLogger;
                }

                return retVal;
            }
        }

        /// <summary>
        ///   Gets a value indicating whether is true if the Logger has been configured.
        /// </summary>
        public static bool IsConfigured
        {
            get
            {
                return isConfigured;
            }
        }

        /// <summary>
        ///   Gets a value indicating whether is true if the Logger has been initialised.
        /// </summary>
        public static bool IsInitialised
        {
            get
            {
                return isInitialised;
            }
        }

        /// <summary>
        ///   Gets or sets allow setting of the waarble Instance logging level.
        /// </summary>
        public static LogLevel LogLevel
        {
            get
            {
                return logLevel;
            }

            set
            {
                logLevel = value;
            }
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the debug type set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="logLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <param name="debugType">
        /// Helps constrain logging when only interested in a certain area of processing, Must match a configutation setting that is set to true to log.
        /// </param>
        public static void SpecialLog(string message, string logName, LogLevel logLevel, string debugType)
        {
            if (IsInitialised)
            {
                if (debugType == null || (loggingSettings != null && loggingSettings.DebugSpecialFlags != null && loggingSettings.DebugSpecialFlags.Contains(debugType)))
                {
                    LogMessage(message, logName, logLevel);
                }
            }
            else
            {
                Trace.WriteLine(message);
            }
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the debug type set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches. Defaults to Debug Log and debug log level.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="debugType">
        /// Helps constrain logging when only interested in a certain area of processing, Must match a configutation setting that is set to true to log.
        /// </param>
        public static void SpecialLog(string message, string debugType)
        {
            SpecialLog(message, LogFileTypes.DebugLog, LogLevel.Debug, debugType);
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the any on e of the debug types set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="logLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <param name="debugTypes">
        /// Helps constrain logging when only interested in certain areas of processing, Must match any one configutation setting that is set to true to log.
        /// </param>
        public static void SpecialLog(string message, string logName, LogLevel logLevel, string[] debugTypes)
        {
            if (loggingSettings != null && loggingSettings.DebugSpecialFlags != null)
            {
                foreach (string debugType in debugTypes)
                {
                    if (loggingSettings != null && loggingSettings.DebugSpecialFlags != null && loggingSettings.DebugSpecialFlags.Contains(debugType))
                    {
                        SpecialLog(message, logName, logLevel, debugType);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the any one of the debug types set to true. If configuration setting "Log All Debug Logs" is true then it will log if debug level matches. Defaults to Debug Log and debug log level.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="debugTypes">
        /// Helps constrain logging when only interested in certain areas of processing, Must match any one configutation setting that is set to true to log.
        /// </param>
        public static void SpecialLog(string message, string[] debugTypes)
        {
            SpecialLog(message, LogFileTypes.DebugLog, LogLevel.Debug, debugTypes);
        }

        /// <summary>
        /// Initialises the Log system for use.
        /// </summary>
        public static void InitialiseLogging()
        {
            try
            {
                if (MailQueueNetLogger.GetInstance != null)
                {
                    // Create Exception log file
                    MailQueueNetLogger.GetInstance.CreateLogFile(LogFileTypes.AccessLog);

                    // Create Exception log file
                    MailQueueNetLogger.GetInstance.CreateLogFile(LogFileTypes.ExceptionLog);

                    // Create Debug log file
                    MailQueueNetLogger.GetInstance.CreateLogFile(LogFileTypes.DebugLog);

                    // Create the Security log file
                    MailQueueNetLogger.GetInstance.CreateLogFile(LogFileTypes.SecurityLog);

                    // Create the Email Sending log file
                    MailQueueNetLogger.GetInstance.CreateLogFile(LogFileTypes.EmailLog);

                    isInitialised = true;
                }
            }
            catch (Exception ex)
            {
                LogMessage("WaarbleLogger: Error Initialising Log\r\n" + ex, LogFileTypes.ExceptionLog);
            }
        }

        /// <summary>
        /// Static method that creates a WaarbleLogger instance and then logs the message.
        /// </summary>
        /// <param name="message">
        /// Message to be logged.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <remarks>
        /// Requires that the WaarbleLogger logger is previously instantiated in the application.
        /// </remarks>
        public static void LogMessage(string message, string logName)
        {
            if (IsInitialised)
            {
                var msgLogLevel = logName switch
                {
                    LogFileTypes.AccessLog => LogLevel.Access,
                    LogFileTypes.DebugLog => LogLevel.Debug,

                    // ExceptionLog, SecurityLog, and any other log messages we default to lowest log level to ensure they get logged.
                    _ => LogLevel.None,
                };
                LogMessage(message, logName, msgLogLevel);
            }
            else
            {
                Trace.WriteLine(message);
            }
        }

        /// <summary>
        /// Static method that creates a WaarbleLogger instance and then logs the message.
        /// </summary>
        /// <param name="message">
        /// Message to be logged.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="msgLogLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <remarks>
        /// Requires that the WaarbleLogger logger is previously instantiated in the application.
        /// </remarks>
        public static void LogMessage(string message, string logName, LogLevel msgLogLevel)
        {
            if (IsInitialised && MailQueueNetLogger.GetInstance != null)
            {
                if (LogLevel >= msgLogLevel)
                {
                    MailQueueNetLogger.GetInstance.Log(message, logName);

                    // if current log level is debug or greater force a save log file.
                    if (LogLevel >= LogLevel.Debug && loggingSettings?.ForceDebugSave != null && loggingSettings.ForceDebugSave.Value)
                    {
                        SaveLogFiles(true);
                    }
                }
            }
            else
            {
                Trace.WriteLine(message);
            }
        }

        /// <summary>
        /// Logs and exception to the exception log and forces a save.
        /// </summary>
        /// <param name="message">Message to be logged.</param>
        public static void LogException(string message)
        {
            LogMessage(message, LogFileTypes.ExceptionLog, LogLevel.None);
            SaveLogFiles(true);
        }

        /// <summary>
        /// Saves all log files in use.
        /// </summary>
        /// <param name="forceSave">
        /// The force Save.
        /// </param>
        public static void SaveLogFiles(bool forceSave)
        {
            if (isInitialised && MailQueueNetLogger.GetInstance != null)
            {
                // Save the log files.
                MailQueueNetLogger.GetInstance.Save(LogFileTypes.AccessLog, forceSave);
                MailQueueNetLogger.GetInstance.Save(LogFileTypes.ExceptionLog, forceSave);
                MailQueueNetLogger.GetInstance.Save(LogFileTypes.DebugLog, forceSave);
                MailQueueNetLogger.GetInstance.Save(LogFileTypes.SecurityLog, forceSave);
                MailQueueNetLogger.GetInstance.Save(LogFileTypes.EmailLog, forceSave);
            }
        }

        /// <summary>
        /// Saves all log files in use.
        /// </summary>
        public static void SaveLogFiles()
        {
            SaveLogFiles(false);
        }

        /// <summary>
        /// Sets the log level based on the value in the Waarble configuration file.
        /// </summary>
        public static void SetLogLevelFromConfig()
        {
            int theLogLevel = 1;

            if (loggingSettings != null && loggingSettings.ApplicationLogLevel != null)
            {
                theLogLevel = loggingSettings.ApplicationLogLevel ?? theLogLevel;
            }

            if (theLogLevel > (int)LogLevel.Verbose)
            {
                logLevel = LogLevel.Verbose;
            }
            else
            {
                logLevel = (LogLevel)theLogLevel;
            }
        }

        /// <summary>
        /// Configures the Logging system the Log system for use.
        /// </summary>
        /// <remarks>
        /// WaarbleConfig must be initialised.
        /// </remarks>
        public void ConfigureLogging()
        {
            try
            {
                if (LoggingSettings.Current == null)
                {
                    loggingSettings = new LoggingSettings();
                }
                else
                {
                    loggingSettings = LoggingSettings.Current;
                }

                if (!isInitialised)
                {
                    InitialiseLogging();
                }

                SetLogLevelFromConfig();

                // Setting the log memory buffersize.
                BufferSize = loggingSettings.LogBufferSize ?? DefaultLogBufferSize;

                isConfigured = true;
            }
            catch (Exception ex)
            {
                LogMessage("ERROR: Initialising Log\r\n" + ex, LogFileTypes.ExceptionLog);
            }
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the debug type set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="logLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <param name="debugType">
        /// Helps constrain logging when only interested in a certain area of processing, Must match a configutation setting that is set to true to log.
        /// </param>
        public void InstanceSpecialLog(string message, string logName, LogLevel logLevel, string debugType)
        {
            MailQueueNetLogger.SpecialLog(message, logName, logLevel, debugType);
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the debug type set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches. Defaults to Debug Log and debug log level.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="debugType">
        /// Helps constrain logging when only interested in a certain area of processing, Must match a configutation setting that is set to true to log.
        /// </param>
        public void InstanceSpecialLog(string message, string debugType)
        {
            MailQueueNetLogger.SpecialLog(message, debugType);
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the any on e of the debug types set to true If configuration setting "Log All Debug Logs" is true then it will log if debug level matches.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="logLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <param name="debugTypes">
        /// Helps constrain logging when only interested in certain areas of processing, Must match any one configutation setting that is set to true to log.
        /// </param>
        public void InstanceSpecialLog(string message, string logName, LogLevel logLevel, string[] debugTypes)
        {
            MailQueueNetLogger.SpecialLog(message, logName, logLevel, debugTypes);
        }

        /// <summary>
        /// Will output to debug logs only if conifguration file has the any one of the debug types set to true. If configuration setting "Log All Debug Logs" is true then it will log if debug level matches. Defaults to Debug Log and debug log level.
        /// </summary>
        /// <param name="message">
        /// The message to log.
        /// </param>
        /// <param name="debugTypes">
        /// Helps constrain logging when only interested in certain areas of processing, Must match any one configutation setting that is set to true to log.
        /// </param>
        public void InstanceSpecialLog(string message, string[] debugTypes)
        {
            MailQueueNetLogger.SpecialLog(message, debugTypes);
        }

        /// <summary>
        /// Static method that creates a WaarbleLogger instance and then logs the message.
        /// </summary>
        /// <param name="message">
        /// Message to be logged.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <remarks>
        /// Requires that the WaarbleLogger logger is previously instantiated in the application.
        /// </remarks>
        public void InstanceLogMessage(string message, string logName)
        {
            MailQueueNetLogger.LogMessage(message, logName);
        }

        /// <summary>
        /// Static method that creates a WaarbleLogger instance and then logs the message.
        /// </summary>
        /// <param name="message">
        /// Message to be logged.
        /// </param>
        /// <param name="logName">
        /// The log file to log to either WaarbleLogger.AccessLog, WaarbleLogger.DebugLog or WaarbleLogger.ExceptionLog.
        /// </param>
        /// <param name="msgLogLevel">
        /// Defines the log level the application needs to be running to write the log.
        /// </param>
        /// <remarks>
        /// Requires that the WaarbleLogger logger is previously instantiated in the applicatio.
        /// </remarks>
        public void InstanceLogMessage(string message, string logName, LogLevel msgLogLevel)
        {
            MailQueueNetLogger.LogMessage(message, logName, msgLogLevel);
        }

        /// <summary>
        /// Logs and exception to the exception log and forces a save.
        /// </summary>
        /// <param name="message">Message to be logged.</param>
        public void InstanceLogException(string message)
        {
            MailQueueNetLogger.LogException(message);
        }

        /// <summary>
        /// Gets the instance specific version of the log name based on common log names.
        /// </summary>
        /// <param name="logName">The name of the common log name.</param>
        /// <returns>The instance equivilent log name.</returns>
        public string InstanceGetLogName(string logName)
        {
            if (logName == CommonLogNames.ExceptionLog)
            {
                return LogFileTypes.ExceptionLog;
            }

            if (logName == CommonLogNames.DebugLog)
            {
                return LogFileTypes.DebugLog;
            }

            if (logName == CommonLogNames.AccessLog)
            {
                return LogFileTypes.AccessLog;
            }

            if (logName == CommonLogNames.SecurityLog)
            {
                return LogFileTypes.SecurityLog;
            }

            return LogFileTypes.DebugLog;
        }

        /// <summary>
        /// Saves all log files in use.
        /// </summary>
        /// <param name="forceSave">
        /// The force Save.
        /// </param>
        public void InstanceSaveLogFiles(bool forceSave)
        {
            MailQueueNetLogger.SaveLogFiles(forceSave);
        }

        /// <summary>
        /// The create log file.
        /// </summary>
        /// <param name="logName">
        /// The log name.
        /// </param>
        public override void CreateLogFile(string logName)
        {
            string initialLogLine = string.Empty;
            this.CreateLogFile(logName, initialLogLine);
        }

        /// <summary>
        /// creates a new log for the waarble Instance.
        /// </summary>
        /// <param name="logName">
        /// The name of the log. Needs to be unique for each separate log per application.
        /// </param>
        /// <param name="initialLogLine">
        /// The line at the top of each log file.
        /// </param>
        public void CreateLogFile(string logName, string initialLogLine)
        {
            // create the access logger
            string logFilePath = this.CreateLogFilePath(logName);

            // set up the access log
            this.NewLog(logName, logFilePath, initialLogLine);
        }

        /// <summary>
        /// creates a new log for the waarble Instance.
        /// </summary>
        /// <param name="logFilePath">
        /// passes the current waarble Instance HttpContext through so it can figure out the location of the log file directory.
        /// </param>
        /// <param name="logName">
        /// The name of the log. Needs to be unique for each separate log per application.
        /// </param>
        /// <param name="initialLogLine">
        /// The line at the top of each log file.
        /// </param>
        public void CreateLogFile(string logFilePath, string logName, string initialLogLine)
        {
            this.NewLog(logName, logFilePath, initialLogLine);
        }

        /// <summary>
        /// Creates the log file path for the current day.
        /// </summary>
        /// <param name="logName">
        /// The name of the log.
        /// </param>
        /// <returns>
        /// The full path to the current log file.
        /// </returns>
        public string CreateLogFilePath(string logName)
        {
            // retrieve the Waarble Log File Dir
            string logFilePath = this.GetLoggingDir();

            // add the log file name to the path
            logFilePath = Path.Combine(logFilePath, logName);

            // add the unique identifier and file extension
            logFilePath += this.GetFileIdentifier();

            return logFilePath;
        }

        /// <summary>
        /// Returns a string to identify the log files that are being cycled.
        /// </summary>
        /// <returns> identity to be included in a file name to assist in file rotations.</returns>
        public string GetFileIdentifier()
        {
            return DateTime.Now.ToString("yyyyMMdd") + ".log";
        }

        /// <summary>
        /// returns the log file directory for the waarble instance.
        /// </summary>
        /// <returns>
        /// The path that the current waarble instance stores its logs.
        /// </returns>
        public string GetLoggingDir()
        {
            string logFilePath = string.Empty;

            if (loggingSettings != null && !string.IsNullOrEmpty(loggingSettings.LogFilesFolder))
            {
                if (!Directory.Exists(loggingSettings.LogFilesFolder))
                {
                    Directory.CreateDirectory(loggingSettings.LogFilesFolder);
                }

                logFilePath = loggingSettings.LogFilesFolder;
            }

            if (string.IsNullOrEmpty(logFilePath))
            {
                // get the path to the root of the waarble Web application
                logFilePath = ApplicationFolder.Current;

                // ensure the path ends with a slash
                if (!logFilePath.EndsWith(Path.DirectorySeparatorChar))
                {
                    logFilePath += "Path.DirectorySeparatorChar";
                }

                // make the log dir a sibling dir to the web root
                logFilePath += @".." + Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar;
            }

            return logFilePath;
        }

        /// <summary>
        /// Function will set the format of the log lines to always include a date.
        /// </summary>
        /// <param name="logText">
        /// the log line that needs to be formatted.
        /// </param>
        /// <param name="logName">Allows different log formats per log.</param>
        /// <returns>
        /// The formatted string.
        /// </returns>
        public override string LogFormat(string logText, string logName)
        {
            try
            {
                StringBuilder logTextBuilder = new StringBuilder();

                DateTime now = DateTime.Now;
                logTextBuilder.Append(now.ToString("yyyy-MM-dd HH:mm:ss:ffff - "));

                if (Task.CurrentId != null)
                {
                    logTextBuilder.Append("TASK[");
                    logTextBuilder.Append(Task.CurrentId);
                    logTextBuilder.Append("] ");
                }

                /*if (WaarbleAppMiddleware.RequestIdentifier != null)
                {
                    logTextBuilder.Append("Request[");
                    logTextBuilder.Append(WaarbleAppMiddleware.RequestIdentifier);
                    logTextBuilder.Append("] ");
                }

                if (AppMonitor.DebugStopWatch != null)
                {
                    logTextBuilder.Append("t:");
                    logTextBuilder.Append(AppMonitor.DebugStopWatch.ElapsedMilliseconds);
                    logTextBuilder.Append(" s:");
                    logTextBuilder.Append(AppMonitor.GetDebugStopWatchSplit(logName));
                }

                logTextBuilder.Append(" {");

                // TODO: add logged in user details.
                if (IbcHttpContext.Current != null)
                {
                    logTextBuilder.Append(IbcHttpContext.Current.GetRemoteIPAddress());
                }

                logTextBuilder.Append("} ");*/
                logTextBuilder.Append(logText);

                return logTextBuilder.ToString();
            }
            catch (Exception)
            {
                return logText;
            }
        }

        /// <summary>
        /// Saves the specified log. Will check the log file name and rotate it if GetFileIdentifier returns a new suffix.
        /// </summary>
        /// <param name="log">
        /// the log to save.
        /// </param>
        /// <param name="forceSave">defines if log should be save straignt away or wait until page request is over.</param>
        /// <remarks>
        /// Assumes that GetFileIdentifier always returns a string of the same length.
        /// </remarks>
        public new void Save(string log, bool forceSave)
        {
            string currentLogFile = this.GetLogFile(log);
            string currentSuffix = this.GetFileIdentifier();

            if (!string.IsNullOrEmpty(currentLogFile))
            {
                // if the current suffix doesn't match the suffix provided by the function GetFileIdentifier then reset the path
                if (!currentLogFile.EndsWith(currentSuffix))
                {
                    currentLogFile = currentLogFile.Substring(0, currentLogFile.Length - currentSuffix.Length)
                                     + currentSuffix;
                    this.SetLogFile(log, currentLogFile);
                }

                base.Save(log, forceSave);
            }
        }
    }
}
