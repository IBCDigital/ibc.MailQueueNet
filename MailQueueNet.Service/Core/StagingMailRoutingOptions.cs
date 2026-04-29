//-----------------------------------------------------------------------
// <copyright file="StagingMailRoutingOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    /// <summary>
    /// Configuration options controlling staging-only mail routing.
    /// </summary>
    public sealed class StagingMailRoutingOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether staging mail routing is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether all mail should be forced to Mailpit only.
        /// </summary>
        public bool ForceMailpitOnly { get; set; }

        /// <summary>
        /// Gets or sets the subject prefix applied to real SMTP deliveries in staging.
        /// </summary>
        public string SubjectPrefix { get; set; } = "[STAGING] ";

        /// <summary>
        /// Gets or sets Mailpit SMTP settings used for the default safe delivery path.
        /// </summary>
        public SmtpDeliveryOptions Mailpit { get; set; } = new SmtpDeliveryOptions();

        /// <summary>
        /// Gets or sets the real SMTP settings used for allow-listed staging deliveries.
        /// </summary>
        public SmtpDeliveryOptions RealSmtp { get; set; } = new SmtpDeliveryOptions();
    }
}
