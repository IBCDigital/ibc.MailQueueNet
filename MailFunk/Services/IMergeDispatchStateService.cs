// <copyright file="IMergeDispatchStateService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Provides access to the queue service dispatcher state for mail merge reporting.
    /// </summary>
    public interface IMergeDispatchStateService
    {
        /// <summary>
        /// Lists merge dispatch state rows from the queue service.
        /// </summary>
        /// <param name="take">Maximum number of results to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply containing merge dispatch state rows.</returns>
        Task<ListMergeDispatchStateReply> ListAsync(int take, CancellationToken cancellationToken);
    }
}
