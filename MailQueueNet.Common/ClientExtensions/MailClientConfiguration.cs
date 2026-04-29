// <copyright file="MailClientConfiguration.cs" company="IBC Digital">
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
// <license>
// MIT Licence – see the repository root LICENCE file for full text.
// </license>

namespace MailQueueNet.Grpc
{
    using IBC.Application;

    using Microsoft.Extensions.Configuration;

    /// <summary>
    /// MailClientConfiguration a configuration representation of the MailClientConfiguration settings.
    /// </summary>
    public class MailClientConfiguration
    {
        /// <summary>
        /// Name of the configruation Section.
        /// </summary>
        public const string MailClientConfigurationSettingsSection = "MailClientConfigurationSettings";

        private static readonly object Mutex = new object();

        /// <summary>
        /// Initializes static members of the <see cref="MailClientConfiguration"/> class.
        /// Inisialise the MailClientConfiguration through static constructor.
        /// </summary>
        static MailClientConfiguration()
        {
            MailClientConfiguration init = new MailClientConfiguration();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MailClientConfiguration"/> class.
        /// </summary>
        public MailClientConfiguration()
        {
            if (Current == null)
            {
                lock (Mutex)
                {
                    if (Current == null)
                    {
                        if (AppContext.Configuration != null)
                        {
                            var section = AppContext.Configuration.GetSection(MailClientConfigurationSettingsSection);

                            section.Bind(this);

                            Current = this;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the singleton instance of the
        /// <see cref="MailClientConfiguration"/> currently in use.
        /// </summary>
        /// <value>
        /// A reference to the configuration object loaded from
        /// <c>MailClientConfigurationSettings</c>; <see langword="null"/> if the
        /// configuration has not yet been initialised.
        /// </value>
        public static MailClientConfiguration? Current { get; set; } = null;

        /// <summary>
        /// Gets or sets a value indicating whether the client should write
        /// outbound messages to disk on failure so they can be re-queued if the
        /// gRPC service is unreachable.
        /// </summary>
        /// <value>
        /// <see langword="true"/> to enable local disk resilience; otherwise
        /// <see langword="false"/>. The default is <c>false</c>.
        /// </value>
        public bool EnableDiskResilience { get; set; } = false;

        /// <summary>
        /// Gets or sets the absolute path of the folder used to store messages
        /// that have exhausted all retry attempts or could not be sent due to
        /// a fatal error.
        /// </summary>
        /// <value>
        /// A valid folder path such as <c>C:\mail\undelivered</c>. The folder
        /// is created at runtime if it does not already exist.
        /// </value>
        public string UndeliveredFolder { get; set; } = "C:\\mail\\undelivered";

        /// <summary>
        /// Gets or sets the maximum number of retry attempts the client should
        /// make for a single message before it is moved to
        /// <see cref="UndeliveredFolder"/>.
        /// </summary>
        /// <value>
        /// A non-negative integer. The default is <c>5</c>.
        /// </value>
        public int RetryCount { get; set; } = 5;

        /// <summary>
        /// Gets or sets the exponential back-off factor applied between retry
        /// attempts. Each successive retry waits
        /// <c>previousDelay&nbsp;×&nbsp;<see cref="RetryBackoffFactor"/></c>.
        /// </summary>
        /// <value>
        /// A positive floating-point number, typically between <c>1</c> and <c>10</c>.
        /// The default is <c>2.0</c>.
        /// </value>
        public double RetryBackoffFactor { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the interval, in minutes, between scheduled scans of
        /// the <see cref="UndeliveredFolder"/>.
        /// </summary>
        public int UnsentCheckIntervalMinutes { get; set; } = 120;

        /// <summary>
        /// Gets or sets the maximum age, in hours, for which an undelivered
        /// message is still eligible for resending.
        /// </summary>
        public int ResendWindowHours { get; set; } = 24;

        /// <summary>
        /// Gets or sets the address (or distribution list) that receives alert
        /// e-mails when messages stay undelivered beyond one check window.
        /// </summary>
        public string AlertEmailAddress { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the address used to create the MailQueueNet gRPC channel.
        /// This value is also used by the client helpers to determine whether the
        /// queue service is local (shared filesystem) or remote (attachments must be uploaded).
        /// </summary>
        /// <value>
        /// An absolute URI such as <c>https://localhost:5001</c>.
        /// </value>
        public string? MailQueueNetServiceChannelAddress { get; set; }

        /// <summary>
        /// Gets or sets the SMTP host used when the client needs to send alert messages.
        /// </summary>
        public string? SmtpHost { get; set; }

        public int? SmtpPort { get; set; }

        public bool? SmtpEnableSsl { get; set; }

        public string? SmtpUsername { get; set; }

        public string? SmtpPassword { get; set; }

        public int DistributedLockTimeoutSeconds { get; set; } = 300;    // 5 min

        public string LockFileName { get; set; } = ".resend.lock";
    }
}