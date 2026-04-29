//-----------------------------------------------------------------------
// <copyright file="SmtpDeliveryOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    /// <summary>
    /// Represents SMTP delivery settings for a named routing target.
    /// </summary>
    public sealed class SmtpDeliveryOptions
    {
        /// <summary>
        /// Gets or sets the SMTP host name.
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the SMTP port.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether SSL is required.
        /// </summary>
        public bool RequiresSsl { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether SMTP authentication is required.
        /// </summary>
        public bool RequiresAuthentication { get; set; }

        /// <summary>
        /// Gets or sets the SMTP username.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the SMTP password.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the connection timeout in milliseconds.
        /// </summary>
        public int ConnectionTimeout { get; set; } = 100000;
    }
}
