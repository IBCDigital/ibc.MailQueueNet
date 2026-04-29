//-----------------------------------------------------------------------
// <copyright file="MergeJobRunner.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Mail;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Google.Protobuf;
    using MailForge.Grpc;
    using MailForge.Queue;
    using MailForge.Template;
    using MailQueueNet.Grpc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Executes merge jobs and persists durable state into the per-job database.
    /// </summary>
    public sealed class MergeJobRunner : IMergeJobRunner
    {
        private const int DefaultMaxBatchSize = 50;

        private static readonly TimeSpan MergeJobReopenWindow = TimeSpan.FromSeconds(60);

        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private readonly ILogger<MergeJobRunner> logger;
        private readonly SqliteMergeJobStore store;
        private readonly MailForgeOptions options;
        private readonly ConcurrentDictionary<string, JobExecution> runningJobs;
        private readonly IMailQueueClient mailQueueClient;
        private readonly MailQueueOptions mailQueueOptions;
        private readonly ITemplateEngineResolver templateEngineResolver;

        /// <summary>
        /// Initialises a new instance of the <see cref="MergeJobRunner"/> class.
        /// </summary>
        public MergeJobRunner(
            ILogger<MergeJobRunner> logger,
            SqliteMergeJobStore store,
            IOptions<MailForgeOptions> options,
            IMailQueueClient mailQueueClient,
            IOptions<MailQueueOptions> mailQueueOptions,
            ITemplateEngineResolver templateEngineResolver)
        {
            this.logger = logger;
            this.store = store;
            this.options = options.Value;
            this.mailQueueClient = mailQueueClient;
            this.mailQueueOptions = mailQueueOptions.Value;
            this.templateEngineResolver = templateEngineResolver;
            this.runningJobs = new ConcurrentDictionary<string, JobExecution>(StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public async Task<StartMergeJobReply> StartAsync(StartMergeJobRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request == null || string.IsNullOrWhiteSpace(request.JobId))
            {
                return new StartMergeJobReply
                {
                    Accepted = false,
                    Message = "Job id is required.",
                };
            }

            var resolvedJobWorkRoot = this.ResolveJobWorkRoot(request.JobId, request.JobWorkRoot);
            if (string.IsNullOrWhiteSpace(resolvedJobWorkRoot))
            {
                return new StartMergeJobReply
                {
                    Accepted = false,
                    Message = "Job work root is not configured. Configure MailForge:JobWorkRoot (or MailForge:JobWorkRoots).",
                };
            }

            this.logger.LogInformation(
                "Merge job accepted (JobId={JobId}; Engine={Engine}; ClientId={ClientId}; JobWorkRoot={JobWorkRoot})",
                request.JobId,
                request.Engine.ToString(),
                request.ClientId ?? string.Empty,
                resolvedJobWorkRoot);

            var dbPath = await this.store.EnsureJobDatabaseAsync(resolvedJobWorkRoot, request.JobId, cancellationToken).ConfigureAwait(false);

            _ = await this.store.UpsertJobAsync(
                dbPath,
                request.JobId,
                request.ClientId ?? string.Empty,
                request.Engine.ToString(),
                inputJsonPath: string.Empty,
                jobWorkRoot: resolvedJobWorkRoot,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (request.Template != null)
            {
                await this.store.SetTemplateAsync(dbPath, request.JobId, request.Template.ToByteArray(), cancellationToken).ConfigureAwait(false);
            }

            // Ensure we can render before starting the job.
            var renderer = this.ResolveRenderer(request.Engine);

            // If already running, treat as idempotent.
            if (this.runningJobs.ContainsKey(request.JobId))
            {
                return new StartMergeJobReply
                {
                    Accepted = true,
                    Message = "Job already running.",
                };
            }

            var execution = new JobExecution(request.JobId, dbPath, resolvedJobWorkRoot);
            this.runningJobs[request.JobId] = execution;

            execution.Task = Task.Run(async () =>
            {
                try
                {
                    await this.ExecuteJobAsync(dbPath, request, renderer, execution, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Merge job failed: {JobId}", request.JobId);
                    try
                    {
                        await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    this.runningJobs.TryRemove(request.JobId, out _);
                }
            }, CancellationToken.None);

            return new StartMergeJobReply
            {
                Accepted = true,
                Message = "Accepted.",
            };
        }

        /// <inheritdoc/>
        public Task<PauseMergeJobReply> PauseAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Task.FromResult(new PauseMergeJobReply
                {
                    Success = false,
                    Message = "Job id is required.",
                });
            }

            if (this.runningJobs.TryGetValue(jobId, out var execution))
            {
                // Mark as pause rather than cancellation to preserve state.
                execution.RequestPause();
                return Task.FromResult(new PauseMergeJobReply
                {
                    Success = true,
                    Message = "Pause requested.",
                });
            }

            return Task.FromResult(new PauseMergeJobReply
            {
                Success = false,
                Message = "Job is not running.",
            });
        }

        /// <inheritdoc/>
        public async Task<ResumeMergeJobReply> ResumeAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return new ResumeMergeJobReply
                {
                    Success = false,
                    Message = "Job id is required.",
                };
            }

            if (this.runningJobs.ContainsKey(jobId))
            {
                return new ResumeMergeJobReply
                {
                    Success = true,
                    Message = "Job already running.",
                };
            }

            var dbPath = SqliteMergeJobStore.TryResolveJobDatabasePath(jobId, this.options.JobWorkRoots);
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return new ResumeMergeJobReply
                {
                    Success = false,
                    Message = "Unable to locate job database. Configure MailForge:JobWorkRoots.",
                };
            }
            var snapshot = await this.store.TryReadJobAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
            {
                return new ResumeMergeJobReply
                {
                    Success = false,
                    Message = "Job not found.",
                };
            }

            if (snapshot.Status != MergeJobStatus.Paused)
            {
                return new ResumeMergeJobReply
                {
                    Success = false,
                    Message = "Job is not paused.",
                };
            }

            var templateBytes = await this.store.TryGetTemplateAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
            if (templateBytes == null || templateBytes.Length == 0)
            {
                return new ResumeMergeJobReply
                {
                    Success = false,
                    Message = "Template is missing for paused job.",
                };
            }

            var template = MailQueueNet.Grpc.MailMessageWithSettings.Parser.ParseFrom(templateBytes);
            var engine = TemplateEngine.Unspecified;
            try
            {
                if (!string.IsNullOrWhiteSpace(snapshot.Engine))
                {
                    engine = (TemplateEngine)Enum.Parse(typeof(TemplateEngine), snapshot.Engine, ignoreCase: true);
                }
            }
            catch
            {
                engine = TemplateEngine.Unspecified;
            }

            engine = engine == TemplateEngine.Unspecified ? TemplateEngine.Liquid : engine;
            var renderer = this.ResolveRenderer(engine);

            var request = new StartMergeJobRequest
            {
                JobId = jobId,
                Engine = engine,
                Template = template,
                JobWorkRoot = snapshot.JobWorkRoot,
                ClientId = snapshot.ClientId,
            };

            var execution = new JobExecution(jobId, dbPath, request.JobWorkRoot);
            this.runningJobs[jobId] = execution;

            await this.store.UpdateJobStatusAsync(dbPath, jobId, MergeJobStatus.Running, null, CancellationToken.None).ConfigureAwait(false);

            execution.Task = Task.Run(async () =>
            {
                try
                {
                    await this.ExecuteJobAsync(dbPath, request, renderer, execution, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Merge job resume failed: {JobId}", jobId);
                    try
                    {
                        await this.store.UpdateJobStatusAsync(dbPath, jobId, MergeJobStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    this.runningJobs.TryRemove(jobId, out _);
                }
            }, CancellationToken.None);

            return new ResumeMergeJobReply
            {
                Success = true,
                Message = "Resumed.",
            };
        }

        /// <inheritdoc/>
        public async Task<DeleteMergeJobReply> DeleteAsync(string mergeId, string? jobWorkRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return new DeleteMergeJobReply
                {
                    Success = false,
                    Message = "Merge id is required.",
                };
            }

            if (this.runningJobs.TryGetValue(mergeId, out var running))
            {
                running.Cancel();
            }

            var dbPath = !string.IsNullOrWhiteSpace(jobWorkRoot)
                ? SqliteMergeJobStore.GetJobDatabasePath(jobWorkRoot, mergeId)
                : SqliteMergeJobStore.TryResolveJobDatabasePath(mergeId, this.options.JobWorkRoots);

            var resolvedJobFolder = this.TryResolveJobFolder(mergeId, dbPath, jobWorkRoot);

            try
            {
                if (!string.IsNullOrWhiteSpace(resolvedJobFolder) && Directory.Exists(resolvedJobFolder))
                {
                    Directory.Delete(resolvedJobFolder, recursive: true);
                }
            }
            catch
            {
                // Fall back to best-effort deletes.
                try
                {
                    if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
                    {
                        File.Delete(dbPath);
                    }
                }
                catch
                {
                }
            }

            // Also delete any batch files written under the global batches folder.
            try
            {
                var batchesFolder = this.ResolveBatchesFolder();
                if (Directory.Exists(batchesFolder))
                {
                    foreach (var file in Directory.GetFiles(batchesFolder, mergeId + "__*.jsonl"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            return new DeleteMergeJobReply
            {
                Success = true,
                Message = "Deleted.",
            };
        }

        private IMailTemplateRenderer ResolveRenderer(TemplateEngine engine)
        {
            var resolved = engine == TemplateEngine.Unspecified
                ? TemplateEngine.Liquid
                : engine;

            return this.templateEngineResolver.Resolve(resolved);
        }

        private async Task ExecuteJobAsync(
            string dbPath,
            StartMergeJobRequest request,
            IMailTemplateRenderer renderer,
            JobExecution execution,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Running, null, CancellationToken.None).ConfigureAwait(false);

            if (request.Template == null)
            {
                await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Failed, "Template is required.", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var templateTransport = request.Template;
            var subjectTemplate = templateTransport.Message?.Subject ?? string.Empty;
            var bodyTemplate = templateTransport.Message?.Body ?? string.Empty;
            var templateIsBodyHtml = templateTransport.Message?.IsBodyHtml ?? false;
            var templateTokens = templateTransport.Message?.AttachmentTokens?.ToArray() ?? Array.Empty<MailQueueNet.Grpc.AttachmentTokenRef>();
            var templateFileName = templateTransport.Message?.Headers?.FirstOrDefault(h => string.Equals(h?.Name, "X-MailMerge-TemplateFileName", StringComparison.OrdinalIgnoreCase))?.Value
                ?? string.Empty;

            if (templateTransport.Message?.From == null)
            {
                await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Failed, "Template From is required.", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var templateFrom = templateTransport.Message.From.ToSystemType();

            var batchesFolder = this.ResolveBatchesFolder();
            Directory.CreateDirectory(batchesFolder);

            this.logger.LogInformation(
                "Merge job processing started (JobId={JobId}; TemplateFileName={TemplateFileName}; BatchesFolder={BatchesFolder}; AttachmentTokens={AttachmentTokens})",
                request.JobId,
                templateFileName,
                batchesFolder,
                templateTokens.Length);

            var batchFiles = new Dictionary<int, string>(capacity: 8);

            long total = 0;
            long completed = 0;
            long failed = 0;

            var jobMarkedCompleted = false;
            var jobMarkedCompletedUtc = DateTimeOffset.MinValue;

            while (!execution.IsCancellationRequested)
            {
                var newBatchFiles = this.ListBatchFiles(batchesFolder, request.JobId, templateFileName);

                if (jobMarkedCompleted)
                {
                    var hasNewBatch = newBatchFiles.Keys.Any(batchId => !execution.ProcessedBatches.Contains(batchId));
                    if (hasNewBatch)
                    {
                        jobMarkedCompleted = false;
                        jobMarkedCompletedUtc = DateTimeOffset.MinValue;
                        await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Running, null, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                foreach (var kvp in newBatchFiles)
                {
                    if (!batchFiles.ContainsKey(kvp.Key))
                    {
                        batchFiles[kvp.Key] = kvp.Value;
                    }
                }

                if (batchFiles.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                foreach (var kvp in batchFiles.OrderBy(k => k.Key))
                {
                    if (execution.IsCancellationRequested)
                    {
                        break;
                    }

                    var batchId = kvp.Key;
                    var batchPath = kvp.Value;

                    if (execution.ProcessedBatches.Contains(batchId))
                    {
                        continue;
                    }

                    string[] lines;
                    try
                    {
                        lines = await File.ReadAllLinesAsync(batchPath, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex, "Failed reading batch file (JobId={JobId}; BatchId={BatchId}; Path={Path})", request.JobId, batchId, batchPath);
                        continue;
                    }

                    var usable = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    execution.SeenBatchIds[batchId] = usable.Length;

                    this.logger.LogInformation(
                        "Processing merge batch (JobId={JobId}; BatchId={BatchId}; Rows={Rows}; Path={Path})",
                        request.JobId,
                        batchId,
                        usable.Length,
                        batchPath);

                    await this.store.UpsertBatchAsync(dbPath, request.JobId, batchId, MergeJobStatus.Running, usable.Length, 0, 0, CancellationToken.None).ConfigureAwait(false);

                    var queuedMessages = new List<System.Net.Mail.MailMessage>();
                    var queuedInBatch = 0;

                    for (var i = 0; i < usable.Length; i++)
                    {
                        if (execution.IsCancellationRequested)
                        {
                            break;
                        }

                        var row = usable[i];
                        total++;

                        var recipient = TryGetRecipientAddress(row, out var recipientError);
                        if (string.IsNullOrWhiteSpace(recipient))
                        {
                            failed++;
                            execution.BatchFailedById[batchId] = execution.BatchFailedById.TryGetValue(batchId, out var f) ? f + 1 : 1;
                            execution.LastError = recipientError ?? "Row missing recipient email.";
                            continue;
                        }

                        TemplateRenderResult renderResult;
                        try
                        {
                            renderResult = await renderer.RenderAsync(subjectTemplate, bodyTemplate, row, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (TemplateRenderException ex)
                        {
                            failed++;
                            execution.BatchFailedById[batchId] = execution.BatchFailedById.TryGetValue(batchId, out var f) ? f + 1 : 1;
                            execution.LastError = ex.Message;
                            continue;
                        }

                        var msg = new System.Net.Mail.MailMessage
                        {
                            From = templateFrom,
                            Subject = renderResult.Subject ?? string.Empty,
                            Body = renderResult.Body ?? string.Empty,
                            IsBodyHtml = templateIsBodyHtml,
                        };

                        msg.To.Add(new System.Net.Mail.MailAddress(recipient));

                        this.ApplyAttachmentTokens(msg, templateTokens);

                        this.TryStampMergeHeaders(msg, request.JobId, batchId.ToString(CultureInfo.InvariantCulture));

                        queuedMessages.Add(msg);
                        queuedInBatch++;

                        var maxBatchSize = this.mailQueueOptions.MaxBatchSize > 0 ? this.mailQueueOptions.MaxBatchSize : DefaultMaxBatchSize;
                        if (queuedMessages.Count >= maxBatchSize)
                        {
                            var result = await this.QueueAndDisposeAsync(queuedMessages, request.JobId, batchId, CancellationToken.None).ConfigureAwait(false);
                            completed += result.Accepted;
                            failed += result.Failed;

                            if (result.Accepted == 0 && result.Failed > 0 && string.IsNullOrWhiteSpace(execution.LastError))
                            {
                                execution.LastError = this.BuildMailQueueQueueFailureReason();
                            }

                            this.logger.LogInformation(
                                "Queued mail batch to queue service (JobId={JobId}; BatchId={BatchId}; Accepted={Accepted}; Failed={Failed})",
                                request.JobId,
                                batchId,
                                result.Accepted,
                                result.Failed);

                            execution.BatchCompletedById[batchId] = execution.BatchCompletedById.TryGetValue(batchId, out var c) ? c + result.Accepted : result.Accepted;
                            execution.BatchFailedById[batchId] = execution.BatchFailedById.TryGetValue(batchId, out var bf) ? bf + result.Failed : result.Failed;

                            queuedMessages.Clear();

                            await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    if (queuedMessages.Count > 0)
                    {
                        var result = await this.QueueAndDisposeAsync(queuedMessages, request.JobId, batchId, CancellationToken.None).ConfigureAwait(false);
                        completed += result.Accepted;
                        failed += result.Failed;

                        if (result.Accepted == 0 && result.Failed > 0 && string.IsNullOrWhiteSpace(execution.LastError))
                        {
                            execution.LastError = this.BuildMailQueueQueueFailureReason();
                        }

                        this.logger.LogInformation(
                            "Queued mail batch to queue service (final flush) (JobId={JobId}; BatchId={BatchId}; Accepted={Accepted}; Failed={Failed})",
                            request.JobId,
                            batchId,
                            result.Accepted,
                            result.Failed);

                        execution.BatchCompletedById[batchId] = execution.BatchCompletedById.TryGetValue(batchId, out var c) ? c + result.Accepted : result.Accepted;
                        execution.BatchFailedById[batchId] = execution.BatchFailedById.TryGetValue(batchId, out var bf) ? bf + result.Failed : result.Failed;
                    }

                    var batchFailed = execution.BatchFailedById.TryGetValue(batchId, out var bfTotal) ? bfTotal : 0;
                    var batchCompleted = execution.BatchCompletedById.TryGetValue(batchId, out var bcTotal) ? bcTotal : 0;

                    await this.store.UpsertBatchAsync(dbPath, request.JobId, batchId, MergeJobStatus.Completed, usable.Length, batchCompleted, batchFailed, CancellationToken.None).ConfigureAwait(false);
                    await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);

                    execution.ProcessedBatches.Add(batchId);

                    this.logger.LogInformation(
                        "Merge batch completed (JobId={JobId}; BatchId={BatchId}; Rows={Rows}; Completed={Completed}; Failed={Failed})",
                        request.JobId,
                        batchId,
                        usable.Length,
                        batchCompleted,
                        batchFailed);

                    if (batchFailed == 0 && !string.IsNullOrWhiteSpace(templateFileName))
                    {
                        var acked = await this.mailQueueClient.AckMergeBatchAsync(request.JobId, templateFileName, batchId, CancellationToken.None).ConfigureAwait(false);
                        if (acked)
                        {
                            this.logger.LogInformation(
                                "Acknowledged merge batch with queue service (JobId={JobId}; TemplateFileName={TemplateFileName}; BatchId={BatchId})",
                                request.JobId,
                                templateFileName,
                                batchId);

                            try
                            {
                                // Preserve batch rows for operator preview by moving the file to a processed folder.
                                // This also prevents the worker from re-processing the batch.
                                var processedFolder = Path.Combine(batchesFolder, "processed");
                                Directory.CreateDirectory(processedFolder);
                                var destPath = Path.Combine(processedFolder, Path.GetFileName(batchPath) ?? string.Empty);
                                if (!string.IsNullOrWhiteSpace(destPath))
                                {
                                    File.Move(batchPath, destPath, overwrite: true);
                                }
                            }
                            catch
                            {
                            }
                        }
                        else
                        {
                            this.logger.LogWarning(
                                "Failed acknowledging merge batch with queue service (JobId={JobId}; TemplateFileName={TemplateFileName}; BatchId={BatchId})",
                                request.JobId,
                                templateFileName,
                                batchId);
                        }
                    }
                }

                // Consider the job complete when no new batches have been added for a short interval.
                if (execution.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);

                var latestBatches = this.ListBatchFiles(batchesFolder, request.JobId, templateFileName);
                var hasUnprocessedBatches = latestBatches.Keys.Any(batchId => !execution.ProcessedBatches.Contains(batchId));

                if (!hasUnprocessedBatches && execution.ProcessedBatches.Count > 0)
                {
                    // Mark the job as completed once there are no unprocessed batch ids remaining.
                    // Keep the worker alive for a short reopen window so callers can append another
                    // batch without needing a new StartMergeJob call.
                    if (!jobMarkedCompleted)
                    {
                        jobMarkedCompleted = true;
                        jobMarkedCompletedUtc = DateTimeOffset.UtcNow;
                        await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                        await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Completed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        var idleDuration = DateTimeOffset.UtcNow - jobMarkedCompletedUtc;
                        if (idleDuration >= MergeJobReopenWindow)
                        {
                            break;
                        }
                    }
                }
            }

            if (execution.IsPauseRequested)
            {
                this.logger.LogInformation(
                    "Merge job paused (JobId={JobId}; Total={Total}; Completed={Completed}; Failed={Failed}; LastError={LastError})",
                    request.JobId,
                    total,
                    completed,
                    failed,
                    execution.LastError);

                await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Paused, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (execution.IsCancellationRequested)
            {
                this.logger.LogInformation(
                    "Merge job cancelled (JobId={JobId}; Total={Total}; Completed={Completed}; Failed={Failed}; LastError={LastError})",
                    request.JobId,
                    total,
                    completed,
                    failed,
                    execution.LastError);

                await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Cancelled, execution.LastError, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            this.logger.LogInformation(
                "Merge job completed (JobId={JobId}; Total={Total}; Completed={Completed}; Failed={Failed}; LastError={LastError})",
                request.JobId,
                total,
                completed,
                failed,
                execution.LastError);

            await this.store.UpdateJobCountersAsync(dbPath, request.JobId, total, completed, failed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
            await this.store.UpdateJobStatusAsync(dbPath, request.JobId, MergeJobStatus.Completed, execution.LastError, CancellationToken.None).ConfigureAwait(false);
        }

        private string BuildMailQueueQueueFailureReason()
        {
            if (string.IsNullOrWhiteSpace(this.mailQueueOptions.Address))
            {
                return "MailQueue is not configured. Configure MailQueue:Address.";
            }

            return "Failed to queue mail to MailQueueNet. Check MailForge and MailQueueNet logs for details.";
        }

        private void ApplyAttachmentTokens(System.Net.Mail.MailMessage message, IReadOnlyList<MailQueueNet.Grpc.AttachmentTokenRef> tokenRefs)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (tokenRefs == null || tokenRefs.Count == 0)
            {
                return;
            }

            // The protobuf conversion layer reads these headers and serialises them into attachment_tokens.
            foreach (var tokenRef in tokenRefs)
            {
                if (tokenRef == null || string.IsNullOrWhiteSpace(tokenRef.Token))
                {
                    continue;
                }

                try
                {
                    message.Headers.Add("X-Attachment-Token", tokenRef.Token);
                    message.Headers.Add("X-Attachment-Token-FileName", tokenRef.FileName ?? string.Empty);
                    message.Headers.Add("X-Attachment-Token-ContentType", tokenRef.ContentType ?? string.Empty);
                    message.Headers.Add("X-Attachment-Token-ContentId", tokenRef.ContentId ?? string.Empty);
                    message.Headers.Add("X-Attachment-Token-Inline", tokenRef.Inline ? "1" : "0");
                }
                catch
                {
                }
            }
        }

        private void TryStampMergeHeaders(System.Net.Mail.MailMessage message, string mergeId, string batchId)
        {
            if (message == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(mergeId))
            {
                try
                {
                    message.Headers["X-MailMerge-Id"] = mergeId;
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(batchId))
            {
                try
                {
                    message.Headers["X-MailMerge-BatchId"] = batchId;
                }
                catch
                {
                }
            }
        }

        private async Task<(long Accepted, long Failed)> QueueAndDisposeAsync(IReadOnlyList<System.Net.Mail.MailMessage> messages, string mergeId, int batchId, CancellationToken cancellationToken)
        {
            if (messages == null || messages.Count == 0)
            {
                return (0, 0);
            }

            try
            {
                var result = await this.mailQueueClient.QueueBulkAsync(messages, cancellationToken).ConfigureAwait(false);
                return (result.Accepted, result.Failed);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "QueueBulkAsync failed (MergeId={MergeId}; BatchId={BatchId}; MessageCount={MessageCount})", mergeId, batchId, messages.Count);
                return (0, messages.Count);
            }
            finally
            {
                foreach (var msg in messages)
                {
                    try
                    {
                        msg.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private string ResolveBatchesFolder()
        {
            var configured = Environment.GetEnvironmentVariable("MAILFORGE_BATCH_FOLDER");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    Directory.CreateDirectory(configured);
                    return configured;
                }
                catch (UnauthorizedAccessException ex)
                {
                    var fallbackFolder = Path.Combine(Path.GetTempPath(), "MailForge", "batches");
                    this.logger.LogWarning(ex, "Batch folder '{BatchFolder}' is not writable. Falling back to '{FallbackBatchFolder}'.", configured, fallbackFolder);

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

        private IReadOnlyDictionary<int, string> ListBatchFiles(string batchesFolder, string mergeId, string templateFileName)
        {
            var results = new Dictionary<int, string>();
            try
            {
                if (!Directory.Exists(batchesFolder))
                {
                    return results;
                }

                var safeTemplateName = Path.GetFileName(templateFileName ?? string.Empty);
                var prefix = string.IsNullOrWhiteSpace(safeTemplateName)
                    ? mergeId + "__"
                    : mergeId + "__" + safeTemplateName + "__";

                foreach (var file in Directory.GetFiles(batchesFolder, "*.jsonl"))
                {
                    var name = Path.GetFileName(file) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var tail = name.Substring(prefix.Length);
                    if (!tail.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    tail = tail.Substring(0, tail.Length - ".jsonl".Length);

                    if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchId))
                    {
                        continue;
                    }

                    results[batchId] = file;
                }
            }
            catch
            {
            }

            return results;
        }

        /// <inheritdoc/>
        public Task<CancelMergeJobReply> CancelAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Task.FromResult(new CancelMergeJobReply
                {
                    Success = false,
                    Message = "Job id is required.",
                });
            }

            if (this.runningJobs.TryGetValue(jobId, out var execution))
            {
                execution.Cancel();
            }

            return Task.FromResult(new CancelMergeJobReply
            {
                Success = true,
                Message = "Cancellation requested.",
            });
        }

        /// <inheritdoc/>
        public Task<GetMergeJobStatusReply> GetStatusAsync(string jobId, CancellationToken cancellationToken)
        {
            return this.GetStatusAsync(jobId, jobWorkRoot: null, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<GetMergeJobStatusReply> GetStatusAsync(string jobId, string? jobWorkRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return new GetMergeJobStatusReply
                {
                    JobId = string.Empty,
                    Status = MergeJobStatus.Pending.ToString(),
                    UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
            }

            if (this.runningJobs.TryGetValue(jobId, out var running))
            {
                try
                {
                    var snapshot = await this.store.TryReadJobAsync(running.DbPath, jobId, cancellationToken).ConfigureAwait(false);
                    if (snapshot != null)
                    {
                        return ToReply(snapshot);
                    }
                }
                catch
                {
                }

                return new GetMergeJobStatusReply
                {
                    JobId = jobId,
                    Status = MergeJobStatus.Running.ToString(),
                    UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
            }

            if (!string.IsNullOrWhiteSpace(jobWorkRoot))
            {
                var dbPath = SqliteMergeJobStore.GetJobDatabasePath(jobWorkRoot, jobId);
                var snapshot = await this.store.TryReadJobAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
                if (snapshot != null)
                {
                    return ToReply(snapshot);
                }
            }

            // When callers omit job_work_root, attempt to locate the per-job database under configured roots.
            var resolvedDbPath = SqliteMergeJobStore.TryResolveJobDatabasePath(jobId, this.options.JobWorkRoots);
            if (!string.IsNullOrWhiteSpace(resolvedDbPath))
            {
                var snapshot = await this.store.TryReadJobAsync(resolvedDbPath, jobId, cancellationToken).ConfigureAwait(false);
                if (snapshot != null)
                {
                    return ToReply(snapshot);
                }
            }

            return new GetMergeJobStatusReply
            {
                JobId = jobId,
                Status = MergeJobStatus.Pending.ToString(),
                UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
        }

        private string? ResolveJobWorkRoot(string jobId, string? requestedJobWorkRoot)
        {
            _ = jobId;

            var configuredRoots = (this.options.JobWorkRoots ?? Array.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToArray();

            if (configuredRoots.Length == 0)
            {
                return string.IsNullOrWhiteSpace(requestedJobWorkRoot) ? null : requestedJobWorkRoot;
            }

            // Do not require callers to provide the job work root. When supplied, only honour it if it matches
            // one of the configured roots.
            if (!string.IsNullOrWhiteSpace(requestedJobWorkRoot) && configuredRoots.Any(r => string.Equals(r, requestedJobWorkRoot, PathComparison)))
            {
                return requestedJobWorkRoot;
            }

            return configuredRoots[0];
        }

        private string? TryResolveJobFolder(string mergeId, string? dbPath, string? jobWorkRoot)
        {
            if (!string.IsNullOrWhiteSpace(jobWorkRoot))
            {
                return SqliteMergeJobStore.GetJobFolder(jobWorkRoot, mergeId);
            }

            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(dbPath);
            }
            catch
            {
                return null;
            }
        }

        private static GetMergeJobStatusReply ToReply(MergeJobSnapshot snapshot)
        {
            return new GetMergeJobStatusReply
            {
                JobId = snapshot.JobId,
                Status = snapshot.Status.ToString(),
                Total = snapshot.Total,
                Completed = snapshot.Completed,
                Failed = snapshot.Failed,
                LastError = snapshot.LastError ?? string.Empty,
                UpdatedUtc = snapshot.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        }

        private static string? TryGetRecipientAddress(string jsonLine, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                error = "Row is empty.";
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Row JSON must be an object.";
                    return null;
                }

                if (!TryGetStringProperty(doc.RootElement, "Email", out var email) || string.IsNullOrWhiteSpace(email))
                {
                    error = "Row is missing required 'Email' property.";
                    return null;
                }

                try
                {
                    _ = new System.Net.Mail.MailAddress(email);
                    return email;
                }
                catch (FormatException)
                {
                    error = "Row has invalid recipient email address.";
                    return null;
                }
            }
            catch (JsonException)
            {
                error = "Row contains invalid JSON.";
                return null;
            }
        }

        private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
        {
            value = null;

            foreach (var prop in element.EnumerateObject())
            {
                if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    value = prop.Value.GetString();
                    return true;
                }

                value = prop.Value.GetRawText();
                return true;
            }

            return false;
        }

        private sealed class JobExecution
        {
            private readonly CancellationTokenSource cts;
            private int pauseRequested;

            public JobExecution(string jobId, string dbPath, string jobWorkRoot)
            {
                this.JobId = jobId;
                this.DbPath = dbPath;
                this.JobWorkRoot = jobWorkRoot;
                this.cts = new CancellationTokenSource();
            }

            public string JobId { get; }

            public string DbPath { get; }

            public string JobWorkRoot { get; }

            public Task? Task { get; set; }

            public string? LastError { get; set; }

            public HashSet<int> ProcessedBatches { get; } = new HashSet<int>();

            public Dictionary<int, int> SeenBatchIds { get; } = new Dictionary<int, int>();

            public Dictionary<int, long> BatchCompletedById { get; } = new Dictionary<int, long>();

            public Dictionary<int, long> BatchFailedById { get; } = new Dictionary<int, long>();

            public bool IsCancellationRequested
            {
                get
                {
                    return this.cts.IsCancellationRequested;
                }
            }

            public bool IsPauseRequested
            {
                get
                {
                    return Interlocked.CompareExchange(ref this.pauseRequested, 0, 0) == 1;
                }
            }

            public void RequestPause()
            {
                Interlocked.Exchange(ref this.pauseRequested, 1);
            }

            public void Cancel()
            {
                this.cts.Cancel();
            }

        }

    }
}
