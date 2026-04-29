//-----------------------------------------------------------------------
// <copyright file="IMailQueueClient.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Queue
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Defines an abstraction for queuing mail messages to MailQueueNet.
    /// </summary>
    public interface IMailQueueClient
    {
        /// <summary>
        /// Queues a mail message for delivery.
        /// </summary>
        /// <param name="message">The mail message to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> if the message was accepted by MailQueueNet; otherwise <see langword="false"/>.</returns>
        Task<bool> QueueAsync(System.Net.Mail.MailMessage message, CancellationToken cancellationToken);

        /// <summary>
        /// Queues multiple mail messages for delivery in a single request.
        /// </summary>
        /// <param name="messages">The mail messages to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The bulk reply indicating totals and failures.</returns>
        Task<MailQueueBulkResult> QueueBulkAsync(IReadOnlyList<System.Net.Mail.MailMessage> messages, CancellationToken cancellationToken);

        /// <summary>
        /// Acknowledges that a merge batch has been ingested/processed so the queue service can delete the
        /// corresponding <c>*.jsonl</c> batch file.
        /// </summary>
        /// <param name="mergeId">The merge identifier.</param>
        /// <param name="templateFileName">The template file name in the merge queue folder.</param>
        /// <param name="batchId">The batch identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> if the queue successfully deleted the batch file; otherwise <see langword="false"/>.</returns>
        Task<bool> AckMergeBatchAsync(string mergeId, string templateFileName, int batchId, CancellationToken cancellationToken);
    }
}
