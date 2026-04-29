//-----------------------------------------------------------------------
// <copyright file="MergeDispatchStateRow.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents a single merge dispatch state row as persisted in the dispatcher
    /// state database.
    /// </summary>
    public sealed class MergeDispatchStateRow
    {
        /// <summary>
        /// Gets or sets the merge identifier.
        /// </summary>
        public string MergeId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the template path persisted when the merge was first tracked.
        /// </summary>
        public string TemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the worker address that accepted the job (if any).
        /// </summary>
        public string WorkerAddress { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the fence token used when the job was dispatched.
        /// </summary>
        public long FenceToken { get; set; }

        /// <summary>
        /// Gets or sets the dispatch status as a friendly string.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the last error / message recorded against the job.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the last updated timestamp (UTC).
        /// </summary>
        public DateTimeOffset UpdatedUtc { get; set; }

        /// <summary>
        /// Gets or sets the completion timestamp (UTC) if completed.
        /// </summary>
        public DateTimeOffset? CompletedUtc { get; set; }
    }
}
