// <copyright file="LogFileTypes.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Core.Logging
{
    /// <summary>
    /// List of Costants defining the types of log files in use.
    /// </summary>
    public class LogFileTypes
    {
        /// <summary>
        /// The access log.
        /// </summary>
        public const string AccessLog = "access";

        /// <summary>
        /// The debug log.
        /// </summary>
        public const string DebugLog = "debug";

        /// <summary>
        /// The exception log.
        /// </summary>
        public const string ExceptionLog = "exception";

        /// <summary>
        /// The email sending log.
        /// </summary>
        public const string EmailLog = "email";

        /// <summary>
        /// The security log.
        /// </summary>
        public const string SecurityLog = "security";
    }
}