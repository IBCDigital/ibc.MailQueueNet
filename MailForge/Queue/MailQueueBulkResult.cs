//-----------------------------------------------------------------------
// <copyright file="MailQueueBulkResult.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Queue
{
    /// <summary>
    /// Represents the outcome of submitting a bulk batch to MailQueueNet.
    /// </summary>
    public sealed class MailQueueBulkResult
    {
        /// <summary>
        /// Gets or sets the total number of messages in the batch.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Gets or sets the number of messages accepted by MailQueueNet.
        /// </summary>
        public int Accepted { get; set; }

        /// <summary>
        /// Gets or sets the number of messages rejected or failed.
        /// </summary>
        public int Failed { get; set; }
    }
}
