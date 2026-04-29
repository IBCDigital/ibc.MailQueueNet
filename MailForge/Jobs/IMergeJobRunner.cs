//-----------------------------------------------------------------------
// <copyright file="IMergeJobRunner.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailForge.Grpc;

    /// <summary>
    /// Defines the in-process job runner responsible for accepting and executing merge jobs.
    /// </summary>
    public interface IMergeJobRunner
    {
        /// <summary>
        /// Attempts to start (or resume) a merge job.
        /// </summary>
        /// <param name="request">The gRPC request describing the job inputs.</param>
        /// <param name="cancellationToken">Cancellation token for request-lifetime cancellation.</param>
        /// <returns>A reply indicating whether the job was accepted by this instance.</returns>
        Task<StartMergeJobReply> StartAsync(StartMergeJobRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Attempts to cancel a running job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply indicating whether cancellation was requested.</returns>
        Task<CancelMergeJobReply> CancelAsync(string jobId, CancellationToken cancellationToken);

        /// <summary>
        /// Attempts to pause a running merge job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply indicating whether the pause was requested.</returns>
        Task<PauseMergeJobReply> PauseAsync(string jobId, CancellationToken cancellationToken);

        /// <summary>
        /// Attempts to resume a paused merge job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply indicating whether the resume was requested.</returns>
        Task<ResumeMergeJobReply> ResumeAsync(string jobId, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes a merge job and its stored state.
        /// </summary>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="jobWorkRoot">
        /// Optional job work root containing the per-job database. When omitted, the runner will attempt to
        /// locate the job database under the configured <c>MailForge:JobWorkRoots</c>.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply indicating whether the delete succeeded.</returns>
        Task<DeleteMergeJobReply> DeleteAsync(string mergeId, string? jobWorkRoot, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves the latest known status for a job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="jobWorkRoot">
        /// Optional job work root used to locate the per-job SQLite database when the job is not running.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply suitable for returning from gRPC.</returns>
        Task<GetMergeJobStatusReply> GetStatusAsync(string jobId, string? jobWorkRoot, CancellationToken cancellationToken);
    }
}
