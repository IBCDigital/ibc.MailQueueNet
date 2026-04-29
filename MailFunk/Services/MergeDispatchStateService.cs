//-----------------------------------------------------------------------
// <copyright file="MergeDispatchStateService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Reads merge dispatch state rows from the MailQueueNet service.
    /// </summary>
    public sealed class MergeDispatchStateService : IMergeDispatchStateService
    {
        private readonly MailGrpcService.MailGrpcServiceClient grpcClient;

        /// <summary>
        /// Initialises a new instance of the <see cref="MergeDispatchStateService"/> class.
        /// </summary>
        /// <param name="grpcClient">The queue gRPC client.</param>
        public MergeDispatchStateService(MailGrpcService.MailGrpcServiceClient grpcClient)
        {
            this.grpcClient = grpcClient;
        }

        /// <inheritdoc />
        public async Task<ListMergeDispatchStateReply> ListAsync(int take, CancellationToken cancellationToken)
        {
            var req = new ListMergeDispatchStateRequest
            {
                Take = take,
            };

            return await this.grpcClient.ListMergeDispatchStateAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
