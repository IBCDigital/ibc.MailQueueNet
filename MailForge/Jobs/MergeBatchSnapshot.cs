//-----------------------------------------------------------------------
// <copyright file="MergeBatchSnapshot.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;

    /// <summary>
    /// Represents a durable snapshot of a single merge batch as tracked in the job database.
    /// </summary>
    public sealed class MergeBatchSnapshot
    {
        /// <summary>
        /// Gets or sets the merge identifier.
        /// </summary>
        public string MergeId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the batch identifier.
        /// </summary>
        public int BatchId { get; set; }

        /// <summary>
        /// Gets or sets the current batch status.
        /// </summary>
        public MergeJobStatus Status { get; set; } = MergeJobStatus.Pending;

        /// <summary>
        /// Gets or sets the total number of rows seen for this batch.
        /// </summary>
        public long TotalRows { get; set; }

        /// <summary>
        /// Gets or sets the completed row count.
        /// </summary>
        public long CompletedRows { get; set; }

        /// <summary>
        /// Gets or sets the failed row count.
        /// </summary>
        public long FailedRows { get; set; }

        /// <summary>
        /// Gets or sets the latest update time (UTC).
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
