//-----------------------------------------------------------------------
// <copyright file="MailForgeService.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security.Claims;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailForge.Security;
    using MailForge.Grpc;
    using MailForge.Jobs;
    using MailForge.Security;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Implements the MailForge gRPC service used by MailQueueNet for dispatching merge jobs
    /// and by admin clients for inspecting job state.
    /// </summary>
    public sealed class MailForgeService : MailForge.Grpc.MailForgeService.MailForgeServiceBase
    {
        private const string FenceHeaderName = "x-dispatch-fence";
        private const string FenceDbDefaultFileName = "dispatch_fence.db";

        private const string DefaultLogFolder = "/data/logs";
        private const string LogFileSearchPattern = "mailforge_*.txt";

        private const int DefaultTailBytes = 128 * 1024;
        private const int MaxTailBytes = 2 * 1024 * 1024;

        private static readonly string[] SupportedEngines = new[] { "liquid", "handlebars" };

        private readonly ILogger<MailForgeService> logger;
        private readonly IConfiguration configuration;
        private readonly IWebHostEnvironment env;
        private readonly IMergeJobRunner jobRunner;
        private readonly IMergeBatchStore mergeBatchStore;
        private readonly SqliteFenceStore fenceStore;
        private readonly MergeJobSummaryProvider jobSummaryProvider;
        private readonly SqliteMergeJobStore jobStore;
        private readonly string workerId;
        private readonly NonceStore nonceStore;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailForgeService"/> class.
        /// </summary>
        public MailForgeService(
            ILogger<MailForgeService> logger,
            IConfiguration configuration,
            IWebHostEnvironment env,
            IMergeJobRunner jobRunner,
            IMergeBatchStore mergeBatchStore,
            SqliteFenceStore fenceStore,
            MergeJobSummaryProvider jobSummaryProvider,
            SqliteMergeJobStore jobStore,
            NonceStore nonceStore)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.env = env;
            this.jobRunner = jobRunner;
            this.mergeBatchStore = mergeBatchStore;
            this.fenceStore = fenceStore;
            this.jobSummaryProvider = jobSummaryProvider;
            this.jobStore = jobStore;
            this.nonceStore = nonceStore;

            this.workerId = Environment.GetEnvironmentVariable("MAILFORGE_WORKER_ID")
                ?? Environment.MachineName
                ?? Guid.NewGuid().ToString("N");
        }

        /// <inheritdoc/>
        public override Task<GetInfoReply> GetInfo(GetInfoRequest request, ServerCallContext context)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            var reply = new GetInfoReply
            {
                WorkerId = this.workerId,
                Version = version,
            };

            reply.SupportedEngines.AddRange(SupportedEngines);
            return Task.FromResult(reply);
        }

        /// <inheritdoc/>
        public override async Task<StartMergeJobReply> StartMergeJob(StartMergeJobRequest request, ServerCallContext context)
        {
            await this.EnforceDispatchFenceAsync(context).ConfigureAwait(false);
            return await this.jobRunner.StartAsync(request, context.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<CancelMergeJobReply> CancelMergeJob(CancelMergeJobRequest request, ServerCallContext context)
        {
            await this.EnforceDispatchFenceAsync(context).ConfigureAwait(false);
            return await this.jobRunner.CancelAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<PauseMergeJobReply> PauseMergeJob(PauseMergeJobRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "job_id is required"));
            }

            return await this.jobRunner.PauseAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<ResumeMergeJobReply> ResumeMergeJob(ResumeMergeJobRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "job_id is required"));
            }

            return await this.jobRunner.ResumeAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task<DeleteMergeJobReply> DeleteMergeJob(DeleteMergeJobRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.MergeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "merge_id is required"));
            }

            // job_work_root is intentionally optional; the worker resolves stored state from its configured roots.
            return await this.jobRunner.DeleteAsync(request.MergeId, request.JobWorkRoot, context.CancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override Task<GetMergeJobStatusReply> GetMergeJobStatus(GetMergeJobStatusRequest request, ServerCallContext context)
        {
            return this.jobRunner.GetStatusAsync(request.JobId, request.JobWorkRoot, context.CancellationToken);
        }

        /// <inheritdoc/>
        public override async Task<AppendMergeBatchReply> AppendMergeBatch(AppendMergeBatchRequest request, ServerCallContext context)
        {
            await this.EnforceDispatchFenceAsync(context).ConfigureAwait(false);

            if (request == null || string.IsNullOrWhiteSpace(request.MergeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "merge_id is required"));
            }

            var accepted = 0;
            try
            {
                accepted = await this.mergeBatchStore.AppendAsync(
                    request.MergeId,
                    request.TemplateFileName ?? string.Empty,
                    request.BatchId,
                    request.JsonLines.ToArray(),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "AppendMergeBatch failed: mergeId={MergeId}", request.MergeId);
                return new AppendMergeBatchReply
                {
                    Success = false,
                    Accepted = 0,
                    Message = ex.Message,
                };
            }

            return new AppendMergeBatchReply
            {
                Success = true,
                Accepted = accepted,
                Message = string.Empty,
            };
        }

        /// <inheritdoc/>
        public override async Task<ListMergeJobsReply> ListMergeJobs(ListMergeJobsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var take = request?.Take ?? 50;
            if (take <= 0)
            {
                take = 50;
            }

            var roots = new[]
            {
                this.configuration["MailForge:DefaultJobWorkRoot"],
                this.configuration["MailForge:JobWorkRoot"],
                AppContext.BaseDirectory,
            };

            var jobs = await this.jobSummaryProvider.ListJobsAsync(roots, take, context.CancellationToken).ConfigureAwait(false);

            var reply = new ListMergeJobsReply();
            reply.Jobs.AddRange(jobs);
            return reply;
        }

        /// <inheritdoc/>
        public override Task<PreviewMergeBatchReply> PreviewMergeBatch(PreviewMergeBatchRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.MergeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "merge_id is required"));
            }

            if (request.BatchId < 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "batch_id is required"));
            }

            var take = request.Take;
            if (take <= 0)
            {
                take = 50;
            }

            if (take > 500)
            {
                take = 500;
            }

            var skip = request.Skip;
            if (skip < 0)
            {
                skip = 0;
            }

            var batchesFolder = this.ResolveAndEnsureBatchesFolder();
            var pattern = request.MergeId + "__*__" + request.BatchId.ToString(CultureInfo.InvariantCulture) + ".jsonl";

            string? path = null;
            try
            {
                path = Directory.GetFiles(batchesFolder, pattern).FirstOrDefault();
            }
            catch
            {
            }

            if ((string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            {
                try
                {
                    var processedFolder = Path.Combine(batchesFolder, "processed");
                    if (Directory.Exists(processedFolder))
                    {
                        path = Directory.GetFiles(processedFolder, pattern).FirstOrDefault();
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Task.FromResult(new PreviewMergeBatchReply
                {
                    Success = false,
                    Message = "Batch file not found.",
                    BatchId = request.BatchId,
                    TotalRows = 0,
                });
            }

            try
            {
                var allLines = File.ReadAllLines(path);
                var totalRows = allLines.Length;

                var rows = allLines
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Skip(skip)
                    .Take(take)
                    .ToArray();

                var reply = new PreviewMergeBatchReply
                {
                    Success = true,
                    Message = string.Empty,
                    BatchId = request.BatchId,
                    TotalRows = totalRows,
                };

                reply.Rows.AddRange(rows);
                return Task.FromResult(reply);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new PreviewMergeBatchReply
                {
                    Success = false,
                    Message = ex.Message,
                    BatchId = request.BatchId,
                    TotalRows = 0,
                });
            }
        }

        /// <inheritdoc/>
        public override async Task<GetMergeJobDetailReply> GetMergeJobDetail(GetMergeJobDetailRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.MergeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "merge_id is required"));
            }

            var jobWorkRoot = request.JobWorkRoot;
            if (string.IsNullOrWhiteSpace(jobWorkRoot))
            {
                jobWorkRoot = this.configuration["MailForge:DefaultJobWorkRoot"] ?? string.Empty;
            }

            var candidateRoots = new[]
            {
                jobWorkRoot,
                this.configuration["MailForge:JobWorkRoot"] ?? string.Empty,
                this.configuration["MailForge:DefaultJobWorkRoot"] ?? string.Empty,
                AppContext.BaseDirectory,
            };

            var dbPath = SqliteMergeJobStore.TryResolveJobDatabasePath(request.MergeId, candidateRoots);
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return new GetMergeJobDetailReply
                {
                    Summary = new MergeJobSummary
                    {
                        MergeId = request.MergeId,
                        Status = "Unknown",
                    },
                    PercentComplete = 0,
                };
            }

            var summary = await this.jobSummaryProvider.TryGetJobSummaryAsync(dbPath, request.MergeId, context.CancellationToken).ConfigureAwait(false);
            if (summary == null)
            {
                return new GetMergeJobDetailReply
                {
                    Summary = new MergeJobSummary
                    {
                        MergeId = request.MergeId,
                        Status = "Unknown",
                    },
                    PercentComplete = 0,
                };
            }

            var percent = 0;
            if (summary.TotalRows > 0)
            {
                percent = (int)Math.Min(100, Math.Round((double)summary.CompletedRows * 100.0 / summary.TotalRows, 0));
            }

            var reply = new GetMergeJobDetailReply
            {
                Summary = summary,
                PercentComplete = percent,
            };

            try
            {
                var templateBytes = await this.jobStore.TryGetTemplateAsync(dbPath, request.MergeId, context.CancellationToken).ConfigureAwait(false);
                if (templateBytes != null && templateBytes.Length > 0)
                {
                    reply.Template = MailQueueNet.Grpc.MailMessageWithSettings.Parser.ParseFrom(templateBytes);
                }
            }
            catch
            {
            }

            try
            {
                var batches = await this.jobStore.ListBatchesAsync(dbPath, request.MergeId, context.CancellationToken).ConfigureAwait(false);
                foreach (var b in batches)
                {
                    reply.Batches.Add(new MergeBatchStatus
                    {
                        BatchId = b.BatchId,
                        Status = b.Status.ToString(),
                        TotalRows = b.TotalRows,
                        CompletedRows = b.CompletedRows,
                        FailedRows = b.FailedRows,
                        UpdatedUtc = b.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
                    });
                }
            }
            catch
            {
            }

            return reply;
        }

        /// <inheritdoc/>
        public override Task<ListLogsReply> ListWorkerLogFiles(ListLogsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var baseFolder = this.ResolveLogBaseFolder();
            var reply = new ListLogsReply
            {
                BaseFolder = baseFolder,
            };

            try
            {
                if (!Directory.Exists(baseFolder))
                {
                    return Task.FromResult(reply);
                }

                var files = Directory
                    .GetFiles(baseFolder, LogFileSearchPattern)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new LogFileInfo
                    {
                        Name = f.Name,
                        Size = f.Length,
                        ModifiedUtc = f.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                    })
                    .ToArray();

                reply.Files.AddRange(files);
            }
            catch
            {
            }

            return Task.FromResult(reply);
        }

        /// <inheritdoc/>
        public override async Task<ReadLogReply> ReadWorkerLog(ReadLogRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
            }

            var logPath = this.TryResolveLogPath(request.Name);
            if (string.IsNullOrWhiteSpace(logPath))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid log name."));
            }

            if (!File.Exists(logPath))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Log file not found."));
            }

            var tailBytes = NormaliseTailBytes(request.TailBytes);
            var content = await this.ReadTailAsync(logPath, tailBytes, context.CancellationToken).ConfigureAwait(false);

            var fi = new FileInfo(logPath);
            return new ReadLogReply
            {
                Name = fi.Name,
                Content = content,
                Size = fi.Length,
                ModifiedUtc = fi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        }

        /// <inheritdoc/>
        public override async Task StreamWorkerLog(ReadLogRequest request, IServerStreamWriter<ReadLogReply> responseStream, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
            }

            var logPath = this.TryResolveLogPath(request.Name);
            if (string.IsNullOrWhiteSpace(logPath))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid log name."));
            }

            if (!File.Exists(logPath))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Log file not found."));
            }

            var safeName = Path.GetFileName(request.Name);
            var tailBytes = NormaliseTailBytes(request.TailBytes);

            await using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);

            var initialStart = Math.Max(0, fs.Length - tailBytes);
            fs.Seek(initialStart, SeekOrigin.Begin);
            var initial = await sr.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(initial))
            {
                var initialInfo = new FileInfo(logPath);
                await responseStream.WriteAsync(new ReadLogReply
                {
                    Name = safeName,
                    Content = initial,
                    Size = initialInfo.Length,
                    ModifiedUtc = initialInfo.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                }, context.CancellationToken).ConfigureAwait(false);
            }

            while (!context.CancellationToken.IsCancellationRequested)
            {
                var info = new FileInfo(logPath);
                if (info.Length < fs.Position)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    sr.DiscardBufferedData();
                }

                var chunk = await sr.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(chunk))
                {
                    await responseStream.WriteAsync(new ReadLogReply
                    {
                        Name = safeName,
                        Content = chunk,
                        Size = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                    }, context.CancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken).ConfigureAwait(false);
            }
        }

        private static int NormaliseTailBytes(int requestedTailBytes)
        {
            if (requestedTailBytes <= 0)
            {
                return DefaultTailBytes;
            }

            if (requestedTailBytes > MaxTailBytes)
            {
                return MaxTailBytes;
            }

            return requestedTailBytes;
        }

        private static bool IsAdmin(ServerCallContext context)
        {
            return context.GetHttpContext()?.User?.IsInRole("Admin") == true;
        }

        private string ResolveLogBaseFolder()
        {
            var configuredPath = this.configuration["FileLogging:Path"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return DefaultLogFolder;
            }

            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            var basePath = this.env.ContentRootPath ?? AppContext.BaseDirectory;
            return Path.Combine(basePath, configuredPath);
        }

        private string? TryResolveLogPath(string name)
        {
            var baseFolder = this.ResolveLogBaseFolder();
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                return null;
            }

            try
            {
                var fullBase = Path.GetFullPath(baseFolder);
                var fullCandidate = Path.GetFullPath(Path.Combine(baseFolder, safeName));

                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

                var basePrefix = fullBase;
                if (!basePrefix.EndsWith(Path.DirectorySeparatorChar))
                {
                    basePrefix += Path.DirectorySeparatorChar;
                }

                if (!fullCandidate.StartsWith(basePrefix, comparison))
                {
                    return null;
                }

                return fullCandidate;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> ReadTailAsync(string path, int tailBytes, CancellationToken cancellationToken)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var toRead = Math.Min(len, (long)tailBytes);
            if (toRead > 0)
            {
                fs.Seek(-toRead, SeekOrigin.End);
            }

            using var sr = new StreamReader(fs, Encoding.UTF8);
            return await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        private void EnsureAdmin(ServerCallContext context)
        {
            if (IsAdmin(context))
            {
                return;
            }

            if (this.IsSharedSecretAdmin(context, out var adminId))
            {
                this.EnforceAdminReplayProtection(context, adminId);
                return;
            }

            throw new RpcException(new Status(StatusCode.PermissionDenied, "Admin privileges required"));
        }

        private static string ComputePassword(string clientId, string sharedSecret)
        {
            using var sha = SHA256.Create();
            var data = Encoding.UTF8.GetBytes(clientId + ":" + sharedSecret);
            return Convert.ToBase64String(sha.ComputeHash(data));
        }

        private static bool ConstantEquals(string a, string b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        private bool IsSharedSecretAdmin(ServerCallContext context, out string adminId)
        {
            adminId = string.Empty;
            var sharedSecret = this.configuration["Security:AdminSharedSecret"];
            if (string.IsNullOrWhiteSpace(sharedSecret))
            {
                return false;
            }

            adminId = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-admin-id")?.Value ?? string.Empty;
            var suppliedPass = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-admin-pass")?.Value;
            if (string.IsNullOrWhiteSpace(adminId) || string.IsNullOrWhiteSpace(suppliedPass))
            {
                return false;
            }

            var expected = ComputePassword(adminId, sharedSecret);
            if (!ConstantEquals(expected, suppliedPass))
            {
                this.logger.LogWarning("Admin shared-secret auth failed for id {AdminId}", adminId);
                return false;
            }

            return true;
        }

        private void EnforceAdminReplayProtection(ServerCallContext context, string adminId)
        {
            var tsHeader = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-ts")?.Value;
            var nonceHeader = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-nonce")?.Value;

            if (string.IsNullOrWhiteSpace(tsHeader) || string.IsNullOrWhiteSpace(nonceHeader))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing replay protection headers"));
            }

            if (!DateTimeOffset.TryParse(tsHeader, out var parsedTs))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid timestamp"));
            }

            var skew = DateTimeOffset.UtcNow - parsedTs;
            if (skew < TimeSpan.FromMinutes(-5) || skew > TimeSpan.FromMinutes(5))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Stale timestamp"));
            }

            var key = string.IsNullOrWhiteSpace(adminId)
                ? nonceHeader
                : adminId + ":" + nonceHeader;

            if (!this.nonceStore.TryRegister(key, parsedTs))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid nonce"));
            }
        }

        private string ResolveFenceDbPath()
        {
            var value = this.configuration["MailForge:FenceDbPath"];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return Path.Combine(AppContext.BaseDirectory, FenceDbDefaultFileName);
        }

        private async Task EnforceDispatchFenceAsync(ServerCallContext context)
        {
            var headers = context.RequestHeaders;
            var tokenString = headers?.FirstOrDefault(h => string.Equals(h.Key, FenceHeaderName, StringComparison.OrdinalIgnoreCase))?.Value;

            if (string.IsNullOrWhiteSpace(tokenString) || !long.TryParse(tokenString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Missing or invalid dispatch fence header"));
            }

            var dbPath = this.ResolveFenceDbPath();
            var accepted = await this.fenceStore.TryAcceptFenceAsync(dbPath, token, context.CancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Dispatch fence token rejected"));
            }
        }

        private string ResolveAndEnsureBatchesFolder()
        {
            var configuredBaseFolder = Environment.GetEnvironmentVariable("MAILFORGE_BATCH_FOLDER");
            if (!string.IsNullOrWhiteSpace(configuredBaseFolder))
            {
                try
                {
                    Directory.CreateDirectory(configuredBaseFolder);
                    return configuredBaseFolder;
                }
                catch (UnauthorizedAccessException ex)
                {
                    var fallbackFolder = Path.Combine(Path.GetTempPath(), "MailForge", "batches");
                    this.logger.LogWarning(ex, "Batch folder '{BatchFolder}' is not writable. Falling back to '{FallbackBatchFolder}'.", configuredBaseFolder, fallbackFolder);

                    Directory.CreateDirectory(fallbackFolder);
                    return fallbackFolder;
                }
            }

            var baseFolder = Path.Combine(AppContext.BaseDirectory, "data", "batches");
            try
            {
                Directory.CreateDirectory(baseFolder);
                return baseFolder;
            }
            catch (UnauthorizedAccessException ex)
            {
                var fallbackFolder = Path.Combine(Path.GetTempPath(), "MailForge", "batches");
                this.logger.LogWarning(ex, "Default batch folder '{DefaultBatchFolder}' is not writable. Falling back to '{FallbackBatchFolder}'.", baseFolder, fallbackFolder);

                Directory.CreateDirectory(fallbackFolder);
                return fallbackFolder;
            }
        }
    }
}
