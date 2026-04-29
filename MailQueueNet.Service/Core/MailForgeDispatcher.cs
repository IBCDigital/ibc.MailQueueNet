// <copyright file="MailForgeDispatcher.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using global::Grpc.Net.Client;
    using MailForge.Grpc;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    public sealed class MailForgeDispatcher : IMailForgeDispatcher, IDisposable
    {
        private const string FenceHeaderName = "x-dispatch-fence";
        private const string DefaultStateDbFileName = "dispatcher_state.db";

        private readonly ILogger<MailForgeDispatcher> logger;
        private readonly IConfiguration configuration;
        private readonly SqliteDispatcherStateStore stateStore;
        private readonly object gate = new object();

        private MailForgeDispatcherOptions options;
        private DispatcherLease? lease;
        private Timer? leaseTimer;

        private string? stateDbPathForRead;
        private string? stateDbPathForWrite;
        private bool stateDbWriteFallbackLogged;

        public MailForgeDispatcher(ILogger<MailForgeDispatcher> logger, IConfiguration configuration, SqliteDispatcherStateStore stateStore)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.stateStore = stateStore;
            this.options = new MailForgeDispatcherOptions();
            this.RefreshSettings();
        }

        /// <inheritdoc/>
        public DispatcherLease? GetLeaseSnapshot()
        {
            lock (this.gate)
            {
                if (this.lease == null)
                {
                    return null;
                }

                return new DispatcherLease
                {
                    WorkerAddress = this.lease.WorkerAddress,
                    FenceToken = this.lease.FenceToken,
                    ExpiresUtc = this.lease.ExpiresUtc,
                };
            }
        }

        /// <inheritdoc/>
        public void RefreshSettings()
        {
            var newOptions = new MailForgeDispatcherOptions();
            this.configuration.GetSection("mailforge:dispatcher").Bind(newOptions);
            this.options = newOptions;

            this.leaseTimer?.Dispose();
            this.leaseTimer = null;

            if (!this.options.Enabled)
            {
                this.logger.LogInformation("MailForge dispatcher is disabled.");
                return;
            }

            if (this.options.WorkerAddresses == null || this.options.WorkerAddresses.Count == 0)
            {
                this.logger.LogWarning("MailForge dispatcher is enabled but no workers are configured.");
                return;
            }

            _ = Task.Run(() => this.TryRestoreLeaseAsync(CancellationToken.None), CancellationToken.None);

            var renewInterval = this.options.RenewInterval;
            if (renewInterval <= TimeSpan.Zero)
            {
                renewInterval = TimeSpan.FromSeconds(30);
            }

            this.leaseTimer = new Timer(
                static state => _ = ((MailForgeDispatcher)state!).RenewLeaseAsync(CancellationToken.None),
                this,
                TimeSpan.Zero,
                renewInterval);
        }

        /// <inheritdoc/>
        public async Task<StartMergeJobReply> StartMergeJobAsync(StartMergeJobRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!this.options.Enabled)
            {
                return new StartMergeJobReply
                {
                    Accepted = false,
                    Message = "MailForge dispatcher is disabled",
                };
            }

            var leaseSnapshot = this.GetLeaseSnapshot();
            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                await this.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                leaseSnapshot = this.GetLeaseSnapshot();
            }

            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                return new StartMergeJobReply
                {
                    Accepted = false,
                    Message = "No MailForge worker lease is available",
                };
            }

            return await this.CallStartMergeJobAsync(leaseSnapshot, request, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<CancelMergeJobReply> CancelMergeJobAsync(string jobId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required", nameof(jobId));
            }

            if (!this.options.Enabled)
            {
                return new CancelMergeJobReply
                {
                    Success = false,
                    Message = "MailForge dispatcher is disabled",
                };
            }

            var leaseSnapshot = this.GetLeaseSnapshot();
            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                await this.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                leaseSnapshot = this.GetLeaseSnapshot();
            }

            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                return new CancelMergeJobReply
                {
                    Success = false,
                    Message = "No MailForge worker lease is available",
                };
            }

            return await this.CallCancelMergeJobAsync(leaseSnapshot, jobId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<AppendMergeBatchReply> AppendMergeBatchAsync(AppendMergeBatchRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!this.options.Enabled)
            {
                return new AppendMergeBatchReply
                {
                    Success = false,
                    Accepted = 0,
                    Message = "MailForge dispatcher is disabled",
                };
            }

            var leaseSnapshot = this.GetLeaseSnapshot();
            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                await this.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                leaseSnapshot = this.GetLeaseSnapshot();
            }

            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                return new AppendMergeBatchReply
                {
                    Success = false,
                    Accepted = 0,
                    Message = "No MailForge worker lease is available",
                };
            }

            return await this.CallAppendMergeBatchAsync(leaseSnapshot, request, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<GetMergeJobStatusReply> GetMergeJobStatusAsync(GetMergeJobStatusRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!this.options.Enabled)
            {
                return new GetMergeJobStatusReply
                {
                    JobId = request.JobId,
                    Status = "Unknown",
                    LastError = "MailForge dispatcher is disabled",
                };
            }

            var leaseSnapshot = this.GetLeaseSnapshot();
            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                await this.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                leaseSnapshot = this.GetLeaseSnapshot();
            }

            if (leaseSnapshot == null || !leaseSnapshot.IsValid)
            {
                return new GetMergeJobStatusReply
                {
                    JobId = request.JobId,
                    Status = "Unknown",
                    LastError = "No MailForge worker lease is available",
                };
            }

            return await this.CallGetMergeJobStatusAsync(leaseSnapshot, request, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.leaseTimer?.Dispose();
            this.leaseTimer = null;
        }

        private string ResolveStateDbPath()
        {
            // Backwards compatible wrapper used by existing callers.
            return this.ResolveStateDbPathForWrite();
        }

        private string ResolveStateDbPathForRead()
        {
            lock (this.gate)
            {
                if (!string.IsNullOrWhiteSpace(this.stateDbPathForRead))
                {
                    return this.stateDbPathForRead;
                }

                this.stateDbPathForRead = this.ResolveConfiguredStateDbPath();
                return this.stateDbPathForRead;
            }
        }

        private string ResolveStateDbPathForWrite()
        {
            lock (this.gate)
            {
                if (!string.IsNullOrWhiteSpace(this.stateDbPathForWrite))
                {
                    return this.stateDbPathForWrite;
                }

                var preferred = this.ResolveConfiguredStateDbPath();
                this.stateDbPathForWrite = this.TryGetWritableStateDbPath(preferred);
                return this.stateDbPathForWrite;
            }
        }

        private string ResolveConfiguredStateDbPath()
        {
            var configured = this.configuration["mailforge:dispatcher:stateDbPath"];
            var root = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(root, DefaultStateDbFileName);
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(root, configured);
        }

        private string TryGetWritableStateDbPath(string preferred)
        {
            if (string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            try
            {
                var folder = Path.GetDirectoryName(preferred);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                if (File.Exists(preferred))
                {
                    using var fs = new FileStream(preferred, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    return preferred;
                }

                // If the DB does not exist yet, validate directory write access using a temporary probe file.
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var probe = Path.Combine(folder, ".write_probe_" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                }

                return preferred;
            }
            catch (Exception ex)
            {
                var fallbackFolder = Path.Combine(Path.GetTempPath(), "MailQueueNet", "state");
                var fallback = Path.Combine(fallbackFolder, DefaultStateDbFileName);

                try
                {
                    Directory.CreateDirectory(fallbackFolder);
                }
                catch
                {
                    // If even the temp folder is unavailable, return the preferred path and allow callers to log the failure.
                    return preferred;
                }

                if (!this.stateDbWriteFallbackLogged)
                {
                    this.stateDbWriteFallbackLogged = true;
                    this.logger.LogWarning(
                        ex,
                        "Dispatcher state database path is not writable. Falling back to '{FallbackPath}'. PreferredPath='{PreferredPath}'.",
                        fallback,
                        preferred);
                }

                return fallback;
            }
        }

        private async Task TryRestoreLeaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                var path = this.ResolveStateDbPathForRead();
                var persisted = await this.stateStore.TryLoadLeaseAsync(path, cancellationToken).ConfigureAwait(false);
                if (persisted == null)
                {
                    return;
                }

                lock (this.gate)
                {
                    this.lease = persisted;
                }

                this.logger.LogInformation(
                    "Restored persisted dispatcher lease snapshot (Worker={Worker}; Fence={Fence}; Expires={Expires:o})",
                    persisted.WorkerAddress,
                    persisted.FenceToken,
                    persisted.ExpiresUtc);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed restoring persisted dispatcher lease");
            }
        }

        private async Task PersistLeaseAsync(DispatcherLease leaseSnapshot, CancellationToken cancellationToken)
        {
            try
            {
                var path = this.ResolveStateDbPathForWrite();
                await this.stateStore.SaveLeaseAsync(path, leaseSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed persisting dispatcher lease");
            }
        }

        private async Task RenewLeaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!this.options.Enabled)
                {
                    return;
                }

                var workers = (this.options.WorkerAddresses ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (workers.Length == 0)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                DispatcherLease? snapshot;

                lock (this.gate)
                {
                    if (this.lease != null && this.lease.IsValid)
                    {
                        this.lease.ExpiresUtc = now.Add(this.GetLeaseDuration());
                        snapshot = new DispatcherLease
                        {
                            WorkerAddress = this.lease.WorkerAddress,
                            FenceToken = this.lease.FenceToken,
                            ExpiresUtc = this.lease.ExpiresUtc,
                        };
                    }
                    else
                    {
                        // Fence tokens are designed to be monotonic to prevent stale dispatchers from
                        // dispatching work to a worker after a restart. When the dispatcher loses its
                        // persisted lease state (for example, due to a state DB path change in dev),
                        // restarting at fence token 1 can be rejected by a worker that has already
                        // accepted a previous token.
                        //
                        // To make this resilient in development, seed the next fence token to at least
                        // the current Unix time, which is safely higher than the small incrementing
                        // values used in earlier runs.
                        var nextFence = Math.Max((this.lease?.FenceToken ?? 0) + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        this.lease = new DispatcherLease
                        {
                            WorkerAddress = workers[0],
                            FenceToken = nextFence,
                            ExpiresUtc = now.Add(this.GetLeaseDuration()),
                        };

                        snapshot = new DispatcherLease
                        {
                            WorkerAddress = this.lease.WorkerAddress,
                            FenceToken = this.lease.FenceToken,
                            ExpiresUtc = this.lease.ExpiresUtc,
                        };
                    }
                }

                if (snapshot != null)
                {
                    await this.PersistLeaseAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to renew MailForge worker lease");
            }
        }

        private TimeSpan GetLeaseDuration()
        {
            var duration = this.options.LeaseDuration;
            if (duration <= TimeSpan.Zero)
            {
                duration = TimeSpan.FromMinutes(2);
            }

            return duration;
        }

        private Metadata BuildFenceHeaders(DispatcherLease leaseSnapshot)
        {
            return new Metadata
            {
                { FenceHeaderName, leaseSnapshot.FenceToken.ToString(CultureInfo.InvariantCulture) },
            };
        }

        private GrpcChannel CreateWorkerChannel(string workerAddress)
        {
            if (!this.options.AllowInvalidWorkerCertificates)
            {
                return GrpcChannel.ForAddress(workerAddress);
            }

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };

            return GrpcChannel.ForAddress(workerAddress, new GrpcChannelOptions
            {
                HttpHandler = handler,
            });
        }

        private async Task<StartMergeJobReply> CallStartMergeJobAsync(DispatcherLease leaseSnapshot, StartMergeJobRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var channel = this.CreateWorkerChannel(leaseSnapshot.WorkerAddress);
                var client = new MailForgeService.MailForgeServiceClient(channel);

                var headers = this.BuildFenceHeaders(leaseSnapshot);

                var call = client.StartMergeJobAsync(request, headers: headers, cancellationToken: cancellationToken);
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "StartMergeJob failed (Worker={Worker}; JobId={JobId}; Fence={Fence})",
                    leaseSnapshot.WorkerAddress,
                    request.JobId,
                    leaseSnapshot.FenceToken);

                return new StartMergeJobReply
                {
                    Accepted = false,
                    Message = ex.Message,
                };
            }
        }

        private async Task<CancelMergeJobReply> CallCancelMergeJobAsync(DispatcherLease leaseSnapshot, string jobId, CancellationToken cancellationToken)
        {
            try
            {
                using var channel = this.CreateWorkerChannel(leaseSnapshot.WorkerAddress);
                var client = new MailForgeService.MailForgeServiceClient(channel);

                var headers = this.BuildFenceHeaders(leaseSnapshot);

                var req = new CancelMergeJobRequest
                {
                    JobId = jobId,
                };

                var call = client.CancelMergeJobAsync(req, headers: headers, cancellationToken: cancellationToken);
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "CancelMergeJob failed (Worker={Worker}; JobId={JobId}; Fence={Fence})",
                    leaseSnapshot.WorkerAddress,
                    jobId,
                    leaseSnapshot.FenceToken);

                return new CancelMergeJobReply
                {
                    Success = false,
                    Message = ex.Message,
                };
            }
        }

        private async Task<AppendMergeBatchReply> CallAppendMergeBatchAsync(DispatcherLease leaseSnapshot, AppendMergeBatchRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var channel = this.CreateWorkerChannel(leaseSnapshot.WorkerAddress);
                var client = new MailForgeService.MailForgeServiceClient(channel);

                var headers = this.BuildFenceHeaders(leaseSnapshot);

                var call = client.AppendMergeBatchAsync(request, headers: headers, cancellationToken: cancellationToken);
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "AppendMergeBatch failed (Worker={Worker}; MergeId={MergeId}; Fence={Fence})",
                    leaseSnapshot.WorkerAddress,
                    request.MergeId,
                    leaseSnapshot.FenceToken);

                return new AppendMergeBatchReply
                {
                    Success = false,
                    Accepted = 0,
                    Message = ex.Message,
                };
            }
        }

        private async Task<GetMergeJobStatusReply> CallGetMergeJobStatusAsync(DispatcherLease leaseSnapshot, GetMergeJobStatusRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var channel = this.CreateWorkerChannel(leaseSnapshot.WorkerAddress);
                var client = new MailForgeService.MailForgeServiceClient(channel);

                var headers = this.BuildFenceHeaders(leaseSnapshot);

                var call = client.GetMergeJobStatusAsync(request, headers: headers, cancellationToken: cancellationToken);
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "GetMergeJobStatus failed (Worker={Worker}; JobId={JobId}; Fence={Fence})",
                    leaseSnapshot.WorkerAddress,
                    request.JobId,
                    leaseSnapshot.FenceToken);

                return new GetMergeJobStatusReply
                {
                    JobId = request.JobId,
                    Status = "Unknown",
                    LastError = ex.Message,
                };
            }
        }
    }
}
