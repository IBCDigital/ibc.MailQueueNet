//-----------------------------------------------------------------------
// <copyright file="AllowedTestRecipientsService.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Grpc;
    using Microsoft.Extensions.Configuration;

    /// <summary>
    /// Provides staging-only access to the authenticated client's allow-listed real SMTP recipients.
    /// </summary>
    public sealed class AllowedTestRecipientsService
    {
        private readonly MailGrpcService.MailGrpcServiceClient grpcClient;
        private readonly IConfiguration configuration;

        /// <summary>
        /// Initialises a new instance of the <see cref="AllowedTestRecipientsService"/> class.
        /// </summary>
        /// <param name="grpcClient">The queue service gRPC client.</param>
        /// <param name="configuration">Application configuration.</param>
        public AllowedTestRecipientsService(MailGrpcService.MailGrpcServiceClient grpcClient, IConfiguration configuration)
        {
            this.grpcClient = grpcClient;
            this.configuration = configuration;
        }

        /// <summary>
        /// Lists allow-listed recipient email addresses for the authenticated client.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The allow-listed email addresses.</returns>
        public async Task<IReadOnlyList<string>> ListAsync(string clientId, CancellationToken cancellationToken)
        {
            this.EnsureStaging();
            var reply = await this.grpcClient.ListAllowedTestRecipientsAsync(new ListAllowedTestRecipientsRequest
            {
                ClientId = clientId ?? string.Empty,
            }, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            return reply == null ? Array.Empty<string>() : reply.EmailAddresses.ToArray();
        }

        /// <summary>
        /// Adds an allow-listed recipient email address for the authenticated client.
        /// </summary>
        /// <param name="emailAddress">Email address to add.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The server response.</returns>
        public Task<AddAllowedTestRecipientReply> AddAsync(string clientId, string emailAddress, CancellationToken cancellationToken)
        {
            this.EnsureStaging();
            return this.grpcClient.AddAllowedTestRecipientAsync(new AddAllowedTestRecipientRequest
            {
                EmailAddress = emailAddress ?? string.Empty,
                ClientId = clientId ?? string.Empty,
            }, cancellationToken: cancellationToken).ResponseAsync;
        }

        /// <summary>
        /// Deletes an allow-listed recipient email address for the authenticated client.
        /// </summary>
        /// <param name="emailAddress">Email address to delete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The server response.</returns>
        public Task<DeleteAllowedTestRecipientReply> DeleteAsync(string clientId, string emailAddress, CancellationToken cancellationToken)
        {
            this.EnsureStaging();
            return this.grpcClient.DeleteAllowedTestRecipientAsync(new DeleteAllowedTestRecipientRequest
            {
                EmailAddress = emailAddress ?? string.Empty,
                ClientId = clientId ?? string.Empty,
            }, cancellationToken: cancellationToken).ResponseAsync;
        }

        /// <summary>
        /// Gets a value indicating whether the current MailFunk environment is staging.
        /// </summary>
        public bool IsStagingEnabled => string.Equals(this.configuration["ASPNETCORE_ENVIRONMENT"], "Staging", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(this.configuration["DOTNET_ENVIRONMENT"], "Staging", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(this.configuration["Environment"], "Staging", StringComparison.OrdinalIgnoreCase);

        private void EnsureStaging()
        {
            if (!this.IsStagingEnabled)
            {
                throw new InvalidOperationException("Allow-listed test recipients are only available in Staging.");
            }
        }
    }
}
