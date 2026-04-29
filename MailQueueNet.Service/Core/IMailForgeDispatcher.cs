//-----------------------------------------------------------------------
// <copyright file="IMailForgeDispatcher.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailForge.Grpc;

    /// <summary>
    /// Defines the operations for dispatching merge jobs to MailForge workers.
    /// </summary>
    public interface IMailForgeDispatcher
    {
        /// <summary>
        /// Attempts to start a merge job on the currently leased worker.
        /// </summary>
        /// <param name="request">The MailForge start request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The worker reply.</returns>
        Task<StartMergeJobReply> StartMergeJobAsync(StartMergeJobRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Attempts to cancel a merge job on the currently leased worker.
        /// </summary>
        /// <param name="jobId">The job identifier to cancel.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The worker reply.</returns>
        Task<CancelMergeJobReply> CancelMergeJobAsync(string jobId, CancellationToken cancellationToken);

        /// <summary>
        /// Appends a batch of JSONL rows to the merge input stream on the currently leased worker.
        /// </summary>
        /// <param name="request">The append request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The worker reply.</returns>
        Task<AppendMergeBatchReply> AppendMergeBatchAsync(AppendMergeBatchRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Gets a snapshot of the current lease.
        /// </summary>
        /// <returns>The current lease, or <see langword="null"/> if no lease is held.</returns>
        DispatcherLease? GetLeaseSnapshot();

        /// <summary>
        /// Refreshes the dispatcher settings.
        /// </summary>
        void RefreshSettings();

        /// <summary>
        /// Gets the status of a merge job from the currently leased worker.
        /// </summary>
        /// <param name="request">Status request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Status reply.</returns>
        Task<GetMergeJobStatusReply> GetMergeJobStatusAsync(GetMergeJobStatusRequest request, CancellationToken cancellationToken);
    }
}
