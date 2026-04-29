//-----------------------------------------------------------------------
// <copyright file="MailQueueGrpcClient.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Queue
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Security;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Net.Client;
    using MailQueueNet.Grpc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Queues mail messages by calling the MailQueueNet gRPC API.
    /// </summary>
    public sealed class MailQueueGrpcClient : IMailQueueClient
    {
        private readonly ILogger<MailQueueGrpcClient> logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly MailQueueOptions options;

        private MailGrpcService.MailGrpcServiceClient? client;
        private MailGrpcServiceClientWithRetry? retriableClient;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailQueueGrpcClient"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="loggerFactory">Logger factory instance.</param>
        /// <param name="options">MailQueue client options.</param>
        public MailQueueGrpcClient(ILogger<MailQueueGrpcClient> logger, ILoggerFactory loggerFactory, IOptions<MailQueueOptions> options)
        {
            this.logger = logger;
            this.loggerFactory = loggerFactory;
            this.options = options?.Value ?? new MailQueueOptions();
        }

        /// <inheritdoc/>
        public async Task<bool> QueueAsync(System.Net.Mail.MailMessage message, CancellationToken cancellationToken)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrWhiteSpace(this.options.Address))
            {
                this.logger.LogWarning("MailQueue is not configured (MailQueue:Address is empty). Message will not be queued.");
                return false;
            }

            _ = this.EnsureClient();
            var retry = this.EnsureRetryClient();

            try
            {
                MailMessageReply reply;
                if (this.options.EnableDiskResilience)
                {
                    reply = await retry.QueueMailWithRetryAndResilienceAsync(message, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    reply = await retry.QueueMailWithRetryAsync(message, cancellationToken).ConfigureAwait(false);
                }

                return reply?.Success ?? false;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to queue mail via MailQueueNet");
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<MailQueueBulkResult> QueueBulkAsync(IReadOnlyList<System.Net.Mail.MailMessage> messages, CancellationToken cancellationToken)
        {
            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            if (messages.Count == 0)
            {
                return new MailQueueBulkResult
                {
                    Total = 0,
                    Accepted = 0,
                    Failed = 0,
                };
            }

            if (string.IsNullOrWhiteSpace(this.options.Address))
            {
                this.logger.LogWarning("MailQueue is not configured (MailQueue:Address is empty). Batch will not be queued.");
                return new MailQueueBulkResult
                {
                    Total = messages.Count,
                    Accepted = 0,
                    Failed = messages.Count,
                };
            }

            _ = this.EnsureClient();

            var retry = this.EnsureRetryClient();

            try
            {
                var reply = await retry.QueueMailBulkWithRetryAsync(messages, cancellationToken).ConfigureAwait(false);
                return new MailQueueBulkResult
                {
                    Total = reply?.Total ?? messages.Count,
                    Accepted = reply?.Accepted ?? 0,
                    Failed = reply?.Failed ?? messages.Count,
                };
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to queue bulk mail via MailQueueNet");
                return new MailQueueBulkResult
                {
                    Total = messages.Count,
                    Accepted = 0,
                    Failed = messages.Count,
                };
            }
        }

        /// <inheritdoc/>
        public async Task<bool> AckMergeBatchAsync(string mergeId, string templateFileName, int batchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                throw new ArgumentException("mergeId is required", nameof(mergeId));
            }

            if (string.IsNullOrWhiteSpace(templateFileName))
            {
                throw new ArgumentException("templateFileName is required", nameof(templateFileName));
            }

            if (batchId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(batchId), "batchId must be >= 0");
            }

            if (string.IsNullOrWhiteSpace(this.options.Address))
            {
                this.logger.LogWarning("MailQueue is not configured (MailQueue:Address is empty). Merge batch acknowledgement will be skipped.");
                return false;
            }

            _ = this.EnsureClient();

            try
            {
                var reply = await this.client!
                    .AckMailMergeBatchAsync(new AckMailMergeBatchRequest
                    {
                        MergeId = mergeId,
                        TemplateFileName = templateFileName,
                        BatchId = batchId,
                    }, cancellationToken: cancellationToken)
                    .ResponseAsync
                    .ConfigureAwait(false);

                if (reply == null)
                {
                    return false;
                }

                if (!reply.Success)
                {
                    this.logger.LogWarning(
                        "AckMailMergeBatch returned unsuccessful (MergeId={MergeId}; Template={Template}; BatchId={BatchId}; Message={Message})",
                        mergeId,
                        templateFileName,
                        batchId,
                        reply.Message);
                }

                return reply.Success;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "AckMailMergeBatch failed (MergeId={MergeId}; Template={Template}; BatchId={BatchId})", mergeId, templateFileName, batchId);
                return false;
            }
        }

        private MailGrpcServiceClientWithRetry EnsureRetryClient()
        {
            if (this.retriableClient != null)
            {
                return this.retriableClient;
            }

            var undelivered = this.options.UndeliveredFolder;
            if (string.IsNullOrWhiteSpace(undelivered))
            {
                undelivered = Path.Combine(AppContext.BaseDirectory, "undelivered");
            }

            var cfg = new MailClientConfiguration
            {
                EnableDiskResilience = this.options.EnableDiskResilience,
                UndeliveredFolder = undelivered,
                LockFileName = "undelivered.lock",
                RetryCount = this.options.RetryCount > 0 ? this.options.RetryCount : 3,
                RetryBackoffFactor = this.options.RetryBackoffFactor > 0 ? this.options.RetryBackoffFactor : 2.0,
                UnsentCheckIntervalMinutes = 5,
                ResendWindowHours = 24,
                DistributedLockTimeoutSeconds = 30,
                AlertEmailAddress = string.Empty,
                SmtpHost = string.Empty,
            };

            var retryLogger = this.loggerFactory.CreateLogger<MailGrpcServiceClientWithRetry>();
            this.retriableClient = new MailGrpcServiceClientWithRetry(this.client!, cfg, retryLogger);
            return this.retriableClient;
        }

        private MailGrpcService.MailGrpcServiceClient EnsureClient()
        {
            if (this.client != null)
            {
                return this.client;
            }

            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = 20,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };

            if (this.options.Address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
            }

            var channel = GrpcChannel.ForAddress(this.options.Address, new GrpcChannelOptions
            {
                HttpHandler = handler,
            });

            if (!string.IsNullOrWhiteSpace(this.options.ClientId) && !string.IsNullOrWhiteSpace(this.options.SharedSecret))
            {
                MailGrpcService.MailGrpcServiceClient.ConfigureClientAuth(this.options.ClientId, this.options.SharedSecret);
            }

            this.client = new MailGrpcService.MailGrpcServiceClient(channel);
            return this.client;
        }
    }
}
