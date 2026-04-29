// <copyright file="Coordinator.cs" company="IBC Digital">
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
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Mail;
    using System.Net.Mime;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailForge.Grpc;
    using MailQueueNet.Common.FileExtensions;
    using MailQueueNet.Service.Core.Telemetry;
    using MailQueueNet.Service.Utilities;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    public class Coordinator
    {
        private const string AttemptHeaderName = "X-Attempt-Count";
        private const string ClientIdHeaderName = "X-Client-ID";
        private const string DefaultStateDbFileName = "dispatcher_state.db";
        private const int MaxReasonLength = 200;
        private const string MissingTokenFailFastConfigKey = "Attachments:FailFastOnMissingTokens";
        private const string TokenInlineAsLinkedResourcesConfigKey = "Attachments:TokenInlineAsLinkedResources";

        private static readonly TimeSpan MergeFolderPollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AttachmentCleanupInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MergePumpInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MergeReopenWindow = TimeSpan.FromMinutes(60);

        private readonly ILogger sentLog;
        private readonly ILogger failedLog;

        private readonly object actionMonitor = new object();
        private readonly object failedFnLock = new object();
        private readonly object mergeGate = new object();
        private readonly object stateDbGate = new object();

        private readonly IMailForgeDispatcher dispatcher;
        private readonly IStagingMailRouter stagingMailRouter;
        private readonly SqliteDispatcherStateStore stateStore;

        private readonly IAttachmentIndexNotifier attachmentIndexNotifier;

        private readonly ConcurrentDictionary<string, string> lastErrorByFile = new();
        private readonly ConcurrentDictionary<string, bool> activeMergePumps = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTimeOffset> mergeClosedUtc = new(StringComparer.OrdinalIgnoreCase);

        private ILogger logger;
        private IConfiguration configuration;
        private Grpc.Settings settings;
        private Grpc.MailSettings mailSettings;
        private DateTime? pausedUntilUtc;
        private string pausedByUser;
        private DateTime? pausedAtUtc;

        private string? resolvedStateDbPath;
        private bool stateDbFallbackLogged;

        private int mailIdCounter = 0;
        private int concurrentWorkers = 0;
        private List<string> fileNameList;
        private ConcurrentDictionary<string, bool> sendingFileNames;
        private Dictionary<string, int> failedFileNameCounter;

        private DateTimeOffset lastAttachmentCleanupUtc = DateTimeOffset.UtcNow;

        public Coordinator(ILogger<Coordinator> logger, ILoggerFactory loggerFactory, IConfiguration configuration, IMailForgeDispatcher dispatcher, IStagingMailRouter stagingMailRouter, SqliteDispatcherStateStore stateStore, IAttachmentIndexNotifier attachmentIndexNotifier)
        {
            this.logger = logger;
            this.sentLog = loggerFactory.CreateLogger("MailSent");
            this.failedLog = loggerFactory.CreateLogger("MailFailed");
            this.configuration = configuration;
            this.dispatcher = dispatcher;
            this.stagingMailRouter = stagingMailRouter;
            this.stateStore = stateStore;
            this.attachmentIndexNotifier = attachmentIndexNotifier ?? NullAttachmentIndexNotifier.Instance;
            this.settings = SettingsController.GetSettings(this.configuration);
            this.mailSettings = SettingsController.GetMailSettings(this.configuration);
        }

        public bool ThereIsAFreeWorker => this.settings.MaximumConcurrentWorkers <= 0 || this.concurrentWorkers < this.settings.MaximumConcurrentWorkers;

        public bool IsPaused => this.pausedUntilUtc.HasValue && this.pausedUntilUtc.Value > DateTime.UtcNow;

        public (bool IsPaused, string PausedBy, DateTime? PausedAtUtc, DateTime? AutoResumeUtc) GetPauseStatus() => (this.IsPaused, this.pausedByUser, this.pausedAtUtc, this.pausedUntilUtc);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "This public helper is kept with the public coordinator API.")]
        public static string GetLongDesc(Grpc.MailMessage message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            var to = message.To == null ? string.Empty : string.Join(",", message.To);
            var cc = message.Cc == null ? string.Empty : string.Join(",", message.Cc);
            var bcc = message.Bcc == null ? string.Empty : string.Join(",", message.Bcc);

            return $"From={message.From}; To={to}; Cc={cc}; Bcc={bcc}; Subject='{message.Subject}'";
        }

        public void RefreshSettings()
        {
            this.settings = SettingsController.GetSettings(this.configuration);
            this.mailSettings = SettingsController.GetMailSettings(this.configuration);
        }

        public bool TryPause(string user, int requestedMinutes, int maxMinutes, out int effectiveMinutes, out DateTime? autoResumeUtc)
        {
            if (requestedMinutes <= 0)
            {
                requestedMinutes = 1;
            }

            if (requestedMinutes > maxMinutes && maxMinutes > 0)
            {
                requestedMinutes = maxMinutes;
            }

            effectiveMinutes = requestedMinutes;
            this.pausedUntilUtc = DateTime.UtcNow.AddMinutes(requestedMinutes);
            this.pausedByUser = user;
            this.pausedAtUtc = DateTime.UtcNow;
            autoResumeUtc = this.pausedUntilUtc;
            return true;
        }

        public bool Resume(string user)
        {
            if (!this.IsPaused)
            {
                return false;
            }

            this.pausedUntilUtc = null;
            this.pausedByUser = null;
            this.pausedAtUtc = null;
            lock (this.actionMonitor)
            {
                Monitor.PulseAll(this.actionMonitor);
            }

            return true;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            this.fileNameList = new List<string>();
            this.sendingFileNames = new ConcurrentDictionary<string, bool>();
            this.failedFileNameCounter = new Dictionary<string, int>();

            cancellationToken.Register(() =>
            {
                lock (this.actionMonitor)
                {
                    Monitor.Pulse(this.actionMonitor);
                }
            });

            var lastMergePollUtc = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastMergePollUtc >= MergeFolderPollInterval)
                {
                    lastMergePollUtc = DateTime.UtcNow;
                    await this.TryDispatchMergeJobsAsync(cancellationToken).ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow - this.lastAttachmentCleanupUtc >= AttachmentCleanupInterval)
                {
                    this.lastAttachmentCleanupUtc = DateTimeOffset.UtcNow;
                    this.TryCleanupAttachments();
                }

                if (this.IsPaused)
                {
                    var autoResumed = this.CheckAutoResume(out var _);
                    if (!autoResumed && this.IsPaused)
                    {
                        lock (this.actionMonitor)
                        {
                            Monitor.Wait(this.actionMonitor, 2000);
                        }

                        continue;
                    }
                }

                while (!this.ThereIsAFreeWorker && !cancellationToken.IsCancellationRequested)
                {
                    this.logger?.LogTrace("Waiting for a free worker...");
                    lock (this.actionMonitor)
                    {
                        if (this.ThereIsAFreeWorker || cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        Monitor.Wait(this.actionMonitor, 60 * 60 * 1000);
                    }
                }

                this.logger?.LogTrace("A worker is available");

                if (this.fileNameList.Count == 0)
                {
                    string queuePath = this.settings.QueueFolder;
                    try
                    {
                        queuePath = Files.MapPath(queuePath);
                    }
                    catch
                    {
                    }

                    try
                    {
                        this.fileNameList = new List<string>(Directory.GetFiles(queuePath, "*.mail"));
                        this.fileNameList.Sort();
                    }
                    catch
                    {
                    }
                }

                if (this.fileNameList.Count == 0)
                {
                    this.logger?.LogDebug("There is no mail in queue");
                    lock (this.actionMonitor)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        this.logger?.LogDebug("Waiting for mail to get in the queue...");
                        Monitor.Wait(this.actionMonitor, (int)(this.settings.SecondsUntilFolderRefresh * 1000f));
                    }
                }

                string nextFileName = null;

                if (this.fileNameList.Count > 0)
                {
                    this.logger?.LogDebug("Picking a mail from the queue...");
                    for (var i = 0; i < this.fileNameList.Count; i++)
                    {
                        if (!this.sendingFileNames.ContainsKey(this.fileNameList[i]))
                        {
                            nextFileName = this.fileNameList[i];
                            this.fileNameList.RemoveAt(i);
                            break;
                        }
                    }
                }

                if (nextFileName != null)
                {
                    this.sendingFileNames[nextFileName] = true;
                    await this.SendMailAsync(nextFileName).ConfigureAwait(false);
                }
                else
                {
                    lock (this.actionMonitor)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        Monitor.Wait(this.actionMonitor, 1000);
                    }
                }
            }

            while (this.concurrentWorkers > 0)
            {
                this.logger?.LogDebug("Waiting for workers to finish...");
                lock (this.actionMonitor)
                {
                    Monitor.Wait(this.actionMonitor, 1000);
                }
            }

            this.logger?.LogInformation("Done.");
        }

        public void ContinueSendingEmails()
        {
            lock (this.actionMonitor)
            {
                Monitor.Pulse(this.actionMonitor);
            }
        }

        public void AddMail(Grpc.MailMessage message, Grpc.MailSettings settings = null)
        {
            SetAttemptHeader(message, GetAttemptHeader(message));

            // Phase 2: increment reference counts for any uploaded attachments.
            this.TryAddAttachmentReferences(message);

            this.AddMail(new Grpc.MailMessageWithSettings { Message = message, Settings = settings });
        }

        public void AddMail(Grpc.MailMessageWithSettings message)
        {
            SetAttemptHeader(message, GetAttemptHeader(message.Message));

            // Phase 2: increment reference counts for any uploaded attachments.
            this.TryAddAttachmentReferences(message.Message);

            this.EnsureQueueFoldersExist();

            string tempPath = Files.CreateEmptyTempFile();
            if (tempPath == null)
            {
                this.logger?.LogError("Failed to create a temp file. Check TEMP/TMP env vars and container filesystem permissions.");
                return;
            }

            if (FileUtils.WriteMailToFile(message, tempPath))
            {
                string queuePath = this.settings.QueueFolder;
                try
                {
                    queuePath = Files.MapPath(queuePath);
                }
                catch
                {
                }

                bool success = false;
                string destPath = Path.Combine(queuePath, DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Interlocked.Increment(ref this.mailIdCounter).ToString().PadLeft(8, '0') + ".mail");
                while (true)
                {
                    var originalSendingState = this.sendingFileNames.ContainsKey(destPath);

                    if (File.Exists(destPath))
                    {
                        destPath = Path.Combine(queuePath, DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Interlocked.Increment(ref this.mailIdCounter).ToString().PadLeft(8, '0') + ".mail");
                        continue;
                    }

                    try
                    {
                        this.sendingFileNames[destPath] = true;
                        File.Move(tempPath, destPath, false);
                        success = true;
                        break;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        this.logger?.LogError(ex, "Queue folder not found. QueueFolder='{QueueFolder}' ResolvedQueueFolder='{ResolvedQueueFolder}'", this.settings.QueueFolder, queuePath);
                        break;
                    }
                    catch (PathTooLongException ex)
                    {
                        this.logger?.LogError(ex, "Exception thrown for AddMail");
                        break;
                    }
                    catch (FileNotFoundException ex)
                    {
                        this.logger?.LogError(ex, "Exception thrown for AddMail");
                        break;
                    }
                    catch (IOException ex)
                    {
                        this.logger?.LogError(ex, "Exception thrown for AddMail");
                        break;
                    }
                    finally
                    {
                        if (!originalSendingState)
                        {
                            this.sendingFileNames.TryRemove(destPath, out originalSendingState);
                        }
                    }
                }

                if (success)
                {
                    this.ContinueSendingEmails();
                }
            }
        }

        private void EnsureQueueFoldersExist()
        {
            var rawQueue = this.settings.QueueFolder;
            var rawFailed = this.settings.FailedFolder;
            var rawMerge = this.settings.MailMergeQueueFolder;

            string resolvedQueue = rawQueue;
            string resolvedFailed = rawFailed;
            string resolvedMerge = rawMerge;

            try
            {
                resolvedQueue = Files.MapPath(rawQueue);
            }
            catch
            {
            }

            try
            {
                resolvedFailed = Files.MapPath(rawFailed);
            }
            catch
            {
            }

            try
            {
                resolvedMerge = Files.MapPath(rawMerge);
            }
            catch
            {
            }

            try
            {
                if (!Directory.Exists(resolvedQueue))
                {
                    Directory.CreateDirectory(resolvedQueue);
                    this.logger?.LogWarning("Queue folder did not exist and was created. QueueFolder='{QueueFolder}' ResolvedQueueFolder='{ResolvedQueueFolder}'", rawQueue, resolvedQueue);
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Queue folder is missing or cannot be created. QueueFolder='{QueueFolder}' ResolvedQueueFolder='{ResolvedQueueFolder}'", rawQueue, resolvedQueue);
            }

            try
            {
                if (!Directory.Exists(resolvedFailed))
                {
                    Directory.CreateDirectory(resolvedFailed);
                    this.logger?.LogWarning("Failed folder did not exist and was created. FailedFolder='{FailedFolder}' ResolvedFailedFolder='{ResolvedFailedFolder}'", rawFailed, resolvedFailed);
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Failed folder is missing or cannot be created. FailedFolder='{FailedFolder}' ResolvedFailedFolder='{ResolvedFailedFolder}'", rawFailed, resolvedFailed);
            }

            try
            {
                if (!Directory.Exists(resolvedMerge))
                {
                    Directory.CreateDirectory(resolvedMerge);
                    this.logger?.LogWarning("Mail merge queue folder did not exist and was created. MailMergeQueueFolder='{MailMergeQueueFolder}' ResolvedMailMergeQueueFolder='{ResolvedMailMergeQueueFolder}'", rawMerge, resolvedMerge);
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Mail merge queue folder is missing or cannot be created. MailMergeQueueFolder='{MailMergeQueueFolder}' ResolvedMailMergeQueueFolder='{ResolvedMailMergeQueueFolder}'", rawMerge, resolvedMerge);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Keeping attempt-header helpers near queue file operations preserves readability in this large coordinator.")]
        private static void SetAttemptHeader(Grpc.MailMessageWithSettings wrapper, int attempt)
        {
            if (wrapper?.Message == null)
            {
                return;
            }

            var header = wrapper.Message.Headers.FirstOrDefault(h => string.Equals(h.Name, AttemptHeaderName, StringComparison.OrdinalIgnoreCase));
            if (header == null)
            {
                wrapper.Message.Headers.Add(new Grpc.Header { Name = AttemptHeaderName, Value = attempt.ToString(CultureInfo.InvariantCulture) });
            }
            else
            {
                header.Value = attempt.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void SetAttemptHeader(Grpc.MailMessage message, int attempt)
        {
            if (message == null)
            {
                return;
            }

            var header = message.Headers.FirstOrDefault(h => string.Equals(h.Name, AttemptHeaderName, StringComparison.OrdinalIgnoreCase));
            if (header == null)
            {
                message.Headers.Add(new Grpc.Header { Name = AttemptHeaderName, Value = attempt.ToString(CultureInfo.InvariantCulture) });
            }
            else
            {
                header.Value = attempt.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static int GetAttemptHeader(Grpc.MailMessage message)
        {
            if (message == null)
            {
                return 0;
            }

            var header = message.Headers.FirstOrDefault(h => string.Equals(h.Name, AttemptHeaderName, StringComparison.OrdinalIgnoreCase));
            if (header == null)
            {
                return 0;
            }

            if (int.TryParse(header.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            return 0;
        }

        private static void UpdateAttemptHeaderOnDisk(string path, int attempt)
        {
            try
            {
                try
                {
                    var wrapper = FileUtils.ReadMailFromFile(path);
                    SetAttemptHeader(wrapper, attempt);
                    FileUtils.WriteMailToFile(wrapper, path);
                    return;
                }
                catch
                {
                    var plain = FileUtils.ReadMailNoSettingsFromFile(path);
                    SetAttemptHeader(plain, attempt);
                    FileUtils.WriteMailToFile(plain, path);
                }
            }
            catch
            {
            }
        }

        private bool CheckAutoResume(out DateTime? resumedAt)
        {
            resumedAt = null;
            if (this.pausedUntilUtc.HasValue && this.pausedUntilUtc.Value <= DateTime.UtcNow)
            {
                this.pausedUntilUtc = null;
                this.pausedByUser = null;
                this.pausedAtUtc = null;
                resumedAt = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        private async Task SendMailAsync(string fileName)
        {
            bool workerInUse = false;
            Grpc.MailMessageWithSettings transportMessage = null;
            Grpc.MailSettings effectiveMailSettings = null;
            System.Net.Mail.MailMessage message = null;
            string clientId = "unknown";

            var attachmentTracker = new AttachmentTokenTracker();

            try
            {
                this.logger?.LogTrace("Reading mail from " + fileName);

                try
                {
                    transportMessage = FileUtils.ReadMailFromFile(fileName);
                    effectiveMailSettings = transportMessage.Settings;
                    message = transportMessage.Message.ToSystemType();

                    // Phase 2: resolve uploaded token attachments into local on-disk files (added to System.Net.Mail message).
                    // If tokens are missing/not-ready at send time, this should be treated as a failure.
                    var tokenResolution = this.TryAttachTokenAttachmentsToMailMessage(transportMessage.Message, message, attachmentTracker);
                    if (!tokenResolution.Success)
                    {
                        this.lastErrorByFile[fileName] = this.ToSingleLineReason(tokenResolution.Message);

                        var failFast = this.configuration.GetValue(MissingTokenFailFastConfigKey, true);
                        if (failFast)
                        {
                            this.logger?.LogWarning("Fail-fast: {Reason} (File={File})", tokenResolution.Message, fileName);
                            this.MarkFailed(fileName, message, clientId);

                            // Terminal: release any references we already resolved for this attempt.
                            this.TryReleaseTrackedAttachments(attachmentTracker);
                            return;
                        }

                        // Not terminal: count as a failed attempt but allow retries.
                        this.MarkFailed(fileName, message, clientId);
                        return;
                    }

                    var existingAttempts = GetAttemptHeader(transportMessage.Message);
                    if (existingAttempts > 0)
                    {
                        lock (this.failedFnLock)
                        {
                            if (!this.failedFileNameCounter.ContainsKey(fileName))
                            {
                                this.failedFileNameCounter[fileName] = existingAttempts;
                            }
                        }
                    }

                    var idHeader = transportMessage.Message.Headers.FirstOrDefault(h => string.Equals(h.Name, ClientIdHeaderName, StringComparison.OrdinalIgnoreCase));
                    clientId = idHeader?.Value ?? clientId;
                }
                catch (FileNotFoundException ex)
                {
                    this.lastErrorByFile[fileName] = this.ToSingleLineReason(ex.Message);
                    this.logger?.LogWarning($"Failed reading {fileName}, file not found.");
                    message?.Dispose();

                    // If file vanished, treat as terminal for attachment references.
                    this.TryReleaseTrackedAttachments(attachmentTracker);

                    this.MarkFailed(fileName, message, clientId);
                    return;
                }

                Interlocked.Increment(ref this.concurrentWorkers);
                workerInUse = true;
                var logDesc = $"From={message.From}; To={string.Join(",", message.To.Cast<System.Net.Mail.MailAddress>().Select(a => a.Address))}; Subject='{message.Subject}'";
                this.logger?.LogDebug($"Sending {fileName} task to worker (ClientId={clientId}; {logDesc})");

                if (effectiveMailSettings == null || effectiveMailSettings.IsEmpty())
                {
                    effectiveMailSettings = this.mailSettings;
                }

                var sw = Stopwatch.StartNew();
                var success = this.stagingMailRouter.ShouldRoute(clientId, effectiveMailSettings)
                    ? await this.stagingMailRouter.SendAsync(message, clientId, effectiveMailSettings, CancellationToken.None).ConfigureAwait(false)
                    : await SenderFactory.SendMailAsync(message, effectiveMailSettings).ConfigureAwait(false);
                sw.Stop();
                Metrics.RecordSendLatency(sw.Elapsed.TotalMilliseconds);

                if (!success)
                {
                    this.logger?.LogWarning($"No mail server name, skipping {fileName} (ClientId={clientId}; {logDesc})");
                    message?.Dispose();

                    // Not terminal: keep references, message will be attempted again.
                    this.MarkSkipped(fileName);
                }
                else
                {
                    this.MarkSent(fileName, message, clientId);

                    // Sent is terminal: release token references.
                    this.TryReleaseTrackedAttachments(attachmentTracker);

                    Metrics.IncSent();
                }

                this.logger?.LogTrace($"Releasing worker from {fileName} task ({logDesc})");
                Interlocked.Decrement(ref this.concurrentWorkers);
                workerInUse = false;
                lock (this.actionMonitor)
                {
                    Monitor.Pulse(this.actionMonitor);
                }
            }
            catch (Exception ex)
            {
                this.lastErrorByFile[fileName] = this.ToSingleLineReason(ex.Message);
                this.logger?.LogError(ex, $"Exception thrown for {fileName} (ClientId={clientId})");
                this.logger?.LogWarning($"Task failed for {fileName} (ClientId={clientId})");
                Metrics.IncFailed();

                if (workerInUse)
                {
                    Interlocked.Decrement(ref this.concurrentWorkers);
                }

                try
                {
                    var terminal = this.TryMarkFailedAndReturnTerminal(fileName, message, clientId);
                    if (terminal)
                    {
                        this.TryReleaseTrackedAttachments(attachmentTracker);
                    }
                }
                catch
                {
                }

                lock (this.actionMonitor)
                {
                    Monitor.Pulse(this.actionMonitor);
                }
            }
        }

        private void MarkSkipped(string fileName)
        {
            this.sendingFileNames.TryRemove(fileName, out _);
        }

        private void MarkSent(string fileName, System.Net.Mail.MailMessage message, string clientId)
        {
            var filesToDelete = message?.Attachments
                ?.Where(x => x is AttachmentEx xx && xx.ShouldDeleteFile && x.ContentStream is FileStream)
                ?.Select(x => ((FileStream)x.ContentStream).Name)
                ?.ToList();

            var subject = message?.Subject ?? string.Empty;
            var from = message?.From?.Address ?? string.Empty;
            var to = message != null ? string.Join(",", message.To.Cast<System.Net.Mail.MailAddress>().Select(a => a.Address)) : string.Empty;

            message?.Dispose();

            try
            {
                File.Delete(fileName);
            }
            catch
            {
            }

            this.sendingFileNames.TryRemove(fileName, out _);

            if (filesToDelete != null)
            {
                foreach (var fn in filesToDelete)
                {
                    try
                    {
                        File.Delete(fn);
                    }
                    catch
                    {
                    }
                }
            }

            this.sentLog.LogInformation("Mail sent (ClientId={ClientId}; From={From}; To={To}; Subject='{Subject}'; File={File})", clientId, from, to, subject, fileName);
        }

        private void MarkFailed(string fileName, System.Net.Mail.MailMessage message, string clientId)
        {
            var filesToDelete = message?.Attachments
                ?.Where(x => x is AttachmentEx xx && xx.ShouldDeleteFile && x.ContentStream is FileStream)
                ?.Select(x => ((FileStream)x.ContentStream).Name)
                ?.ToList();

            var subject = message?.Subject ?? string.Empty;
            var from = message?.From?.Address ?? string.Empty;
            var to = message != null ? string.Join(",", message.To.Cast<System.Net.Mail.MailAddress>().Select(a => a.Address)) : string.Empty;

            message?.Dispose();
            bool shouldRemoveFile = false;
            int currentAttempt = 0;
            lock (this.failedFnLock)
            {
                if (this.failedFileNameCounter.ContainsKey(fileName))
                {
                    this.failedFileNameCounter[fileName]++;
                }
                else
                {
                    this.failedFileNameCounter[fileName] = 1;
                }

                currentAttempt = this.failedFileNameCounter[fileName];

                try
                {
                    UpdateAttemptHeaderOnDisk(fileName, currentAttempt);
                }
                catch
                {
                }

                if (this.failedFileNameCounter[fileName] >= this.settings.MaximumFailureRetries)
                {
                    shouldRemoveFile = true;
                }
            }

            string finalPath = fileName;
            if (shouldRemoveFile)
            {
                string failedPath = this.settings.FailedFolder;
                try
                {
                    failedPath = Files.MapPath(failedPath);
                }
                catch
                {
                }

                string file = Path.Combine(failedPath, Path.GetFileName(fileName));
                try
                {
                    File.Move(fileName, file);
                }
                catch
                {
                    try
                    {
                        file = Path.Combine(failedPath, DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N") + ".mail");
                        File.Move(fileName, file);
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(fileName);
                        }
                        catch
                        {
                        }
                    }
                }

                try
                {
                    UpdateAttemptHeaderOnDisk(file, currentAttempt);
                }
                catch
                {
                }

                lock (this.failedFnLock)
                {
                    this.failedFileNameCounter.Remove(fileName);
                }

                if (filesToDelete != null)
                {
                    foreach (var fn in filesToDelete)
                    {
                        try
                        {
                            File.Delete(fn);
                        }
                        catch
                        {
                        }
                    }
                }

                finalPath = file;
            }

            this.sendingFileNames.TryRemove(fileName, out _);
            this.lastErrorByFile.TryGetValue(fileName, out var reasonRaw);
            var reason = this.ToSingleLineReason(reasonRaw);
            this.failedLog.LogWarning("Mail delivery failed (ClientId={ClientId}; Attempts={Attempts}; From={From}; To={To}; Subject='{Subject}'; File={File}; Reason={Reason})", clientId, currentAttempt, from, to, subject, finalPath, reason);
            this.lastErrorByFile.TryRemove(fileName, out _);
        }

        private void TryCleanupAttachments()
        {
            try
            {
                var options = this.BuildAttachmentStoreOptions();
                if (options == null)
                {
                    return;
                }

                var store = new DiskAttachmentStore(options, this.attachmentIndexNotifier);
                store.Cleanup();
            }
            catch
            {
            }
        }

        private AttachmentStoreOptions? BuildAttachmentStoreOptions()
        {
            var root = AppContext.BaseDirectory;
            var configured = this.configuration["Attachments:Path"];
            var baseFolder = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(root, "Attachments")
                : (Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured));

            var maxBytes = AttachmentStoreOptions.DefaultMaxUploadBytes;
            var maxConfig = this.configuration["Attachments:MaxBytes"];
            if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
            {
                maxBytes = configuredMax;
            }

            var unrefTtl = AttachmentStoreOptions.DefaultUnreferencedTtl;
            var unrefTtlCfg = this.configuration["Attachments:UnreferencedTtlMinutes"];
            if (!string.IsNullOrWhiteSpace(unrefTtlCfg) && int.TryParse(unrefTtlCfg, out var mins) && mins > 0)
            {
                unrefTtl = TimeSpan.FromMinutes(mins);
            }

            var incompleteTtl = AttachmentStoreOptions.DefaultIncompleteUploadTtl;
            var incompleteTtlCfg = this.configuration["Attachments:IncompleteUploadTtlMinutes"];
            if (!string.IsNullOrWhiteSpace(incompleteTtlCfg) && int.TryParse(incompleteTtlCfg, out var incMins) && incMins > 0)
            {
                incompleteTtl = TimeSpan.FromMinutes(incMins);
            }

            return new AttachmentStoreOptions
            {
                BaseFolder = baseFolder,
                MaxUploadBytes = maxBytes,
                UnreferencedTtl = unrefTtl,
                IncompleteUploadTtl = incompleteTtl,
            };
        }

        private void TryAddAttachmentReferences(Grpc.MailMessage? message)
        {
            try
            {
                if (message == null)
                {
                    return;
                }

                if (message.AttachmentTokens == null || message.AttachmentTokens.Count == 0)
                {
                    return;
                }

                var tokens = message.AttachmentTokens
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Token))
                    .Select(t => t.Token)
                    .ToArray();

                if (tokens.Length == 0)
                {
                    return;
                }

                var mergeOwnerId = string.IsNullOrWhiteSpace(message.MergeId) ? null : message.MergeId;

                var options = this.BuildAttachmentStoreOptions();
                if (options == null)
                {
                    return;
                }

                var store = new DiskAttachmentStore(options, this.attachmentIndexNotifier);

                // Strict policy: validate before accepting message into queue so callers can get an immediate response.
                var validation = store.ValidateTokensReady(tokens);
                if (!validation.Success)
                {
                    this.logger.LogWarning(
                        "Rejecting queue request due to invalid attachment token(s). MergeOwnerId={MergeOwnerId}; MissingCount={MissingCount}; NotReadyCount={NotReadyCount}; Details={Details}",
                        mergeOwnerId ?? string.Empty,
                        validation.MissingTokens.Count,
                        validation.NotReadyTokens.Count,
                        validation.ToMessage());

                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Attachment token(s) missing or not ready: " + validation.ToMessage()));
                }

                store.AddReferences(tokens, mergeOwnerId);
            }
            catch
            {
                throw;
            }
        }

        private void TryReleaseTrackedAttachments(AttachmentTokenTracker tracker)
        {
            if (tracker == null)
            {
                return;
            }

            var tokens = tracker.GetTokens();
            if (tokens.Length == 0)
            {
                return;
            }

            try
            {
                var options = this.BuildAttachmentStoreOptions();
                if (options == null)
                {
                    return;
                }

                var store = new DiskAttachmentStore(options, this.attachmentIndexNotifier);
                store.ReleaseReferences(tokens);
            }
            catch
            {
            }
        }

        private void TryCleanupMergeAttachments(string mergeId)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return;
            }

            try
            {
                var options = this.BuildAttachmentStoreOptions();
                if (options == null)
                {
                    return;
                }

                var store = new DiskAttachmentStore(options, this.attachmentIndexNotifier);
                var deleted = store.DeleteOrphanedByMergeOwnerId(mergeId);

                if (deleted > 0)
                {
                    this.logger.LogInformation("Deleted {Deleted} orphaned attachment(s) for merge {MergeId}", deleted, mergeId);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed cleaning up merge attachments (MergeId={MergeId})", mergeId);
            }
        }

        private async Task RunMergePumpAsync(string stateDbPath, string mergeFolder, string templatePath, string templateName, string mergeId, CancellationToken cancellationToken)
        {
            if (!this.activeMergePumps.TryAdd(mergeId, true))
            {
                return;
            }

            try
            {
                this.logger.LogInformation(
                    "Merge pump started (MergeId={MergeId}; Template={Template}; Folder={Folder})",
                    mergeId,
                    templateName,
                    mergeFolder);

                var uploaded = new HashSet<int>();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var appendedInThisPoll = false;
                    var pendingBatchesInThisPoll = false;

                    GetMergeJobStatusReply status;
                    try
                    {
                        status = await this.dispatcher.GetMergeJobStatusAsync(
                            new GetMergeJobStatusRequest
                            {
                                JobId = mergeId,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex, "GetMergeJobStatus failed during pump (MergeId={MergeId})", mergeId);
                        status = null;
                    }

                    if (status != null)
                    {
                        this.logger.LogDebug(
                            "Merge status poll (MergeId={MergeId}; Status={Status}; Total={Total}; Completed={Completed}; Failed={Failed}; LastError={LastError})",
                            mergeId,
                            status.Status,
                            status.Total,
                            status.Completed,
                            status.Failed,
                            status.LastError);
                    }

                    var terminal = status != null && (string.Equals(status.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));

                    int[] batchIds;
                    try
                    {
                        batchIds = Directory.GetFiles(mergeFolder, templateName + ".*.jsonl")
                            .Select(f => TryExtractBatchId(f))
                            .Where(id => id >= 0)
                            .Distinct()
                            .OrderBy(id => id)
                            .ToArray();
                    }
                    catch
                    {
                        batchIds = Array.Empty<int>();
                    }

                    foreach (var batchId in batchIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (uploaded.Contains(batchId))
                        {
                            continue;
                        }

                        var batchFile = templatePath + "." + batchId.ToString(CultureInfo.InvariantCulture) + ".jsonl";
                        if (!File.Exists(batchFile))
                        {
                            continue;
                        }

                        string[] lines;
                        try
                        {
                            lines = await File.ReadAllLinesAsync(batchFile, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogWarning(ex, "Failed reading merge batch file (MergeId={MergeId}; File={File})", mergeId, batchFile);
                            continue;
                        }

                        var payload = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                        if (payload.Length == 0)
                        {
                            uploaded.Add(batchId);
                            continue;
                        }

                        pendingBatchesInThisPoll = true;

                        this.logger.LogInformation(
                            "Appending merge batch to worker (MergeId={MergeId}; BatchId={BatchId}; Lines={Lines})",
                            mergeId,
                            batchId,
                            payload.Length);

                        AppendMergeBatchReply appendReply;
                        try
                        {
                            appendReply = await this.dispatcher.AppendMergeBatchAsync(
                                new AppendMergeBatchRequest
                                {
                                    MergeId = mergeId,
                                    TemplateFileName = templateName,
                                    BatchId = batchId,
                                    JsonLines = { payload },
                                },
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogWarning(ex, "AppendMergeBatch failed (MergeId={MergeId}; BatchId={BatchId})", mergeId, batchId);
                            continue;
                        }

                        if (!appendReply.Success)
                        {
                            this.logger.LogWarning("AppendMergeBatch unsuccessful (MergeId={MergeId}; BatchId={BatchId}; Message={Message})", mergeId, batchId, appendReply.Message);
                            continue;
                        }

                        this.logger.LogInformation(
                            "AppendMergeBatch succeeded (MergeId={MergeId}; BatchId={BatchId}; Accepted={Accepted})",
                            mergeId,
                            batchId,
                            appendReply.Accepted);

                        uploaded.Add(batchId);
                        appendedInThisPoll = true;
                    }

                    // If MailForge reports a terminal status but we either appended a new batch or still have
                    // pending batch files on disk, do not close the pump yet. This avoids re-uploading the
                    // same batch ids across pump restarts (which causes duplicate preview rows).
                    if (terminal && (appendedInThisPoll || pendingBatchesInThisPoll))
                    {
                        await Task.Delay(MergePumpInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (terminal)
                    {
                        this.logger.LogInformation(
                            "Merge pump reached terminal status (MergeId={MergeId}; Status={Status})",
                            mergeId,
                            status?.Status ?? "Unknown");

                        // Mark closed and begin the grace period.
                        this.mergeClosedUtc[mergeId] = DateTimeOffset.UtcNow;

                        try
                        {
                            await this.stateStore.MarkMergeJobCompletedAsync(stateDbPath, mergeId, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogWarning(ex, "Failed marking merge job completed (MergeId={MergeId})", mergeId);
                        }

                        // Allow late batches for a short window, then delete template and clear state.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(MergeReopenWindow, CancellationToken.None).ConfigureAwait(false);

                                if (this.mergeClosedUtc.TryGetValue(mergeId, out var closedAt) && DateTimeOffset.UtcNow - closedAt >= MergeReopenWindow)
                                {
                                    try
                                    {
                                        var settings = SettingsController.GetSettings(this.configuration);
                                        var mergeFolderPath = settings.MailMergeQueueFolder;
                                        try
                                        {
                                            mergeFolderPath = Files.MapPath(mergeFolderPath);
                                        }
                                        catch
                                        {
                                        }

                                        var fullTemplatePath = Path.Combine(mergeFolderPath, templateName);
                                        if (File.Exists(fullTemplatePath))
                                        {
                                            File.Delete(fullTemplatePath);
                                        }

                                        try
                                        {
                                            foreach (var batchFile in Directory.GetFiles(mergeFolderPath, templateName + ".*.jsonl"))
                                            {
                                                try
                                                {
                                                    File.Delete(batchFile);
                                                }
                                                catch
                                                {
                                                }
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }
                                    catch (Exception ex2)
                                    {
                                        this.logger.LogWarning(ex2, "Failed deleting merge template (MergeId={MergeId}; Template={Template})", mergeId, templateName);
                                    }

                                    // After template deletion, purge orphaned attachments for this merge.
                                    this.TryCleanupMergeAttachments(mergeId);

                                    try
                                    {
                                        _ = await this.stateStore.TryDeleteCompletedMergeJobAsync(stateDbPath, mergeId, MergeReopenWindow, CancellationToken.None).ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                    }

                                    this.mergeClosedUtc.TryRemove(mergeId, out _);
                                }
                            }
                            catch
                            {
                            }
                        });

                        break;
                    }

                    await Task.Delay(MergePumpInterval, cancellationToken).ConfigureAwait(false);

                    // If we were previously closed but have received a new batch inside the window, allow pump to restart.
                    if (this.mergeClosedUtc.TryGetValue(mergeId, out var closedUtc) && DateTimeOffset.UtcNow - closedUtc <= MergeReopenWindow)
                    {
                        // nothing to do here; the outer dispatch loop will see new batches and keep pumping while non-terminal.
                    }
                }
            }
            finally
            {
                this.activeMergePumps.TryRemove(mergeId, out _);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Keeping merge pump helper near its only call site preserves readability in this large coordinator.")]
        private static int TryExtractBatchId(string fullPath)
        {
            // Expected suffix: ".<batchId>.jsonl"
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return -1;
            }

            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return -1;
            }

            var parts = name.Split('.');
            if (parts.Length < 3)
            {
                return -1;
            }

            var idPart = parts[^2];
            if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return -1;
            }

            return id;
        }

        private static TemplateEngine ResolveMergeTemplateEngine(Grpc.MailMessageWithSettings template)
        {
            var raw = template?.Message?.Headers?.FirstOrDefault(h => string.Equals(h?.Name, "X-MailMerge-Engine", StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return TemplateEngine.Unspecified;
            }

            if (Enum.TryParse<TemplateEngine>(raw, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            if (string.Equals(raw, "fluid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "fluid.core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "liquid", StringComparison.OrdinalIgnoreCase))
            {
                return TemplateEngine.Liquid;
            }

            if (string.Equals(raw, "handlebars", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "handlebars.net", StringComparison.OrdinalIgnoreCase))
            {
                return TemplateEngine.Handlebars;
            }

            return TemplateEngine.Unspecified;
        }

        private async Task TryDispatchMergeJobsAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var stateDbPath = this.ResolveStateDbPath();

            string rawMerge = this.settings.MailMergeQueueFolder;
            string mergeFolder = rawMerge;

            try
            {
                mergeFolder = Files.MapPath(rawMerge);
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(mergeFolder))
            {
                return;
            }

            if (!Directory.Exists(mergeFolder))
            {
                return;
            }

            string[] templates;
            try
            {
                templates = Directory.GetFiles(mergeFolder, "*.mail");
            }
            catch
            {
                return;
            }

            Array.Sort(templates, StringComparer.Ordinal);

            foreach (var templatePath in templates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var templateName = Path.GetFileName(templatePath);
                if (string.IsNullOrWhiteSpace(templateName))
                {
                    continue;
                }

                var mergeId = TryExtractMergeIdFromTemplateFileName(templateName);
                if (string.IsNullOrWhiteSpace(mergeId))
                {
                    continue;
                }

                var wasRecentlyClosed = false;

                // Reject merges that are closed beyond the reopen window.
                if (this.mergeClosedUtc.TryGetValue(mergeId, out var closedAtUtc) && DateTimeOffset.UtcNow - closedAtUtc > MergeReopenWindow)
                {
                    continue;
                }

                // If the merge was previously completed but a new batch arrived inside the reopen window, reopen it.
                if (this.mergeClosedUtc.TryGetValue(mergeId, out var recentlyClosedAtUtc) && DateTimeOffset.UtcNow - recentlyClosedAtUtc <= MergeReopenWindow)
                {
                    var hasPendingBatch = false;
                    try
                    {
                        foreach (var batchFile in Directory.GetFiles(mergeFolder, templateName + ".*.jsonl"))
                        {
                            try
                            {
                                var info = new FileInfo(batchFile);
                                if (info.Length > 0)
                                {
                                    hasPendingBatch = true;
                                    break;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }

                    if (hasPendingBatch)
                    {
                        wasRecentlyClosed = true;
                        this.mergeClosedUtc.TryRemove(mergeId, out _);
                        try
                        {
                            _ = await this.stateStore.TryReopenMergeJobAsync(stateDbPath, mergeId, MergeReopenWindow, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                        }

                        // Ensure the merge pump is active. The job may still be running in MailForge,
                        // so we do not need to dispatch a new StartMergeJob.
                        if (!this.activeMergePumps.ContainsKey(mergeId))
                        {
                            _ = Task.Run(() => this.RunMergePumpAsync(stateDbPath, mergeFolder, templatePath, templateName, mergeId, cancellationToken), CancellationToken.None);
                            this.logger.LogInformation("Merge pump restarted inside reopen window (MergeId={MergeId}; Template={Template})", mergeId, templateName);
                        }
                    }
                }

                bool alreadyDispatched;
                try
                {
                    alreadyDispatched = await this.stateStore.IsMergeJobDispatchedAsync(stateDbPath, mergeId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Failed reading merge dispatch state (MergeId={MergeId})", mergeId);
                    alreadyDispatched = false;
                }

                if (alreadyDispatched)
                {
                    continue;
                }

                var hasPendingBatchToDispatch = false;
                try
                {
                    foreach (var batchFile in Directory.GetFiles(mergeFolder, templateName + ".*.jsonl"))
                    {
                        try
                        {
                            var info = new FileInfo(batchFile);
                            if (info.Length > 0)
                            {
                                hasPendingBatchToDispatch = true;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }

                if (!hasPendingBatchToDispatch)
                {
                    continue;
                }

                MailQueueNet.Grpc.MailMessageWithSettings template;
                try
                {
                    template = FileUtils.ReadMailFromFile(templatePath);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Failed reading merge template file (MergeId={MergeId}; Template={Template})", mergeId, templateName);
                    continue;
                }

                var request = new StartMergeJobRequest
                {
                    JobId = mergeId,
                    Engine = ResolveMergeTemplateEngine(template),
                    Template = template,
                    ClientId = string.Empty,
                };

                try
                {
                    if (request.Template?.Message != null)
                    {
                        request.Template.Message.Headers.Add(new MailQueueNet.Grpc.Header
                        {
                            Name = "X-MailMerge-TemplateFileName",
                            Value = templateName,
                        });
                    }
                }
                catch
                {
                }

                StartMergeJobReply reply;
                try
                {
                    reply = await this.dispatcher.StartMergeJobAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "StartMergeJobAsync call failed (MergeId={MergeId}; Template={Template})", mergeId, templateName);
                    try
                    {
                        await this.stateStore.MarkMergeJobDispatchFailedAsync(stateDbPath, mergeId, templatePath, ex.Message, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    continue;
                }

                if (reply == null || !reply.Accepted)
                {
                    this.logger.LogWarning("Merge job not accepted (MergeId={MergeId}; Template={Template}; Message={Message})", mergeId, templateName, reply?.Message ?? string.Empty);
                    try
                    {
                        await this.stateStore.MarkMergeJobDispatchFailedAsync(stateDbPath, mergeId, templatePath, reply?.Message, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    continue;
                }

                // Start a background pump to keep pushing new batches until the job completes.
                _ = Task.Run(() => this.RunMergePumpAsync(stateDbPath, mergeFolder, templatePath, templateName, mergeId, cancellationToken), CancellationToken.None);

                // Persist dispatch state to prevent double processing.
                var lease = this.dispatcher.GetLeaseSnapshot();
                var worker = lease?.WorkerAddress ?? string.Empty;
                var fence = lease?.FenceToken ?? 0;

                try
                {
                    await this.stateStore.MarkMergeJobDispatchedAsync(stateDbPath, mergeId, templatePath, worker, fence, reply.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Failed persisting merge dispatch state (MergeId={MergeId})", mergeId);
                }

                this.logger.LogInformation("Merge job dispatched (MergeId={MergeId}; Template={Template}; Worker={Worker}; Fence={Fence})", mergeId, templateName, worker, fence);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Keeping merge dispatch helper near its only call site preserves readability in this large coordinator.")]
        private static string TryExtractMergeIdFromTemplateFileName(string templateFileName)
        {
            // Expected: yyyyMMddHHmmss_<mergeId>_<counter>.mail
            if (string.IsNullOrWhiteSpace(templateFileName))
            {
                return string.Empty;
            }

            var noExt = Path.GetFileNameWithoutExtension(templateFileName);
            if (string.IsNullOrWhiteSpace(noExt))
            {
                return string.Empty;
            }

            var parts = noExt.Split('_');
            if (parts.Length < 3)
            {
                return string.Empty;
            }

            return parts[1];
        }

        private bool TryMarkFailedAndReturnTerminal(string fileName, System.Net.Mail.MailMessage message, string clientId)
        {
            this.MarkFailed(fileName, message, clientId);

            lock (this.failedFnLock)
            {
                // If the entry was removed, it was moved to failed or deleted (terminal).
                if (!this.failedFileNameCounter.ContainsKey(fileName))
                {
                    return true;
                }

                // Otherwise it remains queued (non-terminal).
                return false;
            }
        }

        private AttachmentTokenResolutionResult TryAttachTokenAttachmentsToMailMessage(Grpc.MailMessage? transport, System.Net.Mail.MailMessage mailMessage, AttachmentTokenTracker tracker)
        {
            if (transport == null)
            {
                return AttachmentTokenResolutionResult.Successful();
            }

            if (transport.AttachmentTokens == null || transport.AttachmentTokens.Count == 0)
            {
                return AttachmentTokenResolutionResult.Successful();
            }

            var options = this.BuildAttachmentStoreOptions();
            if (options == null)
            {
                return AttachmentTokenResolutionResult.Failed("Attachment storage is not configured.");
            }

            var store = new DiskAttachmentStore(options, this.attachmentIndexNotifier);

            var missing = new List<string>();
            var notReady = new List<string>();

            var inlineAsLinkedResources = this.configuration.GetValue(TokenInlineAsLinkedResourcesConfigKey, true);
            var inlineResources = new List<(Grpc.AttachmentTokenRef TokenRef, string Path)>();

            foreach (var tokenRef in transport.AttachmentTokens)
            {
                if (tokenRef == null || string.IsNullOrWhiteSpace(tokenRef.Token))
                {
                    continue;
                }

                if (!store.ExistsReady(tokenRef.Token))
                {
                    var info = store.GetInfo(tokenRef.Token);
                    if (!info.Exists)
                    {
                        missing.Add(tokenRef.Token);
                    }
                    else
                    {
                        notReady.Add(tokenRef.Token);
                    }

                    continue;
                }

                var path = store.ResolveDataPath(tokenRef.Token);

                if (inlineAsLinkedResources && tokenRef.Inline && !string.IsNullOrWhiteSpace(tokenRef.ContentId) && mailMessage.IsBodyHtml)
                {
                    inlineResources.Add((tokenRef, path));
                    tracker.Add(tokenRef.Token);
                    continue;
                }

                // ...existing attachment creation and add...
                System.Net.Mail.Attachment attachment;
                if (!string.IsNullOrWhiteSpace(tokenRef.ContentType))
                {
                    try
                    {
                        attachment = new System.Net.Mail.Attachment(path, tokenRef.ContentType);
                    }
                    catch
                    {
                        attachment = new System.Net.Mail.Attachment(path);
                    }
                }
                else
                {
                    attachment = new System.Net.Mail.Attachment(path);
                }

                if (!string.IsNullOrWhiteSpace(tokenRef.FileName))
                {
                    attachment.Name = tokenRef.FileName;
                    try
                    {
                        attachment.ContentDisposition.FileName = tokenRef.FileName;
                    }
                    catch
                    {
                    }
                }

                if (!string.IsNullOrWhiteSpace(tokenRef.ContentId))
                {
                    attachment.ContentId = tokenRef.ContentId;
                }

                try
                {
                    attachment.ContentDisposition.Inline = tokenRef.Inline;
                }
                catch
                {
                }

                try
                {
                    if (tokenRef.Inline)
                    {
                        attachment.TransferEncoding = TransferEncoding.Base64;
                    }
                }
                catch
                {
                }

                mailMessage.Attachments.Add(attachment);

                tracker.Add(tokenRef.Token);
            }

            if (inlineResources.Count > 0)
            {
                this.ApplyInlineTokenResourcesToAlternateViews(mailMessage, inlineResources);
            }

            if (missing.Count == 0 && notReady.Count == 0)
            {
                return AttachmentTokenResolutionResult.Successful();
            }

            var reason = this.BuildMissingTokenFailureReason(missing, notReady);
            return AttachmentTokenResolutionResult.Failed(reason);
        }

        private void ApplyInlineTokenResourcesToAlternateViews(System.Net.Mail.MailMessage mailMessage, IReadOnlyList<(Grpc.AttachmentTokenRef TokenRef, string Path)> resources)
        {
            if (resources == null || resources.Count == 0)
            {
                return;
            }

            var htmlViews = mailMessage.AlternateViews
                .Where(v => v != null && v.ContentType != null && string.Equals(v.ContentType.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (htmlViews.Count == 0)
            {
                try
                {
                    var view = AlternateView.CreateAlternateViewFromString(mailMessage.Body ?? string.Empty, null, "text/html");
                    mailMessage.AlternateViews.Add(view);
                    htmlViews.Add(view);
                }
                catch
                {
                    return;
                }
            }

            foreach (var view in htmlViews)
            {
                foreach (var r in resources)
                {
                    try
                    {
                        var linked = string.IsNullOrWhiteSpace(r.TokenRef.ContentType)
                            ? new LinkedResource(r.Path)
                            : new LinkedResource(r.Path, r.TokenRef.ContentType);

                        linked.ContentId = r.TokenRef.ContentId;
                        linked.TransferEncoding = TransferEncoding.Base64;

                        view.LinkedResources.Add(linked);
                    }
                    catch
                    {
                        // ignore and allow external senders to handle via attachments if needed
                    }
                }
            }
        }

        private string BuildMissingTokenFailureReason(IReadOnlyList<string> missing, IReadOnlyList<string> notReady)
        {
            var parts = new List<string>();

            if (missing != null && missing.Count > 0)
            {
                parts.Add("Missing attachment token(s): " + string.Join(",", missing));
            }

            if (notReady != null && notReady.Count > 0)
            {
                parts.Add("Attachment token(s) not ready: " + string.Join(",", notReady));
            }

            return string.Join("; ", parts);
        }

        private sealed class AttachmentTokenResolutionResult
        {
            private AttachmentTokenResolutionResult(bool success, string message)
            {
                this.Success = success;
                this.Message = message;
            }

            public bool Success { get; }

            public string Message { get; }

            public static AttachmentTokenResolutionResult Successful()
            {
                return new AttachmentTokenResolutionResult(true, string.Empty);
            }

            public static AttachmentTokenResolutionResult Failed(string message)
            {
                return new AttachmentTokenResolutionResult(false, message ?? string.Empty);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1201:Elements should appear in the correct order", Justification = "Keeping this formatter near the nested result type preserves readability in this large coordinator.")]
        private string ToSingleLineReason(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Unknown";
            }

            var s = raw.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            s = Regex.Replace(s, "\\s+", " ").Trim();
            if (s.Length > MaxReasonLength)
            {
                s = s.Substring(0, MaxReasonLength - 3) + "...";
            }

            return s;
        }

        private string ResolveStateDbPath()
        {
            lock (this.stateDbGate)
            {
                if (!string.IsNullOrWhiteSpace(this.resolvedStateDbPath))
                {
                    return this.resolvedStateDbPath;
                }

                var configured = this.configuration["mailforge:dispatcher:stateDbPath"];
                var root = AppContext.BaseDirectory;

                var preferred = string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(root, DefaultStateDbFileName)
                    : (Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured));

                this.resolvedStateDbPath = this.TryGetWritableStateDbPath(preferred);
                return this.resolvedStateDbPath;
            }
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
                    return preferred;
                }

                if (!this.stateDbFallbackLogged)
                {
                    this.stateDbFallbackLogged = true;
                    this.logger.LogWarning(
                        ex,
                        "Dispatcher state database path is not writable. Falling back to '{FallbackPath}'. PreferredPath='{PreferredPath}'.",
                        fallback,
                        preferred);
                }

                return fallback;
            }
        }

        // MailForge job work roots are now resolved by the MailForge worker itself.
        // The queue service does not need to configure or pass filesystem paths.
    }
}