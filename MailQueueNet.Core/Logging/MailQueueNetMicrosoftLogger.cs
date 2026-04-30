// <copyright file="MailQueueNetMicrosoftLogger.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
//
//  Derived from “MailQueueNet” by Daniel Cohen Gindi
//  (https://github.com/danielgindi/MailQueueNet).
//
//  Original portions:
//    © 2014 Daniel Cohen Gindi (danielgindi@gmail.com)
//    Licensed under the MIT Licence.
//  Modifications and additions:
//    © 2025 IBC Digital Pty Ltd
//    Distributed under the same MIT Licence.
//
//  The above notice and this permission notice shall be included in
//  all copies or substantial portions of this file.
// </copyright>

namespace MailQueueNet.Core.Logging
{
    using System;
    using Microsoft.Extensions.Logging;
    using IbcLogLevel = IBC.Logging.LogLevel;
    using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

    /// <summary>
    /// Forwards Microsoft logging messages into the MailQueueNet IBC logger.
    /// </summary>
    internal sealed class MailQueueNetMicrosoftLogger : ILogger
    {
        private readonly string categoryName;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailQueueNetMicrosoftLogger"/> class.
        /// </summary>
        /// <param name="categoryName">The Microsoft logger category name.</param>
        public MailQueueNetMicrosoftLogger(string categoryName)
        {
            this.categoryName = categoryName ?? string.Empty;
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullLoggerScope.Instance;
        }

        /// <inheritdoc />
        public bool IsEnabled(MicrosoftLogLevel logLevel)
        {
            return logLevel != MicrosoftLogLevel.None && MailQueueNetLogger.IsConfigured;
        }

        /// <inheritdoc />
        public void Log<TState>(MicrosoftLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            var logName = this.ResolveLogName(logLevel);
            var ibcLevel = this.ResolveIbcLogLevel(logLevel);
            var formatted = this.FormatMessage(logLevel, eventId, message, exception);

            MailQueueNetLogger.LogMessage(formatted, logName, ibcLevel);
            MailQueueNetLogger.SaveLogFiles(true);
        }

        private string FormatMessage(MicrosoftLogLevel logLevel, EventId eventId, string message, Exception? exception)
        {
            var formatted = $"[{logLevel}] {this.categoryName}";
            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                formatted += $" EventId={eventId.Id}:{eventId.Name}";
            }

            formatted += $" - {message}";
            if (exception != null)
            {
                formatted += Environment.NewLine + exception;
            }

            return formatted;
        }

        private IbcLogLevel ResolveIbcLogLevel(MicrosoftLogLevel logLevel)
        {
            return logLevel switch
            {
                MicrosoftLogLevel.Trace => IbcLogLevel.Verbose,
                MicrosoftLogLevel.Debug => IbcLogLevel.Debug,
                MicrosoftLogLevel.Information => IbcLogLevel.Access,
                MicrosoftLogLevel.Warning => IbcLogLevel.Debug,
                MicrosoftLogLevel.Error => IbcLogLevel.None,
                MicrosoftLogLevel.Critical => IbcLogLevel.None,
                _ => IbcLogLevel.None,
            };
        }

        private string ResolveLogName(MicrosoftLogLevel logLevel)
        {
            if (logLevel >= MicrosoftLogLevel.Error)
            {
                return LogFileTypes.ExceptionLog;
            }

            if (this.categoryName.StartsWith("Admin", StringComparison.OrdinalIgnoreCase)
                || this.categoryName.Contains("Security", StringComparison.OrdinalIgnoreCase)
                || this.categoryName.Contains("Audit", StringComparison.OrdinalIgnoreCase))
            {
                return LogFileTypes.SecurityLog;
            }

            if (this.categoryName.StartsWith("MailSent", StringComparison.OrdinalIgnoreCase)
                || this.categoryName.StartsWith("MailFailed", StringComparison.OrdinalIgnoreCase)
                || this.categoryName.Contains("MailRequest", StringComparison.OrdinalIgnoreCase))
            {
                return LogFileTypes.EmailLog;
            }

            if (logLevel == MicrosoftLogLevel.Information)
            {
                return LogFileTypes.AccessLog;
            }

            return LogFileTypes.DebugLog;
        }
    }
}
