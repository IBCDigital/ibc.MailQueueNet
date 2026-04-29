//-----------------------------------------------------------------------
// <copyright file="MailMergeDashboardRow.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Models
{
    using System;

    /// <summary>
    /// Represents a MailMerge summary row for the MailFunk dashboard, combining queue-side merge metadata
    /// with MailForge progress counters.
    /// </summary>
    public sealed record MailMergeDashboardRow
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="MailMergeDashboardRow"/> class.
        /// </summary>
        /// <param name="mergeId">The merge identifier.</param>
        /// <param name="status">The merge status label.</param>
        /// <param name="batchCount">The number of batches for the merge.</param>
        /// <param name="templateFileName">The merge template file name.</param>
        /// <param name="templateFullPath">The merge template full path (server-side).</param>
        /// <param name="completed">The number of rows completed by MailForge.</param>
        /// <param name="total">The total number of rows for the merge.</param>
        /// <param name="failed">The number of rows failed by MailForge.</param>
        public MailMergeDashboardRow(
            string mergeId,
            string status,
            int batchCount,
            string templateFileName,
            string templateFullPath,
            long completed,
            long total,
            long failed)
        {
            this.MergeId = mergeId ?? string.Empty;
            this.Status = status ?? string.Empty;
            this.BatchCount = batchCount;
            this.TemplateFileName = templateFileName ?? string.Empty;
            this.TemplateFullPath = templateFullPath ?? string.Empty;
            this.CompletedRows = completed;
            this.TotalRows = total;
            this.FailedRows = failed;
        }

        /// <summary>
        /// Gets the merge identifier.
        /// </summary>
        public string MergeId { get; }

        /// <summary>
        /// Gets the merge status label.
        /// </summary>
        public string Status { get; }

        /// <summary>
        /// Gets the number of batches for the merge.
        /// </summary>
        public int BatchCount { get; }

        /// <summary>
        /// Gets the merge template file name.
        /// </summary>
        public string TemplateFileName { get; }

        /// <summary>
        /// Gets the merge template full path (server-side).
        /// </summary>
        public string TemplateFullPath { get; }

        /// <summary>
        /// Gets the number of completed rows for the merge.
        /// </summary>
        public long CompletedRows { get; }

        /// <summary>
        /// Gets the total number of rows for the merge.
        /// </summary>
        public long TotalRows { get; }

        /// <summary>
        /// Gets the number of failed rows for the merge.
        /// </summary>
        public long FailedRows { get; }

        /// <summary>
        /// Gets a display string for progress in the format 'completed/total'.
        /// </summary>
        public string ProgressDisplay => this.TotalRows > 0 ? $"{this.CompletedRows}/{this.TotalRows}" : string.Empty;

        /// <summary>
        /// Gets a value indicating whether the row has MailForge progress information.
        /// </summary>
        public bool HasProgress => this.TotalRows > 0;
    }
}
