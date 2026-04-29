//-----------------------------------------------------------------------
// <copyright file="MailQueueOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Queue
{
    /// <summary>
    /// Configuration settings used to connect to the MailQueueNet gRPC service.
    /// </summary>
    public sealed class MailQueueOptions
    {
        /// <summary>
        /// Gets or sets the MailQueueNet service address (for example, https://mailqueuenet:5001).
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the client identifier used for shared-secret auth.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the shared secret used for shared-secret auth.
        /// </summary>
        public string SharedSecret { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum number of messages to include in a single bulk request.
        /// </summary>
        public int MaxBatchSize { get; set; } = 50;

        /// <summary>
        /// Gets or sets the number of retry attempts to apply when queuing messages.
        /// </summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the exponential backoff factor used between retries.
        /// </summary>
        public double RetryBackoffFactor { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets a value indicating whether undelivered messages should be written
        /// to disk when retries are exhausted.
        /// </summary>
        public bool EnableDiskResilience { get; set; }

        /// <summary>
        /// Gets or sets the folder where undelivered messages will be written when
        /// <see cref="EnableDiskResilience"/> is enabled.
        /// </summary>
        public string UndeliveredFolder { get; set; } = string.Empty;
    }
}
