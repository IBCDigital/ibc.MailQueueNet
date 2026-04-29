//-----------------------------------------------------------------------
// <copyright file="MailMergeQueueClient.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Grpc
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Implements <see cref="IMailMergeQueueClient"/> using the generated
    /// <see cref="MailGrpcService.MailGrpcServiceClient"/>.
    /// </summary>
    public sealed class MailMergeQueueClient : IMailMergeQueueClient
    {
        private readonly MailGrpcService.MailGrpcServiceClient client;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailMergeQueueClient"/> class.
        /// </summary>
        /// <param name="client">Underlying gRPC client.</param>
        public MailMergeQueueClient(MailGrpcService.MailGrpcServiceClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc/>
        public async Task<QueueMailMergeReply> QueueMailMergeAsync(System.Net.Mail.MailMessage message, string? mergeId = null, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var req = new QueueMailMergeRequest
            {
                MergeId = mergeId ?? string.Empty,
                Message = MailQueueNet.Grpc.MailMessage.FromMessage(message),
            };

            return await this.client.QueueMailMergeAsync(req, headers: null, deadline: null, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<QueueMailMergeReply> QueueMailMergeWithSettingsAsync(System.Net.Mail.MailMessage message, MailSettings settings, string? mergeId = null, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var req = new QueueMailMergeWithSettingsRequest
            {
                MergeId = mergeId ?? string.Empty,
                Mail = new MailMessageWithSettings
                {
                    Message = MailQueueNet.Grpc.MailMessage.FromMessage(message),
                    Settings = settings,
                },
            };

            return await this.client.QueueMailMergeWithSettingsAsync(req, headers: null, deadline: null, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        }
    }
}
