//-----------------------------------------------------------------------
// <copyright file="MailMergeSummaryService.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Reads mail merge summaries from the MailQueueNet service.
    /// </summary>
    public sealed class MailMergeSummaryService : IMailMergeSummaryService
    {
        private readonly MailGrpcService.MailGrpcServiceClient grpcClient;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailMergeSummaryService"/> class.
        /// </summary>
        /// <param name="grpcClient">The queue gRPC client.</param>
        public MailMergeSummaryService(MailGrpcService.MailGrpcServiceClient grpcClient)
        {
            this.grpcClient = grpcClient;
        }

        /// <inheritdoc />
        public async Task<ListMailMergesReply> ListAsync(int take, CancellationToken cancellationToken)
        {
            var req = new ListMailMergesRequest
            {
                Take = take,
            };

            return await this.grpcClient.ListMailMergesAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
