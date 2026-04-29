//-----------------------------------------------------------------------
// <copyright file="IMailMergeQueueClient.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Grpc
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a convenience interface for queueing mail merge batches.
    /// </summary>
    public interface IMailMergeQueueClient
    {
        /// <summary>
        /// Queues an item into a mail merge batch.
        /// </summary>
        /// <param name="message">The mail message to queue.</param>
        /// <param name="mergeId">Optional merge batch identifier to append to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The queue reply, including the effective merge id.</returns>
        Task<QueueMailMergeReply> QueueMailMergeAsync(System.Net.Mail.MailMessage message, string? mergeId = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queues an item into a mail merge batch with per-message settings.
        /// </summary>
        /// <param name="message">The mail message to queue.</param>
        /// <param name="settings">Per-message mail settings.</param>
        /// <param name="mergeId">Optional merge batch identifier to append to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The queue reply, including the effective merge id.</returns>
        Task<QueueMailMergeReply> QueueMailMergeWithSettingsAsync(System.Net.Mail.MailMessage message, MailSettings settings, string? mergeId = null, CancellationToken cancellationToken = default);
    }
}
