//-----------------------------------------------------------------------
// <copyright file="IMergeBatchStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides persistent append-only storage for mail merge input batches.
    /// </summary>
    public interface IMergeBatchStore
    {
        /// <summary>
        /// Appends JSONL input rows to the specified merge batch.
        /// </summary>
        /// <param name="mergeId">Merge batch identifier.</param>
        /// <param name="jsonLines">JSONL rows to append.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of rows appended.</returns>
        Task<int> AppendAsync(string mergeId, string[] jsonLines, CancellationToken cancellationToken);

        /// <summary>
        /// Appends JSONL input rows for a specific batch.
        /// </summary>
        /// <param name="mergeId">Merge batch identifier.</param>
        /// <param name="templateFileName">Template file name in the MailQueue merge folder.</param>
        /// <param name="batchId">Batch identifier corresponding to the <c>.N.jsonl</c> suffix.</param>
        /// <param name="jsonLines">JSONL rows to append.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of rows appended.</returns>
        Task<int> AppendAsync(string mergeId, string templateFileName, int batchId, string[] jsonLines, CancellationToken cancellationToken);
    }
}
