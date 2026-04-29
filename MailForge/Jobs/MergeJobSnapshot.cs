//-----------------------------------------------------------------------
// <copyright file="MergeJobSnapshot.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;

    /// <summary>
    /// A concurrency-safe snapshot of the current job state as tracked in-memory.
    /// The durable source of truth is the per-job SQLite database.
    /// </summary>
    public sealed class MergeJobSnapshot
    {
        /// <summary>
        /// Gets or sets the job identifier.
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the client identifier (if provided by dispatcher).
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the template engine value.
        /// </summary>
        public string Engine { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current status.
        /// </summary>
        public MergeJobStatus Status { get; set; } = MergeJobStatus.Pending;

        /// <summary>
        /// Gets or sets the total number of recipients.
        /// </summary>
        public long Total { get; set; }

        /// <summary>
        /// Gets or sets the completed recipient count.
        /// </summary>
        public long Completed { get; set; }

        /// <summary>
        /// Gets or sets the failed recipient count.
        /// </summary>
        public long Failed { get; set; }

        /// <summary>
        /// Gets or sets the last error message captured for the job.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the last update time (UTC).
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the job work root path where this job's state is stored.
        /// </summary>
        public string JobWorkRoot { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the time the job was started (UTC).
        /// </summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>
        /// Gets or sets the time the job finished (UTC).
        /// </summary>
        public DateTime? FinishedUtc { get; set; }

        /// <summary>
        /// Creates a shallow copy for external use.
        /// </summary>
        /// <returns>A copy of the current snapshot.</returns>
        public MergeJobSnapshot Copy()
        {
            return new MergeJobSnapshot
            {
                JobId = this.JobId,
                ClientId = this.ClientId,
                Engine = this.Engine,
                JobWorkRoot = this.JobWorkRoot,
                Status = this.Status,
                Total = this.Total,
                Completed = this.Completed,
                Failed = this.Failed,
                LastError = this.LastError,
                UpdatedUtc = this.UpdatedUtc,
                StartedUtc = this.StartedUtc,
                FinishedUtc = this.FinishedUtc,
            };
        }
    }
}
