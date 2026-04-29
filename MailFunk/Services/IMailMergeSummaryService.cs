//-----------------------------------------------------------------------
// <copyright file="IMailMergeSummaryService.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Provides access to queue-side mail merge summaries for the dashboard.
    /// </summary>
    public interface IMailMergeSummaryService
    {
        /// <summary>
        /// Fetches active and recent mail merge summaries from the queue service.
        /// </summary>
        /// <param name="take">Maximum number of results to fetch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply containing active and recent merge summaries.</returns>
        Task<ListMailMergesReply> ListAsync(int take, CancellationToken cancellationToken);
    }
}
