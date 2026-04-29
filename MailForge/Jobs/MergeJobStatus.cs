//-----------------------------------------------------------------------
// <copyright file="MergeJobStatus.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    /// <summary>
    /// Represents the lifecycle state of a merge job within a MailForge worker.
    /// </summary>
    public enum MergeJobStatus
    {
        /// <summary>
        /// The job has been accepted but processing has not started.
        /// </summary>
        Pending = 0,

        /// <summary>
        /// The job is currently being processed.
        /// </summary>
        Running = 1,

        /// <summary>
        /// The job has been paused by an operator and will not progress until resumed.
        /// </summary>
        Paused = 2,

        /// <summary>
        /// The job has completed successfully.
        /// </summary>
        Completed = 3,

        /// <summary>
        /// The job has failed.
        /// </summary>
        Failed = 4,

        /// <summary>
        /// The job was cancelled.
        /// </summary>
        Cancelled = 5,
    }
}
