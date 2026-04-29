// <copyright file="MailGrpcServiceClientWithRetry.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
//
//  Derived from “MailQueueNet” by Daniel Cohen Gindi
//  (https://github.com/danielgindi/MailQueueNet).
//
//  Original portions:
//    © 2014 Daniel Cohen Gindi (danielgindi@gmail.com)
//    Licensed under the MIT Licence.
//  Modifications and additions:
//    © 2025 IBC Digital Pty Ltd
//    Distributed under the same MIT Licence.
//
//  The above notice and this permission notice shall be included in
//  all copies or substantial portions of this file.
// </copyright>
// <license>
// MIT Licence – see the repository root LICENCE file for full text.
// </license>

namespace MailQueueNet.Grpc
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Mail;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Common.FileExtensions;
    using Microsoft.Extensions.Logging;
    using Polly;
    using Polly.Retry;

    /// <summary>
    /// A resilient wrapper around <see cref="MailGrpcService.MailGrpcServiceClient"/>.
    /// Features:
    /// <list type="bullet">
    ///   <item>Polly retry with exponential back-off.</item>
    ///   <item>Disk resilience (failed items persisted to <c>UndeliveredFolder</c>).</item>
    ///   <item>Low-priority background resend worker.</item>
    ///   <item>Single-process gate + shared lock-file to avoid duplicate processing in
    ///         multi-node clusters.</item>
    /// </list>
    /// </summary>
    public class MailGrpcServiceClientWithRetry : IAsyncDisposable
    {
        private readonly MailGrpcService.MailGrpcServiceClient client;
        private readonly ILogger<MailGrpcServiceClientWithRetry> log;
        private readonly SemaphoreSlim singleProcessGate = new SemaphoreSlim(1, 1); // 1-thread gate
        private readonly ConcurrentDictionary<Guid, InFlightMailMessage> inFlightMessages = new ConcurrentDictionary<Guid, InFlightMailMessage>();
        private readonly CancellationTokenSource shutdownTokenSource = new CancellationTokenSource();
        private bool stopped;
        private AsyncRetryPolicy? retryPolicy;
        private string? undeliveredFolder;
        private bool enableDiskResilience;
        private MailClientConfiguration? config;
        private Timer? resendTimer;
        private FileResendLock? fileLock;
        private DateTime lockAcquiredUtc;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="MailGrpcServiceClientWithRetry"/> class.
        /// </summary>
        /// <param name="client">Underlying gRPC client.</param>
        /// <param name="cfg">Configuration.</param>
        /// <param name="log">Logger injected by DI.</param>
        public MailGrpcServiceClientWithRetry(
            MailGrpcService.MailGrpcServiceClient client,
            MailClientConfiguration cfg,
            ILogger<MailGrpcServiceClientWithRetry> log)
        {
            this.client = client;
            this.log = log;
            this.Configure(cfg);
        }

        /// <summary>
        /// Re-configures retry policy and disk-resilience settings at runtime.
        /// </summary>
        /// <param name="config">The new configuration object to apply.</param>
        public void Configure(MailClientConfiguration config)
        {
            this.ThrowIfStopped();
            this.config = config;
            this.enableDiskResilience = config.EnableDiskResilience;
            this.undeliveredFolder = config.UndeliveredFolder;

            // Build a Polly retry policy that backs off exponentially.
            this.retryPolicy = Policy
                .Handle<RpcException>()
                .WaitAndRetryAsync(
                    retryCount: config.RetryCount,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(config.RetryBackoffFactor, retryAttempt)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        Console.WriteLine($"Retry {retryCount} after {timeSpan.TotalSeconds}s due to {exception.Message}");
                    });

            // Ensure folder exists.
            if (config.EnableDiskResilience && !Directory.Exists(config.UndeliveredFolder))
            {
                Directory.CreateDirectory(config.UndeliveredFolder);
            }

            // shared lock file.
            this.fileLock = new FileResendLock(
                config.UndeliveredFolder,
                config.LockFileName);

            // (Re)start timer.
            this.resendTimer?.Dispose();
            this.resendTimer = new Timer(
                _ => _ = this.RunResendInBackgroundAsync(),
                null,
                TimeSpan.Zero,                                    // run at startup
                TimeSpan.FromMinutes(config.UnsentCheckIntervalMinutes));
        }

        /// <summary>
        /// Enables or disables disk resilience and sets the folder where
        /// undelivered messages are written.
        /// </summary>
        /// <param name="undeliveredFolder">
        /// Absolute path of the folder used to persist failed messages.
        /// </param>
        /// <param name="enableDiskResilience">
        /// <see langword="true"/> to enable the feature; otherwise
        /// <see langword="false"/>.
        /// </param>
        public void ConfigureDiskResilience(string undeliveredFolder, bool enableDiskResilience)
        {
            this.undeliveredFolder = undeliveredFolder;
            this.enableDiskResilience = enableDiskResilience;

            if (this.enableDiskResilience && !Directory.Exists(this.undeliveredFolder))
            {
                Directory.CreateDirectory(this.undeliveredFolder);
            }
        }

        /// <summary>
        /// Queues a message with retry logic applied.
        /// </summary>
        /// <param name="message">The SMTP-style message to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// A task that completes with the <see cref="MailMessageReply"/> returned
        /// by the server.
        /// </returns>
        public async Task<MailMessageReply> QueueMailWithRetryAsync(System.Net.Mail.MailMessage message, CancellationToken cancellationToken = default)
        {
            this.ThrowIfStopped();
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdownTokenSource.Token);

            if (this.retryPolicy == null)
            {
                return await this.client.QueueMailReplyAsync(message, cancellationToken: linkedTokenSource.Token).ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                linkedTokenSource.Token.ThrowIfCancellationRequested();
                return await this.client.QueueMailReplyAsync(message, cancellationToken: linkedTokenSource.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues a message together with per-message
        /// <see cref="MailSettings"/>, applying retry logic.
        /// </summary>
        /// <param name="message">The SMTP-style message to queue.</param>
        /// <param name="settings">Custom settings applied to this message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The server reply.</returns>
        public async Task<MailMessageReply> QueueMailWithSettingsWithRetryAsync(System.Net.Mail.MailMessage message, MailSettings settings, CancellationToken cancellationToken = default)
        {
            if (this.retryPolicy == null)
            {
                return await this.client.QueueMailWithSettingsReplyAsync(message, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                return await this.client.QueueMailWithSettingsReplyAsync(message, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues a message with retry logic and, on permanent failure, writes
        /// the message to the configured <see cref="undeliveredFolder"/>.
        /// </summary>
        /// <param name="message">The SMTP-style message to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The server reply if the send eventually succeeds.</returns>
        /// <exception cref="Exception">
        /// Re-throws the last exception after persisting the message to disk.
        /// </exception>
        public async Task<MailMessageReply> QueueMailWithRetryAndResilienceAsync(System.Net.Mail.MailMessage message, CancellationToken cancellationToken = default)
        {
            var tracking = this.TrackInFlight(message);

            try
            {
                var reply = await this.QueueMailWithRetryAsync(message, cancellationToken).ConfigureAwait(false);
                this.MarkInFlightCompleted(tracking);
                return reply;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send email: {ex.Message}");
                this.PersistInFlight(tracking);
                throw;
            }
            finally
            {
                this.UntrackInFlight(tracking);
            }
        }

        /// <summary>
        /// Stops retry activity and persists any currently in-flight resilient messages to the undelivered folder.
        /// </summary>
        /// <param name="persistInFlight">True to persist messages that have not completed successfully.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when stop handling has finished.</returns>
        public Task StopAsync(bool persistInFlight = true, CancellationToken cancellationToken = default)
        {
            this.stopped = true;
            this.shutdownTokenSource.Cancel();
            this.resendTimer?.Dispose();
            this.resendTimer = null;

            if (persistInFlight)
            {
                return this.FlushInFlightToUndeliveredFolderAsync(cancellationToken);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Persists all currently in-flight resilient messages that have not completed successfully.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when in-flight messages have been flushed.</returns>
        public Task FlushInFlightToUndeliveredFolderAsync(CancellationToken cancellationToken = default)
        {
            foreach (var item in this.inFlightMessages.Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.PersistInFlight(item);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await this.StopAsync(persistInFlight: true, CancellationToken.None).ConfigureAwait(false);
            this.shutdownTokenSource.Dispose();
            this.singleProcessGate.Dispose();
            this.fileLock?.Dispose();
        }

        /// <summary>
        /// Writes an undelivered message to disk when disk resilience is enabled.
        /// </summary>
        /// <param name="message">The message to persist.</param>
        private void SaveToUndeliveredFolder(System.Net.Mail.MailMessage message)
        {
            if (!this.enableDiskResilience || string.IsNullOrEmpty(this.undeliveredFolder))
            {
                return;
            }

            var fileName = Path.Combine(this.undeliveredFolder, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid()}.mail");

            FileUtils.WriteMailToFile(MailMessage.FromMessage(message), fileName);

            Console.WriteLine($"Saved undelivered email to {fileName}");
        }

        private InFlightMailMessage TrackInFlight(System.Net.Mail.MailMessage message)
        {
            this.ThrowIfStopped();

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var trackedMessage = new InFlightMailMessage(Guid.NewGuid(), message);
            this.inFlightMessages[trackedMessage.Id] = trackedMessage;
            return trackedMessage;
        }

        private void MarkInFlightCompleted(InFlightMailMessage trackedMessage)
        {
            if (trackedMessage == null)
            {
                return;
            }

            lock (trackedMessage.SyncRoot)
            {
                trackedMessage.MarkCompleted();
            }
        }

        private void PersistInFlight(InFlightMailMessage trackedMessage)
        {
            if (trackedMessage == null)
            {
                return;
            }

            lock (trackedMessage.SyncRoot)
            {
                if (trackedMessage.IsCompleted || trackedMessage.IsPersisted)
                {
                    return;
                }

                this.SaveToUndeliveredFolder(trackedMessage.Message);
                trackedMessage.MarkPersisted();
            }
        }

        private void UntrackInFlight(InFlightMailMessage trackedMessage)
        {
            if (trackedMessage == null)
            {
                return;
            }

            _ = this.inFlightMessages.TryRemove(trackedMessage.Id, out _);
        }

        private void ThrowIfStopped()
        {
            if (this.stopped)
            {
                throw new ObjectDisposedException(nameof(MailGrpcServiceClientWithRetry));
            }
        }

        private async Task RunResendInBackgroundAsync()
        {
            if (this.stopped || this.shutdownTokenSource.IsCancellationRequested)
            {
                return;
            }

            await Task.Factory.StartNew(
                this.ProcessUndeliveredAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private async Task ProcessUndeliveredAsync()
        {
            // give the worker low CPU priority
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            if (!await this.singleProcessGate.WaitAsync(0))
            {
                return;
            }

            try
            {
                // ---- cluster lock (lock-file) ----
                if (!this.fileLock!.TryAcquire())
                {
                    this.log.LogDebug("Another node owns resend lock");
                    return;
                }

                this.lockAcquiredUtc = DateTime.UtcNow;
                await this.ResendLoopAsync();
            }
            finally
            {
                this.fileLock?.Dispose();
                this.singleProcessGate.Release();
            }
        }

        private async Task ResendLoopAsync()
        {
            if (this.config != null && this.client != null)
            {
                if (!this.config.EnableDiskResilience)
                {
                    return;
                }

                string[] files = Directory.GetFiles(this.config.UndeliveredFolder, "*.mail")
                                            .OrderBy(f => f)
                                            .ToArray();
                DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-this.config.ResendWindowHours);

                int failedCount = 0;

                foreach (string file in files)
                {
                    if (this.stopped || this.shutdownTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    // ---- still have cluster lock? ----
                    if (!this.StillOwnLock())
                    {
                        this.log.LogWarning("Lock lost or timed-out — aborting resend loop");
                        break;
                    }

                    try
                    {
                        if (File.GetCreationTimeUtc(file) < cutoff)
                        {
                            failedCount++;
                            continue; // too old — leave for alert
                        }

                        Grpc.MailMessage m = FileUtils.ReadMailNoSettingsFromFile(file);

                        if (this.retryPolicy == null)
                        {
                            _ = await this.client.QueueMailMessageReplyAsync(m).ConfigureAwait(false);
                        }
                        else
                        {
                            await this.retryPolicy.ExecuteAsync(async () =>
                            {
                                _ = await this.client.QueueMailMessageReplyAsync(m).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        }

                        File.Delete(file);
                        this.log.LogInformation("Resent + deleted {File}", file);
                    }
                    catch (Exception ex)
                    {
                        this.log.LogWarning(ex, "Resend failed for {File}", file);
                        failedCount++;
                    }
                }

                if (failedCount > 0)
                {
                    await this.SendAlertAsync(failedCount);
                }
            }
        }

        private bool StillOwnLock()
        {
            bool inTime = (DateTime.UtcNow - this.lockAcquiredUtc)
                          .TotalSeconds < this.config?.DistributedLockTimeoutSeconds;
            return inTime && this.fileLock!.StillHeld();
        }

        private async Task SendAlertAsync(int failedCount)
        {
            if (this.config == null || string.IsNullOrWhiteSpace(this.config.AlertEmailAddress) || this.config.SmtpPort == null)
            {
                return;
            }

            using var smtp = new SmtpClient(this.config.SmtpHost, this.config.SmtpPort.Value)
            {
                EnableSsl = this.config.SmtpEnableSsl ?? false,
                Credentials = new System.Net.NetworkCredential(
                    this.config.SmtpUsername,
                    this.config.SmtpPassword),
            };

            var mail = new System.Net.Mail.MailMessage()
            {
                From = new System.Net.Mail.MailAddress(this.config.AlertEmailAddress, "Mail Queue Error"),
                Subject = "[MailQueue] Undelivered messages alert",
                Body = $"{failedCount} message(s) remain unsent for >{this.config.UnsentCheckIntervalMinutes} min",
            };
            mail.To.Add(new System.Net.Mail.MailAddress(this.config.AlertEmailAddress));

            await smtp.SendMailAsync(mail);
        }

        /// <summary>
        /// Queues multiple messages in a single request with retry logic applied.
        /// </summary>
        /// <param name="messages">The messages to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply containing totals and failures.</returns>
        public async Task<QueueMailBulkReply> QueueMailBulkWithRetryAsync(IEnumerable<System.Net.Mail.MailMessage> messages, CancellationToken cancellationToken = default)
        {
            this.ThrowIfStopped();

            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdownTokenSource.Token);

            if (this.retryPolicy == null)
            {
                return await this.client.QueueMailBulkReplyAsync(messages, cancellationToken: linkedTokenSource.Token).ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                linkedTokenSource.Token.ThrowIfCancellationRequested();
                return await this.client.QueueMailBulkReplyAsync(messages, cancellationToken: linkedTokenSource.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues multiple messages in a single request with retry logic applied and,
        /// on permanent failure, persists each message to the undelivered folder when enabled.
        /// </summary>
        /// <param name="messages">The messages to queue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A reply containing totals and failures.</returns>
        public async Task<QueueMailBulkReply> QueueMailBulkWithRetryAndResilienceAsync(IEnumerable<System.Net.Mail.MailMessage> messages, CancellationToken cancellationToken = default)
        {
            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            var snapshot = messages as System.Net.Mail.MailMessage[] ?? messages.ToArray();
            var trackedMessages = snapshot.Select(this.TrackInFlight).ToArray();

            try
            {
                var reply = await this.QueueMailBulkWithRetryAsync(snapshot, cancellationToken).ConfigureAwait(false);
                foreach (var trackedMessage in trackedMessages)
                {
                    this.MarkInFlightCompleted(trackedMessage);
                }

                return reply;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send bulk email batch: {ex.Message}");

                foreach (var trackedMessage in trackedMessages)
                {
                    try
                    {
                        this.PersistInFlight(trackedMessage);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
            finally
            {
                foreach (var trackedMessage in trackedMessages)
                {
                    this.UntrackInFlight(trackedMessage);
                }
            }
        }

        /// <summary>
        /// Queues a mail merge item with retry logic applied.
        /// </summary>
        /// <param name="message">The SMTP-style message to queue.</param>
        /// <param name="mergeId">Optional merge id to append to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The reply containing the effective merge id.</returns>
        public async Task<QueueMailMergeReply> QueueMailMergeWithRetryAsync(System.Net.Mail.MailMessage message, string? mergeId = null, CancellationToken cancellationToken = default)
        {
            if (this.retryPolicy == null)
            {
                var req = new QueueMailMergeRequest
                {
                    MergeId = mergeId ?? string.Empty,
                    Message = MailMessage.FromMessage(message),
                };

                return await this.client.QueueMailMergeRequestReplyAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                var req = new QueueMailMergeRequest
                {
                    MergeId = mergeId ?? string.Empty,
                    Message = MailMessage.FromMessage(message),
                };

                return await this.client.QueueMailMergeRequestReplyAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues a mail merge item with per-message settings and retry logic applied.
        /// </summary>
        /// <param name="message">The SMTP-style message to queue.</param>
        /// <param name="settings">Per-message mail settings.</param>
        /// <param name="mergeId">Optional merge id to append to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The reply containing the effective merge id.</returns>
        public async Task<QueueMailMergeReply> QueueMailMergeWithSettingsWithRetryAsync(System.Net.Mail.MailMessage message, MailSettings settings, string? mergeId = null, CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (this.retryPolicy == null)
            {
                var req = new QueueMailMergeWithSettingsRequest
                {
                    MergeId = mergeId ?? string.Empty,
                    Mail = new MailMessageWithSettings
                    {
                        Message = MailMessage.FromMessage(message),
                        Settings = settings,
                    },
                };

                return await this.client.QueueMailMergeWithSettingsRequestReplyAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                var req = new QueueMailMergeWithSettingsRequest
                {
                    MergeId = mergeId ?? string.Empty,
                    Mail = new MailMessageWithSettings
                    {
                        Message = MailMessage.FromMessage(message),
                        Settings = settings,
                    },
                };

                return await this.client.QueueMailMergeWithSettingsRequestReplyAsync(req, cancellationToken: cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists a single page of attachments using server-side cursor paging.
        /// </summary>
        /// <param name="request">The underlying list request to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A page containing items and an optional next-page token.</returns>
        public async Task<AttachmentPage> ListAttachmentsPageAsync(ListAttachmentsRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var reply = await this.ExecuteWithRetryAsync(
                () => this.client.ListAttachmentsAsync(request, cancellationToken: cancellationToken).ResponseAsync,
                cancellationToken).ConfigureAwait(false);

            return new AttachmentPage
            {
                Total = reply?.Total ?? 0,
                Items = reply?.Items?.ToArray() ?? Array.Empty<AttachmentListItem>(),
                NextPageToken = reply?.NextPageToken ?? string.Empty,
            };
        }

        /// <summary>
        /// Lists a single page of attachments for one of the common "views" (orphans / large).
        /// </summary>
        /// <param name="kind">The view kind.</param>
        /// <param name="options">Options controlling filters, sorting, and page-size.</param>
        /// <param name="pageToken">Optional cursor token returned by a prior page.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A page containing items and an optional next-page token.</returns>
        public Task<AttachmentPage> ListAttachmentViewPageAsync(AttachmentViewKind kind, AttachmentViewOptions? options = null, string? pageToken = null, CancellationToken cancellationToken = default)
        {
            var req = this.BuildAttachmentViewRequest(kind, options, pageToken);
            return this.ListAttachmentsPageAsync(req, cancellationToken);
        }

        /// <summary>
        /// Enumerates attachments for one of the common "views" (orphans / large) by repeatedly
        /// calling the server with cursor tokens.
        /// </summary>
        /// <param name="kind">The view kind.</param>
        /// <param name="options">Options controlling filters, sorting, and page-size.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async sequence that yields full pages.</returns>
        public async IAsyncEnumerable<AttachmentPage> EnumerateAttachmentViewPagesAsync(
            AttachmentViewKind kind,
            AttachmentViewOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string? pageToken = null;

            while (true)
            {
                var page = await this.ListAttachmentViewPageAsync(kind, options, pageToken, cancellationToken).ConfigureAwait(false);
                yield return page;

                if (!page.HasMore)
                {
                    yield break;
                }

                pageToken = page.NextPageToken;
            }
        }

        private ListAttachmentsRequest BuildAttachmentViewRequest(AttachmentViewKind kind, AttachmentViewOptions? options, string? pageToken)
        {
            var effective = options ?? new AttachmentViewOptions();

            var take = effective.Take;
            if (take <= 0)
            {
                take = 50;
            }

            if (take > 500)
            {
                take = 500;
            }

            var req = new ListAttachmentsRequest
            {
                Skip = 0,
                Take = take,
                PageToken = pageToken ?? string.Empty,
                SortBy = effective.SortBy,
                SortDesc = effective.SortDesc,
                LargeThresholdBytes = effective.LargeThresholdBytes,
                ClientId = effective.ClientId ?? string.Empty,
                MergeOwnerId = effective.MergeOwnerId ?? string.Empty,
                OnlyOrphans = kind == AttachmentViewKind.Orphans || kind == AttachmentViewKind.LargeOrphans,
                OnlyLarge = kind == AttachmentViewKind.Large || kind == AttachmentViewKind.LargeOrphans,
            };

            if (effective.OlderThanUtc.HasValue)
            {
                req.OlderThanUtc = effective.OlderThanUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            }

            if (effective.NewerThanUtc.HasValue)
            {
                req.NewerThanUtc = effective.NewerThanUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            }

            // Default sorting for common views if caller did not supply one.
            if ((int)req.SortBy == 0)
            {
                if (kind == AttachmentViewKind.Large || kind == AttachmentViewKind.LargeOrphans)
                {
                    // 2 = LENGTH
                    req.SortBy = (AttachmentSortBy)2;
                    req.SortDesc = true;
                }
                else
                {
                    // 1 = UPLOADED_UTC
                    req.SortBy = (AttachmentSortBy)1;
                    req.SortDesc = true;
                }
            }

            return req;
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (this.retryPolicy == null)
            {
                return await action().ConfigureAwait(false);
            }

            return await this.retryPolicy.ExecuteAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await action().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }
}