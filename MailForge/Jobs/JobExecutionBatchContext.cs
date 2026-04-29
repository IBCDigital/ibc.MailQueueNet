//-----------------------------------------------------------------------
// <copyright file="JobExecutionBatchContext.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    /// <summary>
    /// Tracks the currently processed batch within a merge job execution.
    /// </summary>
    public sealed class JobExecutionBatchContext
    {
        /// <summary>
        /// Gets or sets the active batch identifier.
        /// </summary>
        public int BatchId { get; set; } = -1;

        /// <summary>
        /// Gets or sets the active template file name used for this batch.
        /// </summary>
        public string TemplateFileName { get; set; } = string.Empty;
    }
}
