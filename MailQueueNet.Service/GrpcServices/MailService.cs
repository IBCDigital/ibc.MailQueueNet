//-----------------------------------------------------------------------
// <copyright file="MailService.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.GrpcServices
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Security.Claims;
    using System.Security.Cryptography;
    using System.Text;
    using MailQueueNet.Service.Security;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Common.FileExtensions;
    using MailQueueNet.Service.Core;
    using MailQueueNet.Service.Utilities;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    internal class MailService : Grpc.MailGrpcService.MailGrpcServiceBase
    {
        private const int AttachmentDownloadBufferSize = 64 * 1024;

        private static readonly TimeSpan MergeReopenWindow = TimeSpan.FromSeconds(60);

        private const int DefaultListTake = 50;
        private const int MaxListTake = 500;
        private const int DefaultMergeTake = 50;
        private const int MaxMergeTake = 500;

        private readonly ILogger adminLog;
        private readonly ILogger requestLog;
        private readonly ILogger<MailService> logger;
        private readonly IConfiguration configuration;
        private readonly IWebHostEnvironment env;
        private readonly Coordinator coordinator;
        private readonly MailMergeQueueWriter mailMergeQueueWriter;
        private readonly SqliteDispatcherStateStore stateStore;
        private readonly SqliteAttachmentIndexStore attachmentIndexStore;
        private readonly IAttachmentIndexNotifier attachmentIndexNotifier;
        private readonly AuditNonceStore nonceStore;
        private readonly IStagingRecipientAllowListStore stagingRecipientAllowListStore;

        public MailService(
            ILogger<MailService> logger,
            ILoggerFactory loggerFactory,
            IConfiguration configuration,
            IWebHostEnvironment env,
            Coordinator coordinator,
            MailMergeQueueWriter mailMergeQueueWriter,
            SqliteDispatcherStateStore stateStore,
            SqliteAttachmentIndexStore attachmentIndexStore,
            IAttachmentIndexNotifier attachmentIndexNotifier,
            AuditNonceStore nonceStore,
            IStagingRecipientAllowListStore stagingRecipientAllowListStore)
        {
            this.logger = logger;
            this.adminLog = loggerFactory.CreateLogger("Admin");
            this.requestLog = loggerFactory.CreateLogger("MailRequest");
            this.configuration = configuration;
            this.env = env;
            this.coordinator = coordinator;
            this.mailMergeQueueWriter = mailMergeQueueWriter;
            this.stateStore = stateStore;
            this.attachmentIndexStore = attachmentIndexStore;
            this.attachmentIndexNotifier = attachmentIndexNotifier;
            this.nonceStore = nonceStore;
            this.stagingRecipientAllowListStore = stagingRecipientAllowListStore;
        }

        // Public gRPC overrides first (SA1202)
        public override Task<Grpc.MailMessageReply> QueueMail(Grpc.MailMessage request, ServerCallContext context)
        {
            this.EnforceClientAuth(context, request);

            var clientId = request.Headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))?.Value ?? "unknown";
            var from = request.From?.Address ?? string.Empty;
            var to = string.Join(",", request.To.Select(a => a.Address));
            this.requestLog.LogInformation("Mail received (ClientId={ClientId}; From={From}; To={To}; Subject='{Subject}')", clientId, from, to, request.Subject ?? string.Empty);

            var success = false;
            try
            {
                this.coordinator.AddMail(request);
                success = true;
                MailQueueNet.Service.Core.Telemetry.Metrics.IncQueued();
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Exception thrown for QueueMail (ClientId={ClientId})", clientId);
            }

            return Task.FromResult(new Grpc.MailMessageReply { Success = success });
        }

        public override Task<Grpc.MailMessageReply> QueueMailWithSettings(Grpc.MailMessageWithSettings request, ServerCallContext context)
        {
            this.EnforceClientAuth(context, request.Message);

            var clientId = request.Message.Headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))?.Value ?? "unknown";
            var from = request.Message.From?.Address ?? string.Empty;
            var to = string.Join(",", request.Message.To.Select(a => a.Address));
            this.requestLog.LogInformation("Mail received (with settings) (ClientId={ClientId}; From={From}; To={To}; Subject='{Subject}')", clientId, from, to, request.Message.Subject ?? string.Empty);

            var success = false;
            try
            {
                this.coordinator.AddMail(request);
                success = true;
                MailQueueNet.Service.Core.Telemetry.Metrics.IncQueued();
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Exception thrown for QueueMailWithSettings (ClientId={ClientId})", clientId);
            }

            return Task.FromResult(new Grpc.MailMessageReply { Success = success });
        }

        public override Task<Grpc.SetMailSettingsReply> SetMailSettings(Grpc.SetMailSettingsMessage request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            SettingsController.SetMailSettings(request.Settings);

            // Apply changes immediately without service restart
            this.coordinator.RefreshSettings();
            return Task.FromResult(new Grpc.SetMailSettingsReply());
        }

        public override Task<Grpc.GetMailSettingsReply> GetMailSettings(Grpc.GetMailSettingsMessage request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            return Task.FromResult(new Grpc.GetMailSettingsReply { Settings = SettingsController.GetMailSettings(this.configuration) });
        }

        public override Task<Grpc.SetSettingsReply> SetSettings(Grpc.SetSettingsMessage request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            SettingsController.SetSettings(request.Settings);

            // Apply changes immediately without service restart
            this.coordinator.RefreshSettings();
            return Task.FromResult(new Grpc.SetSettingsReply());
        }

        public override Task<Grpc.GetSettingsReply> GetSettings(Grpc.GetSettingsMessage request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            return Task.FromResult(new Grpc.GetSettingsReply { Settings = SettingsController.GetSettings(this.configuration) });
        }

        public override Task<Grpc.GetServiceConfigReply> GetServiceConfig(Grpc.GetServiceConfigRequest request, ServerCallContext context)
        {
            var cfg = SettingsController.GetSettings(this.configuration);
            var queue = cfg.QueueFolder;
            var failed = cfg.FailedFolder;
            var merge = cfg.MailMergeQueueFolder;
            try
            {
                queue = Utilities.Files.MapPath(queue);
            }
            catch
            {
            }

            try
            {
                failed = Utilities.Files.MapPath(failed);
            }
            catch
            {
            }

            try
            {
                merge = Utilities.Files.MapPath(merge);
            }
            catch
            {
            }

            return Task.FromResult(new Grpc.GetServiceConfigReply
            {
                QueueFolder = queue,
                FailedFolder = failed,
                UndeliveredFolder = string.Empty,
                MailMergeQueueFolder = merge,
            });
        }

        public override async Task<Grpc.ListAllowedTestRecipientsReply> ListAllowedTestRecipients(Grpc.ListAllowedTestRecipientsRequest request, ServerCallContext context)
        {
            this.EnsureStagingEnvironment(context, nameof(this.ListAllowedTestRecipients));
            var clientId = this.ResolveAllowListClientId(context, request?.ClientId, requireExplicitClientIdForAdmin: true);
            var items = await this.stagingRecipientAllowListStore.ListAsync(clientId, context.CancellationToken).ConfigureAwait(false);
            var reply = new Grpc.ListAllowedTestRecipientsReply();
            reply.EmailAddresses.AddRange(items);
            return reply;
        }

        public override async Task<Grpc.AddAllowedTestRecipientReply> AddAllowedTestRecipient(Grpc.AddAllowedTestRecipientRequest request, ServerCallContext context)
        {
            this.EnsureStagingEnvironment(context, nameof(this.AddAllowedTestRecipient));
            var clientId = this.ResolveAllowListClientId(context, request?.ClientId, requireExplicitClientIdForAdmin: true);
            var emailAddress = this.ValidateAllowedTestRecipientEmail(request?.EmailAddress);
            var success = await this.stagingRecipientAllowListStore.AddAsync(clientId, emailAddress, context.CancellationToken).ConfigureAwait(false);
            return new Grpc.AddAllowedTestRecipientReply
            {
                Success = success,
                Message = success ? string.Empty : "Recipient was not added.",
            };
        }

        public override async Task<Grpc.DeleteAllowedTestRecipientReply> DeleteAllowedTestRecipient(Grpc.DeleteAllowedTestRecipientRequest request, ServerCallContext context)
        {
            this.EnsureStagingEnvironment(context, nameof(this.DeleteAllowedTestRecipient));
            var clientId = this.ResolveAllowListClientId(context, request?.ClientId, requireExplicitClientIdForAdmin: true);
            var emailAddress = this.ValidateAllowedTestRecipientEmail(request?.EmailAddress);
            var success = await this.stagingRecipientAllowListStore.DeleteAsync(clientId, emailAddress, context.CancellationToken).ConfigureAwait(false);
            return new Grpc.DeleteAllowedTestRecipientReply
            {
                Success = success,
                Message = success ? string.Empty : "Recipient was not removed.",
            };
        }

        public override Task<Grpc.BulkActionReply> DeleteMails(Grpc.ModifyFilesRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var folder = this.ResolveMailFolderPath(request.Folder);

            int total = request.Files.Count + request.FileRefs.Count;
            int succeeded = 0;

            foreach (var fileRef in request.FileRefs)
            {
                try
                {
                    var kind = fileRef?.Folder ?? Grpc.MailFolderKind.Unspecified;
                    var refFolder = this.ResolveMailFolderPath(kind);
                    var resolved = this.TryResolveAdminMailFilePath(refFolder, fileRef?.Name);
                    if (string.IsNullOrWhiteSpace(resolved) && kind == Grpc.MailFolderKind.Unspecified)
                    {
                        resolved = this.TryResolveAdminMailFilePathAcrossMailFolders(fileRef?.Name);
                    }

                    if (string.IsNullOrWhiteSpace(resolved))
                    {
                        continue;
                    }

                    if (File.Exists(resolved))
                    {
                        File.Delete(resolved);
                        succeeded++;
                    }
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Delete failed for file_ref {Folder}:{Name}", fileRef?.Folder, fileRef?.Name);
                }
            }

            foreach (var f in request.Files)
            {
                try
                {
                    var resolved = this.TryResolveAdminMailFilePath(folder, f);

                    // If a caller supplied an absolute path, do not require them to also provide a matching
                    // folder hint. Instead, validate the path is within one of the configured mail folders.
                    if (string.IsNullOrWhiteSpace(resolved) && Path.IsPathRooted(f))
                    {
                        resolved = this.TryResolveAdminMailFilePathAcrossMailFolders(f);
                    }

                    if (string.IsNullOrWhiteSpace(resolved) && request.Folder == Grpc.MailFolderKind.Unspecified)
                    {
                        resolved = this.TryResolveAdminMailFilePathAcrossMailFolders(f);
                    }

                    if (string.IsNullOrWhiteSpace(resolved))
                    {
                        continue;
                    }

                    if (File.Exists(resolved))
                    {
                        File.Delete(resolved);
                        succeeded++;
                    }
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Delete failed for {File}", f);
                }
            }

            return Task.FromResult(new Grpc.BulkActionReply
            {
                Total = total,
                Succeeded = succeeded,
                Failed = total - succeeded,
            });
        }

        public override Task<Grpc.BulkActionReply> RetryFailedMails(Grpc.ModifyFilesRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var cfg = SettingsController.GetSettings(this.configuration);
            var queue = cfg.QueueFolder;
            try
            {
                queue = Utilities.Files.MapPath(queue);
            }
            catch
            {
            }

            // For convenience, RetryFailedMails defaults to the failed folder when callers do not provide a folder hint.
            var sourceKind = request.Folder == Grpc.MailFolderKind.Unspecified
                ? Grpc.MailFolderKind.Failed
                : request.Folder;

            var sourceFolder = this.ResolveMailFolderPath(sourceKind);

            int total = request.Files.Count + request.FileRefs.Count;
            int succeeded = 0;

            foreach (var fileRef in request.FileRefs)
            {
                try
                {
                    var kind = fileRef?.Folder ?? Grpc.MailFolderKind.Unspecified;
                    if (kind == Grpc.MailFolderKind.Unspecified)
                    {
                        kind = sourceKind;
                    }

                    var refFolder = this.ResolveMailFolderPath(kind);
                    var source = this.TryResolveAdminMailFilePath(refFolder, fileRef?.Name);
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    {
                        continue;
                    }

                    var dest = Path.Combine(queue, Path.GetFileName(source));
                    int counter = 1;
                    while (File.Exists(dest))
                    {
                        var name = Path.GetFileNameWithoutExtension(source) + $"_r{counter}" + Path.GetExtension(source);
                        dest = Path.Combine(queue, name);
                        counter++;
                    }

                    File.Move(source, dest, false);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Retry move failed for file_ref {Folder}:{Name}", fileRef?.Folder, fileRef?.Name);
                }
            }

            foreach (var f in request.Files)
            {
                try
                {
                    var source = this.TryResolveAdminMailFilePath(sourceFolder, f);

                    // If the caller supplied an absolute path, do not require the folder hint to match.
                    // Only allow retry moves from the effective source folder.
                    if (string.IsNullOrWhiteSpace(source) && Path.IsPathRooted(f))
                    {
                        var candidate = this.TryResolveAdminMailFilePathAcrossMailFolders(f);
                        if (!string.IsNullOrWhiteSpace(candidate) && IsUnderFolder(sourceFolder, candidate))
                        {
                            source = candidate;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    {
                        continue;
                    }

                    var dest = Path.Combine(queue, Path.GetFileName(source));
                    int counter = 1;
                    while (File.Exists(dest))
                    {
                        var name = Path.GetFileNameWithoutExtension(source) + $"_r{counter}" + Path.GetExtension(source);
                        dest = Path.Combine(queue, name);
                        counter++;
                    }

                    File.Move(source, dest, false);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Retry move failed for {File}", f);
                }
            }

            return Task.FromResult(new Grpc.BulkActionReply
            {
                Total = total,
                Succeeded = succeeded,
                Failed = total - succeeded,
            });
        }

        public override Task<Grpc.PauseProcessingReply> PauseProcessing(Grpc.PauseProcessingRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var user = context.GetHttpContext()?.User?.Identity?.Name ?? "unknown";
            var settings = SettingsController.GetSettings(this.configuration);
            this.coordinator.TryPause(user, request.RequestedMinutes, settings.MaximumPauseMinutes, out var effective, out var autoResume);
            this.adminLog.LogInformation("Processing paused by {User} for {Minutes} minute(s) (auto resume {AutoResume})", user, effective, autoResume);
            return Task.FromResult(new Grpc.PauseProcessingReply { Success = true, EffectiveMinutes = effective, AutoResumeUtc = autoResume?.ToUniversalTime().ToString("o") ?? string.Empty });
        }

        public override Task<Grpc.ResumeProcessingReply> ResumeProcessing(Grpc.ResumeProcessingRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var user = context.GetHttpContext()?.User?.Identity?.Name ?? "unknown";
            var success = this.coordinator.Resume(user);
            if (success)
            {
                this.adminLog.LogInformation("Processing resumed by {User}", user);
            }

            return Task.FromResult(new Grpc.ResumeProcessingReply { Success = success });
        }

        public override Task<Grpc.GetProcessingStatusReply> GetProcessingStatus(Grpc.GetProcessingStatusRequest request, ServerCallContext context)
        {
            var (isPaused, pausedBy, pausedAt, autoResume) = this.coordinator.GetPauseStatus();
            return Task.FromResult(new Grpc.GetProcessingStatusReply
            {
                IsPaused = isPaused,
                PausedBy = pausedBy ?? string.Empty,
                PausedAtUtc = pausedAt?.ToUniversalTime().ToString("o") ?? string.Empty,
                AutoResumeUtc = autoResume?.ToUniversalTime().ToString("o") ?? string.Empty,
            });
        }

        public override async Task StreamServerLog(Grpc.ReadLogRequest request, IServerStreamWriter<Grpc.ReadLogReply> responseStream, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var baseFolder = this.ResolveLogsFolder();
            var fullPath = Path.Combine(baseFolder, request.Name ?? string.Empty);
            if (!File.Exists(fullPath))
            {
                await responseStream.WriteAsync(new Grpc.ReadLogReply { Name = request.Name ?? string.Empty, Content = "ERROR: File not found", Size = 0, ModifiedUtc = string.Empty });
                return;
            }

            long position = 0;
            try
            {
                var fi = new FileInfo(fullPath);
                position = Math.Max(0, fi.Length - Math.Max(0, request.TailBytes));
            }
            catch
            {
                position = 0;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            FileStream? fs = null;
            StreamReader? reader = null;
            try
            {
                fs = await this.OpenForReadWithRetryAsync(fullPath, position, cts.Token).ConfigureAwait(false);
                reader = new StreamReader(fs, Encoding.UTF8);
                var lastSize = new FileInfo(fullPath).Length;
                await responseStream.WriteAsync(new Grpc.ReadLogReply { Name = Path.GetFileName(fullPath), Content = await reader.ReadToEndAsync(cts.Token).ConfigureAwait(false), Size = lastSize, ModifiedUtc = File.GetLastWriteTimeUtc(fullPath).ToString("o") });
            }
            catch (Exception ex)
            {
                await responseStream.WriteAsync(new Grpc.ReadLogReply { Name = Path.GetFileName(fullPath), Content = "ERROR: " + ex.Message, Size = 0, ModifiedUtc = string.Empty });
                return;
            }

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                    long size;
                    try
                    {
                        size = new FileInfo(fullPath).Length;
                    }
                    catch
                    {
                        break;
                    }

                    if (size < fs!.Position)
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                    }

                    if (size > fs.Position)
                    {
                        var chunk = await reader!.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                        await responseStream.WriteAsync(new Grpc.ReadLogReply { Name = Path.GetFileName(fullPath), Content = chunk, Size = size, ModifiedUtc = File.GetLastWriteTimeUtc(fullPath).ToString("o") });
                    }
                }
                catch (IOException)
                {
                    try
                    {
                        reader?.Dispose();
                        fs?.Dispose();
                        fs = await this.OpenForReadWithRetryAsync(fullPath, 0, cts.Token).ConfigureAwait(false);
                        reader = new StreamReader(fs, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        await responseStream.WriteAsync(new Grpc.ReadLogReply { Name = Path.GetFileName(fullPath), Content = "ERROR: " + ex.Message, Size = 0, ModifiedUtc = string.Empty });
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public override async Task<Grpc.ListLogsReply> ListServerLogFiles(Grpc.ListLogsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var baseFolder = this.ResolveLogsFolder();
            try
            {
                Directory.CreateDirectory(baseFolder);
            }
            catch
            {
            }

            var di = new DirectoryInfo(baseFolder);
            var files = di.GetFiles("*.txt").Where(f => f.Name.StartsWith("log_") || f.Name.StartsWith("service_") || f.Name.StartsWith("admin_") || f.Name.StartsWith("mailreq_") || f.Name.StartsWith("sent_") || f.Name.StartsWith("failed_")).OrderByDescending(f => f.LastWriteTimeUtc).Select(f => new Grpc.LogFileInfo
            {
                Name = f.Name,
                Size = f.Length,
                ModifiedUtc = f.LastWriteTimeUtc.ToString("o"),
            });

            var reply = new Grpc.ListLogsReply { BaseFolder = baseFolder };
            reply.Files.AddRange(files);
            return await Task.FromResult(reply).ConfigureAwait(false);
        }

        public override async Task<Grpc.ReadLogReply> ReadServerLog(Grpc.ReadLogRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);
            var baseFolder = this.ResolveLogsFolder();
            var fullPath = Path.Combine(baseFolder, request.Name ?? string.Empty);
            var reply = new Grpc.ReadLogReply { Name = request.Name ?? string.Empty };
            try
            {
                if (!File.Exists(fullPath))
                {
                    reply.Content = "ERROR: File not found";
                    reply.Size = 0;
                    reply.ModifiedUtc = string.Empty;
                    return reply;
                }

                var fi = new FileInfo(fullPath);
                reply.Size = fi.Length;
                reply.ModifiedUtc = fi.LastWriteTimeUtc.ToString("o");
                if (request.TailBytes > 0 && fi.Length > request.TailBytes)
                {
                    await using var fs = await this.OpenForReadWithRetryAsync(fullPath, Math.Max(0, fi.Length - request.TailBytes), context.CancellationToken).ConfigureAwait(false);
                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    reply.Content = await sr.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var fs = await this.OpenForReadWithRetryAsync(fullPath, 0, context.CancellationToken).ConfigureAwait(false);
                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    reply.Content = await sr.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                reply.Content = "ERROR: " + ex.Message;
                reply.Size = 0;
                reply.ModifiedUtc = string.Empty;
            }

            return reply;
        }

        public override async Task<Grpc.UsageStatsReply> GetUsageStats(Grpc.UsageStatsRequest request, ServerCallContext context)
        {
            // Admin-only as it derives from server logs
            this.EnsureAdmin(context);

            var from = DateTimeOffset.FromUnixTimeMilliseconds(request.FromUtcUnixMs).UtcDateTime;
            var to = DateTimeOffset.FromUnixTimeMilliseconds(request.ToUtcUnixMs).UtcDateTime;
            if (to <= from)
            {
                to = DateTime.UtcNow;
            }

            var baseFolder = this.ResolveLogsFolder();
            var reqFiles = new DirectoryInfo(baseFolder).GetFiles("mailreq_*.txt");
            var sentFiles = new DirectoryInfo(baseFolder).GetFiles("sent_*.txt");
            var failedFiles = new DirectoryInfo(baseFolder).GetFiles("failed_*.txt");

            long totalReq = 0;
            long totalSent = 0;
            long totalFailed = 0;
            var perClientReq = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var perClientFailed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            async Task ScanAsync(FileInfo[] files, Action<DateTime, string> onLine)
            {
                foreach (var fi in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    await using var fs = await this.OpenForReadWithRetryAsync(fi.FullName, 0, context.CancellationToken).ConfigureAwait(false);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    while (!sr.EndOfStream)
                    {
                        var line = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var firstSep = line.IndexOf('|');
                        if (firstSep <= 0)
                        {
                            continue;
                        }

                        if (!DateTime.TryParse(line.AsSpan(0, firstSep), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
                        {
                            continue;
                        }

                        if (ts < from || ts > to)
                        {
                            continue;
                        }

                        onLine(ts, line);
                    }
                }
            }

            static string ExtractClientId(string line)
            {
                // Expect pattern: "ClientId=...;" inside parentheses
                var key = "ClientId=";
                var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return "unknown";
                }

                idx += key.Length;
                var end = line.IndexOfAny(new[] { ';', ')', ' ' }, idx);
                if (end < 0)
                {
                    end = line.Length;
                }

                return line.Substring(idx, end - idx).Trim();
            }

            await ScanAsync(reqFiles, (ts, line) =>
            {
                totalReq++;
                var cid = ExtractClientId(line);
                perClientReq[cid] = perClientReq.TryGetValue(cid, out var c) ? c + 1 : 1;
            }).ConfigureAwait(false);

            await ScanAsync(sentFiles, (ts, line) =>
            {
                totalSent++;
            }).ConfigureAwait(false);

            await ScanAsync(failedFiles, (ts, line) =>
            {
                totalFailed++;
                var cid = ExtractClientId(line);
                perClientFailed[cid] = perClientFailed.TryGetValue(cid, out var c) ? c + 1 : 1;
            }).ConfigureAwait(false);

            string mostActive = string.Empty;
            long mostActiveCount = 0;
            foreach (var kv in perClientReq)
            {
                if (kv.Value > mostActiveCount)
                {
                    mostActive = kv.Key;
                    mostActiveCount = kv.Value;
                }
            }

            string topFailure = string.Empty;
            long topFailureCount = 0;
            foreach (var kv in perClientFailed)
            {
                if (kv.Value > topFailureCount)
                {
                    topFailure = kv.Key;
                    topFailureCount = kv.Value;
                }
            }

            var reply = new Grpc.UsageStatsReply
            {
                TotalEmails = totalReq,
                TotalFailures = totalFailed,
                MostActiveClientId = mostActive,
                DistinctClients = perClientReq.Count,
                TopFailureClientId = topFailure,
                SentInPeriod = totalSent,
                FailedInPeriod = totalFailed,
            };

            foreach (var cid in perClientReq.Keys.Union(perClientFailed.Keys, StringComparer.OrdinalIgnoreCase))
            {
                reply.Clients.Add(new Grpc.ClientStats
                {
                    ClientId = cid,
                    Emails = perClientReq.TryGetValue(cid, out var ec) ? ec : 0,
                    Failures = perClientFailed.TryGetValue(cid, out var fc) ? fc : 0,
                });
            }

            return reply;
        }

        public override Task<Grpc.GetFolderSummaryReply> GetFolderSummary(Grpc.GetFolderSummaryRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var cfg = SettingsController.GetSettings(this.configuration);
            var (queue, failed) = this.ResolveQueueAndFailedFolders();

            var merge = cfg.MailMergeQueueFolder;
            try
            {
                merge = Files.MapPath(merge);
            }
            catch
            {
            }

            var queueCount = this.SafeCount(queue);
            var failedCount = this.SafeCount(failed);
            var mergeCount = this.SafeCount(merge);

            return Task.FromResult(new Grpc.GetFolderSummaryReply
            {
                QueueFolder = queue,
                FailedFolder = failed,
                MailMergeQueueFolder = merge,
                QueueCount = queueCount,
                FailedCount = failedCount,
                MailMergeQueueCount = mergeCount,
            });
        }

        public override async Task<Grpc.ListMailFilesReply> ListMailFiles(Grpc.ListMailFilesRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var cfg = SettingsController.GetSettings(this.configuration);
            var (queue, failed) = this.ResolveQueueAndFailedFolders();

            var merge = cfg.MailMergeQueueFolder;
            try
            {
                merge = Files.MapPath(merge);
            }
            catch
            {
            }

            var folder = request.Folder switch
            {
                Grpc.MailFolderKind.Queue => queue,
                Grpc.MailFolderKind.Failed => failed,
                Grpc.MailFolderKind.MailMergeQueue => merge,
                _ => string.Empty,
            };

            if (string.IsNullOrWhiteSpace(folder))
            {
                return new Grpc.ListMailFilesReply
                {
                    Folder = string.Empty,
                    Total = 0,
                };
            }

            var skip = Math.Max(0, request.Skip);
            var take = request.Take;
            if (take <= 0)
            {
                take = DefaultListTake;
            }

            if (take > MaxListTake)
            {
                take = MaxListTake;
            }

            try
            {
                // Yield so we do not compete with mail sending on the same thread.
                await Task.Yield();

                var files = Directory.GetFiles(folder, "*.mail").OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var total = files.Length;

                var page = files.Skip(skip).Take(take).ToArray();

                var reply = new Grpc.ListMailFilesReply
                {
                    Folder = folder,
                    Total = total,
                };

                foreach (var fullPath in page)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    var info = this.TryBuildMailFileInfo(fullPath, request.Folder);
                    if (info is not null)
                    {
                        reply.Files.Add(info);
                    }
                }

                return reply;
            }
            catch (OperationCanceledException)
            {
                return new Grpc.ListMailFilesReply
                {
                    Folder = folder,
                    Total = 0,
                };
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "ListMailFiles failed. Folder={Folder}", folder);
                return new Grpc.ListMailFilesReply
                {
                    Folder = folder,
                    Total = 0,
                };
            }
        }

        public override Task<Grpc.ReadMailFileReply> ReadMailFile(Grpc.ReadMailFileRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var effectiveFolder = request.Folder;
            var effectiveName = request.Name;
            if (request.FileRef is not null && !string.IsNullOrWhiteSpace(request.FileRef.Name))
            {
                effectiveFolder = request.FileRef.Folder;
                effectiveName = request.FileRef.Name;
            }

            string filePath;
            if (!string.IsNullOrWhiteSpace(request.FullPath))
            {
                // For backwards compatibility, callers may supply either a full server path or just a file name.
                // When a file name is supplied, we resolve it using the configured folders so callers do not need
                // to pass raw filesystem paths.
                if (Path.IsPathRooted(request.FullPath))
                {
                    filePath = request.FullPath;

                    // Do not require callers to also supply a folder hint when passing an absolute path.
                    // Always validate the requested path is within one of the configured mail folders.
                    if (string.IsNullOrWhiteSpace(this.TryResolveAdminMailFilePathAcrossMailFolders(filePath)))
                    {
                        throw new RpcException(new Status(StatusCode.PermissionDenied, "full_path is outside the configured mail folders"));
                    }
                }
                else
                {
                    var hintedFolder = this.ResolveMailFolderPath(request.Folder);
                    var resolved = this.TryResolveAdminMailFilePath(hintedFolder, request.FullPath);

                    if (string.IsNullOrWhiteSpace(resolved) && request.Folder == Grpc.MailFolderKind.Unspecified)
                    {
                        resolved = this.TryResolveAdminMailFilePathAcrossMailFolders(request.FullPath);
                    }

                    if (string.IsNullOrWhiteSpace(resolved))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "full_path must be a full server path or a unique file name"));
                    }

                    filePath = resolved;
                }
            }
            else
            {
                var folder = this.ResolveMailFolderPath(effectiveFolder);
                var resolved = this.TryResolveAdminMailFilePath(folder, effectiveName);

                if (string.IsNullOrWhiteSpace(resolved) && effectiveFolder == Grpc.MailFolderKind.Unspecified)
                {
                    resolved = this.TryResolveAdminMailFilePathAcrossMailFolders(effectiveName);
                }

                if (string.IsNullOrWhiteSpace(resolved))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Specify either full_path, folder + name, or a unique file name"));
                }

                filePath = resolved;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    throw new RpcException(new Status(StatusCode.NotFound, "File not found"));
                }

                var mail = FileUtils.ReadMailFromFile(filePath);
                return Task.FromResult(new Grpc.ReadMailFileReply
                {
                    FullPath = filePath,
                    Mail = mail,
                });
            }
            catch (RpcException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "File not found"));
            }
            catch (IOException ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        public override Task<Grpc.QueueMailBulkReply> QueueMailBulk(Grpc.QueueMailBulkRequest request, ServerCallContext context)
        {
            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            var total = request.Mails?.Count ?? 0;
            if (total <= 0)
            {
                return Task.FromResult(new Grpc.QueueMailBulkReply
                {
                    Total = 0,
                    Accepted = 0,
                    Failed = 0,
                });
            }

            // Enforce auth using the first message (headers will be stamped on that message).
            // MailQueueNet's auth model is per-call but results in a client-id header being
            // embedded within each queued item.
            var first = request.Mails[0];
            if (first?.Message == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "mails[0].message is required"));
            }

            this.EnforceClientAuth(context, first.Message);

            var clientId = first.Message.Headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))?.Value ?? "unknown";

            var accepted = 0;
            var failed = 0;

            for (var i = 0; i < request.Mails.Count; i++)
            {
                var item = request.Mails[i];
                if (item?.Message == null)
                {
                    failed++;
                    continue;
                }

                // Stamp the same client id header onto each item so downstream diagnostics match
                // what single-message QueueMail does.
                if (!item.Message.Headers.Any(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase)))
                {
                    item.Message.Headers.Add(new Grpc.Header { Name = "X-Client-ID", Value = clientId });
                }

                try
                {
                    this.coordinator.AddMail(item);
                    accepted++;
                    MailQueueNet.Service.Core.Telemetry.Metrics.IncQueued();
                }
                catch (Exception ex)
                {
                    this.logger?.LogError(ex, "Exception thrown for QueueMailBulk item (ClientId={ClientId}; Index={Index})", clientId, i);
                    failed++;
                }
            }

            this.requestLog.LogInformation(
                "Bulk mail received (ClientId={ClientId}; Total={Total}; Accepted={Accepted}; Failed={Failed})",
                clientId,
                total,
                accepted,
                failed);

            return Task.FromResult(new Grpc.QueueMailBulkReply
            {
                Total = total,
                Accepted = accepted,
                Failed = failed,
            });
        }

        public override Task<Grpc.QueueMailMergeReply> QueueMailMerge(Grpc.QueueMailMergeRequest request, ServerCallContext context)
        {
            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            if (request.Message != null)
            {
                this.EnforceClientAuth(context, request.Message);
            }

            var mergeId = request.MergeId;
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                mergeId = Guid.NewGuid().ToString("N");
            }

            var clientId = request.Message?.Headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))?.Value
                ?? context.RequestHeaders.FirstOrDefault(h => string.Equals(h.Key, "x-client-id", StringComparison.OrdinalIgnoreCase))?.Value
                ?? "unknown";

            this.requestLog.LogInformation(
                "Mail merge batch received (ClientId={ClientId}; MergeId={MergeId}; HasTemplate={HasTemplate}; Lines={Lines})",
                clientId,
                mergeId,
                request.Message != null,
                request.JsonLines?.Count ?? 0);

            // Hard API-level rejection if merge is closed.
            if (!this.CanAcceptMergeBatch(mergeId, context, out var shouldReopen))
            {
                this.requestLog.LogWarning(
                    "Mail merge batch rejected because merge is closed (ClientId={ClientId}; MergeId={MergeId}; Lines={Lines})",
                    clientId,
                    mergeId,
                    request.JsonLines?.Count ?? 0);

                return Task.FromResult(new Grpc.QueueMailMergeReply
                {
                    Success = false,
                    MergeId = mergeId,
                    TemplateFileName = string.Empty,
                    BatchId = -1,
                });
            }

            // Stamp merge id into message headers so it is persisted on disk.
            if (request.Message != null && !request.Message.Headers.Any(h => string.Equals(h.Name, "X-MailMerge-Id", StringComparison.OrdinalIgnoreCase)))
            {
                request.Message.Headers.Add(new Grpc.Header { Name = "X-MailMerge-Id", Value = mergeId });
            }

            bool success = false;
            string templateFileName = string.Empty;
            int batchId = -1;

            try
            {
                if (shouldReopen)
                {
                    this.TryReopenMerge(mergeId, context);
                }

                var settings = SettingsController.GetSettings(this.configuration);

                // Only the first request for a merge id requires a template.
                var wrapper = request.Message == null
                    ? new Grpc.MailMessageWithSettings { Message = new Grpc.MailMessage(), Settings = null }
                    : new Grpc.MailMessageWithSettings { Message = request.Message, Settings = null };

                var lines = request.JsonLines == null ? Array.Empty<string>() : request.JsonLines.ToArray();

                var result = this.mailMergeQueueWriter.TryQueue(settings, wrapper, mergeId, lines);
                if (result.HasValue)
                {
                    success = true;
                    templateFileName = result.Value.TemplateFileName;
                    batchId = result.Value.BatchId;
                    MailQueueNet.Service.Core.Telemetry.Metrics.IncQueued();

                    this.requestLog.LogInformation(
                        "Mail merge batch queued (ClientId={ClientId}; MergeId={MergeId}; Template={Template}; BatchId={BatchId}; Lines={Lines}; Reopened={Reopened})",
                        clientId,
                        mergeId,
                        templateFileName,
                        batchId,
                        lines.Length,
                        shouldReopen);
                }
                else
                {
                    this.requestLog.LogWarning(
                        "Mail merge batch queue failed (ClientId={ClientId}; MergeId={MergeId}; Lines={Lines}; Reopened={Reopened})",
                        clientId,
                        mergeId,
                        lines.Length,
                        shouldReopen);
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Exception thrown for QueueMailMerge (MergeId={MergeId})", mergeId);
            }

            return Task.FromResult(new Grpc.QueueMailMergeReply
            {
                Success = success,
                MergeId = mergeId,
                TemplateFileName = templateFileName,
                BatchId = batchId,
            });
        }

        public override Task<Grpc.QueueMailMergeReply> QueueMailMergeWithSettings(Grpc.QueueMailMergeWithSettingsRequest request, ServerCallContext context)
        {
            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            if (request.Mail?.Message != null)
            {
                this.EnforceClientAuth(context, request.Mail.Message);
            }

            var mergeId = request.MergeId;
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                mergeId = Guid.NewGuid().ToString("N");
            }

            var clientId = request.Mail?.Message?.Headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))?.Value
                ?? context.RequestHeaders.FirstOrDefault(h => string.Equals(h.Key, "x-client-id", StringComparison.OrdinalIgnoreCase))?.Value
                ?? "unknown";

            this.requestLog.LogInformation(
                "Mail merge (with settings) batch received (ClientId={ClientId}; MergeId={MergeId}; HasTemplate={HasTemplate}; Lines={Lines})",
                clientId,
                mergeId,
                request.Mail?.Message != null,
                request.JsonLines?.Count ?? 0);

            if (request.Mail?.Message != null && !request.Mail.Message.Headers.Any(h => string.Equals(h.Name, "X-MailMerge-Id", StringComparison.OrdinalIgnoreCase)))
            {
                request.Mail.Message.Headers.Add(new Grpc.Header { Name = "X-MailMerge-Id", Value = mergeId });
            }

            bool success = false;
            string templateFileName = string.Empty;
            int batchId = -1;

            try
            {
                var settings = SettingsController.GetSettings(this.configuration);

                var wrapper = request.Mail ?? new Grpc.MailMessageWithSettings { Message = new Grpc.MailMessage(), Settings = null };

                var lines = request.JsonLines == null ? Array.Empty<string>() : request.JsonLines.ToArray();

                var result = this.mailMergeQueueWriter.TryQueue(settings, wrapper, mergeId, lines);
                if (result.HasValue)
                {
                    success = true;
                    templateFileName = result.Value.TemplateFileName;
                    batchId = result.Value.BatchId;
                    MailQueueNet.Service.Core.Telemetry.Metrics.IncQueued();

                    this.requestLog.LogInformation(
                        "Mail merge (with settings) batch queued (ClientId={ClientId}; MergeId={MergeId}; Template={Template}; BatchId={BatchId}; Lines={Lines})",
                        clientId,
                        mergeId,
                        templateFileName,
                        batchId,
                        lines.Length);
                }
                else
                {
                    this.requestLog.LogWarning(
                        "Mail merge (with settings) batch queue failed (ClientId={ClientId}; MergeId={MergeId}; Lines={Lines})",
                        clientId,
                        mergeId,
                        lines.Length);
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Exception thrown for QueueMailMergeWithSettings (MergeId={MergeId})", mergeId);
            }

            return Task.FromResult(new Grpc.QueueMailMergeReply
            {
                Success = success,
                MergeId = mergeId,
                TemplateFileName = templateFileName,
                BatchId = batchId,
            });
        }

        public override Task<Grpc.AckMailMergeBatchReply> AckMailMergeBatch(Grpc.AckMailMergeBatchRequest request, ServerCallContext context)
        {
            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            if (string.IsNullOrWhiteSpace(request.TemplateFileName))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "template_file_name is required"));
            }

            if (request.BatchId < 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "batch_id must be >= 0"));
            }

            // This is intended to be called by MailForge. Use client auth (shared secret) if configured.
            var settings = SettingsController.GetSettings(this.configuration);

            var deleted = false;
            try
            {
                deleted = this.mailMergeQueueWriter.TryDeleteBatch(settings, request.TemplateFileName, request.BatchId);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "AckMailMergeBatch failed (MergeId={MergeId}; Template={Template}; BatchId={BatchId})", request.MergeId, request.TemplateFileName, request.BatchId);
                deleted = false;
            }

            if (deleted)
            {
                this.requestLog.LogInformation(
                    "Mail merge batch acknowledged and deleted (MergeId={MergeId}; Template={Template}; BatchId={BatchId})",
                    request.MergeId,
                    request.TemplateFileName,
                    request.BatchId);
            }
            else
            {
                this.requestLog.LogWarning(
                    "Mail merge batch acknowledgement failed (MergeId={MergeId}; Template={Template}; BatchId={BatchId})",
                    request.MergeId,
                    request.TemplateFileName,
                    request.BatchId);
            }

            return Task.FromResult(new Grpc.AckMailMergeBatchReply
            {
                Success = deleted,
                Message = deleted ? string.Empty : "Failed deleting batch file",
            });
        }

        public override Task<Grpc.ListMailMergesReply> ListMailMerges(Grpc.ListMailMergesRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var take = request?.Take ?? 0;
            if (take <= 0)
            {
                take = DefaultMergeTake;
            }

            if (take > MaxMergeTake)
            {
                take = MaxMergeTake;
            }

            var reply = new Grpc.ListMailMergesReply();

            var mergeFolder = this.ResolveMergeFolderPath();
            if (string.IsNullOrWhiteSpace(mergeFolder) || !Directory.Exists(mergeFolder))
            {
                return Task.FromResult(reply);
            }

            string[] templates;
            try
            {
                templates = Directory.GetFiles(mergeFolder, "*.mail");
            }
            catch
            {
                templates = Array.Empty<string>();
            }

            var summaries = new List<Grpc.MailMergeSummary>();
            foreach (var templatePath in templates)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var templateFileName = Path.GetFileName(templatePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(templateFileName))
                {
                    continue;
                }

                var mergeId = TryExtractMergeIdFromTemplateFileName(templateFileName);
                if (string.IsNullOrWhiteSpace(mergeId))
                {
                    continue;
                }

                var summary = this.TryBuildMergeSummary(mergeFolder, mergeId, templateFileName, templatePath, context.CancellationToken);
                if (summary != null)
                {
                    summaries.Add(summary);
                }
            }

            // Most recent first
            var ordered = summaries
                .OrderByDescending(s => TryParseUtcOrDefault(s.ModifiedUtc))
                .ThenByDescending(s => TryParseUtcOrDefault(s.CreatedUtc))
                .Take(take)
                .ToArray();

            foreach (var s in ordered)
            {
                // Treat both "Completed" (within the reopen window) and "Closed" (after the reopen
                // window) as non-active merges.
                if (string.Equals(s.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    reply.Recent.Add(s);
                }
                else
                {
                    reply.Active.Add(s);
                }
            }

            return Task.FromResult(reply);
        }

        public override Task<Grpc.GetMailMergeSummaryReply> GetMailMergeSummary(Grpc.GetMailMergeSummaryRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.MergeId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "merge_id is required"));
            }

            var mergeFolder = this.ResolveMergeFolderPath();
            if (string.IsNullOrWhiteSpace(mergeFolder) || !Directory.Exists(mergeFolder))
            {
                return Task.FromResult(new Grpc.GetMailMergeSummaryReply());
            }

            // Find matching template by parsing merge id.
            string? templatePath = null;
            string templateFileName = string.Empty;
            try
            {
                foreach (var file in Directory.GetFiles(mergeFolder, "*.mail"))
                {
                    var name = Path.GetFileName(file) ?? string.Empty;
                    var id = TryExtractMergeIdFromTemplateFileName(name);
                    if (string.Equals(id, request.MergeId, StringComparison.OrdinalIgnoreCase))
                    {
                        templatePath = file;
                        templateFileName = name;
                        break;
                    }
                }
            }
            catch
            {
                templatePath = null;
            }

            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                return Task.FromResult(new Grpc.GetMailMergeSummaryReply());
            }

            var summary = this.TryBuildMergeSummary(mergeFolder, request.MergeId, templateFileName, templatePath, context.CancellationToken);
            return Task.FromResult(new Grpc.GetMailMergeSummaryReply
            {
                Summary = summary,
            });
        }

        private Grpc.MailMergeSummary? TryBuildMergeSummary(string mergeFolder, string mergeId, string templateFileName, string templatePath, CancellationToken ct)
        {
            try
            {
                var fi = new FileInfo(templatePath);
                if (!fi.Exists)
                {
                    return null;
                }

                var clientId = this.TryReadClientIdFromTemplate(templatePath);

                var pendingBatchFiles = 0;
                var batchIds = new HashSet<int>();
                try
                {
                    foreach (var batchFile in Directory.GetFiles(mergeFolder, templateFileName + ".*.jsonl"))
                    {
                        ct.ThrowIfCancellationRequested();

                        pendingBatchFiles++;
                        var batchId = TryExtractBatchIdFromBatchFilePath(batchFile);
                        if (batchId >= 0)
                        {
                            batchIds.Add(batchId);
                        }
                    }
                }
                catch
                {
                    pendingBatchFiles = 0;
                }

                var (status, completedUtc) = this.TryGetMergeDispatchStatus(mergeId, ct);

                return new Grpc.MailMergeSummary
                {
                    MergeId = mergeId,
                    TemplateFileName = templateFileName,
                    TemplateFullPath = fi.FullName,
                    ClientId = clientId,
                    CreatedUtc = fi.CreationTimeUtc.ToString("o"),
                    ModifiedUtc = fi.LastWriteTimeUtc.ToString("o"),
                    Status = status,
                    CompletedUtc = completedUtc,
                    BatchCount = batchIds.Count,
                    PendingBatchFiles = pendingBatchFiles,
                };
            }
            catch
            {
                return null;
            }
        }

        private (string Status, string CompletedUtc) TryGetMergeDispatchStatus(string mergeId, CancellationToken ct)
        {
            try
            {
                var stateDbPath = this.ResolveStateDbPath();
                var state = this.stateStore.TryGetMergeJobStateAsync(stateDbPath, mergeId, ct).GetAwaiter().GetResult();
                if (state == null)
                {
                    return ("Pending", string.Empty);
                }

                if (state.Status == SqliteDispatcherStateStore.MergeJobDispatchStatus.Completed)
                {
                    if (state.CompletedUtc.HasValue)
                    {
                        var age = DateTimeOffset.UtcNow - state.CompletedUtc.Value;
                        var status = age > MergeReopenWindow ? "Closed" : "Completed";
                        return (status, state.CompletedUtc.Value.ToString("o"));
                    }

                    return ("Completed", string.Empty);
                }

                if (state.Status == SqliteDispatcherStateStore.MergeJobDispatchStatus.Dispatched)
                {
                    return ("Running", state.CompletedUtc?.ToString("o") ?? string.Empty);
                }

                if (state.Status == SqliteDispatcherStateStore.MergeJobDispatchStatus.Failed)
                {
                    return ("Failed", state.CompletedUtc?.ToString("o") ?? string.Empty);
                }

                return (state.Status.ToString(), state.CompletedUtc?.ToString("o") ?? string.Empty);
            }
            catch
            {
                return ("Unknown", string.Empty);
            }
        }

        private string TryReadClientIdFromTemplate(string templatePath)
        {
            try
            {
                var wrapper = FileUtils.ReadMailFromFile(templatePath);
                var headers = wrapper?.Message?.Headers;
                if (headers == null)
                {
                    return string.Empty;
                }

                var idHeader = headers.FirstOrDefault(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase));
                return idHeader?.Value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int TryExtractBatchIdFromBatchFilePath(string fullPath)
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

        private string ResolveMergeFolderPath()
        {
            var cfg = SettingsController.GetSettings(this.configuration);
            var merge = cfg.MailMergeQueueFolder;
            try
            {
                merge = Files.MapPath(merge);
            }
            catch
            {
            }

            return merge ?? string.Empty;
        }

        private static DateTime TryParseUtcOrDefault(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DateTime.MinValue;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }

            return DateTime.MinValue;
        }

        private (string Queue, string Failed) ResolveQueueAndFailedFolders()
        {
            var cfg = SettingsController.GetSettings(this.configuration);
            var queue = cfg.QueueFolder;
            var failed = cfg.FailedFolder;

            try
            {
                queue = Files.MapPath(queue);
            }
            catch
            {
            }

            try
            {
                failed = Files.MapPath(failed);
            }
            catch
            {
            }

            return (queue, failed);
        }

        private string ResolveMailFolderPath(Grpc.MailFolderKind folderKind)
        {
            if (folderKind == Grpc.MailFolderKind.Unspecified)
            {
                return string.Empty;
            }

            var (queue, failed) = this.ResolveQueueAndFailedFolders();

            var cfg = SettingsController.GetSettings(this.configuration);
            var merge = cfg.MailMergeQueueFolder;
            try
            {
                merge = Files.MapPath(merge);
            }
            catch
            {
            }

            return folderKind switch
            {
                Grpc.MailFolderKind.Queue => queue,
                Grpc.MailFolderKind.Failed => failed,
                Grpc.MailFolderKind.MailMergeQueue => merge,
                _ => string.Empty,
            };
        }

        private string? TryResolveAdminMailFilePath(string? resolvedFolder, string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
            {
                return null;
            }

            if (Path.IsPathRooted(pathOrName))
            {
                if (string.IsNullOrWhiteSpace(resolvedFolder))
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(resolvedFolder) && !IsUnderFolder(resolvedFolder, pathOrName))
                {
                    return null;
                }

                return pathOrName;
            }

            if (string.IsNullOrWhiteSpace(resolvedFolder))
            {
                return null;
            }

            var safeName = Path.GetFileName(pathOrName);
            if (!string.Equals(pathOrName, safeName, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(safeName))
            {
                return null;
            }

            return Path.Combine(resolvedFolder, safeName);
        }

        private string? TryResolveAdminMailFilePathAcrossMailFolders(string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
            {
                return null;
            }

            // Absolute paths must still be contained within one of the known mail folders.
            if (Path.IsPathRooted(pathOrName))
            {
                var (queue, failed) = this.ResolveQueueAndFailedFolders();
                var merge = this.ResolveMailFolderPath(Grpc.MailFolderKind.MailMergeQueue);
                if (IsUnderFolder(queue, pathOrName) || IsUnderFolder(failed, pathOrName) || (!string.IsNullOrWhiteSpace(merge) && IsUnderFolder(merge, pathOrName)))
                {
                    return pathOrName;
                }

                return null;
            }

            // Only allow clean file names (no directory separators).
            var safeName = Path.GetFileName(pathOrName);
            if (!string.Equals(pathOrName, safeName, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(safeName))
            {
                return null;
            }

            var (queueFolder, failedFolder) = this.ResolveQueueAndFailedFolders();
            var mergeFolder = this.ResolveMailFolderPath(Grpc.MailFolderKind.MailMergeQueue);

            var candidates = new List<string>(capacity: 3);

            var queueCandidate = string.IsNullOrWhiteSpace(queueFolder) ? null : Path.Combine(queueFolder, safeName);
            if (!string.IsNullOrWhiteSpace(queueCandidate) && File.Exists(queueCandidate))
            {
                candidates.Add(queueCandidate);
            }

            var failedCandidate = string.IsNullOrWhiteSpace(failedFolder) ? null : Path.Combine(failedFolder, safeName);
            if (!string.IsNullOrWhiteSpace(failedCandidate) && File.Exists(failedCandidate))
            {
                candidates.Add(failedCandidate);
            }

            var mergeCandidate = string.IsNullOrWhiteSpace(mergeFolder) ? null : Path.Combine(mergeFolder, safeName);
            if (!string.IsNullOrWhiteSpace(mergeCandidate) && File.Exists(mergeCandidate))
            {
                candidates.Add(mergeCandidate);
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // 0 matches: not found; >1 matches: ambiguous.
            return null;
        }

        private static bool IsUnderFolder(string folder, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

                var basePath = Path.GetFullPath(folder);
                var fullPath = Path.GetFullPath(path);

                if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), comparison) && !basePath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), comparison))
                {
                    basePath += Path.DirectorySeparatorChar;
                }

                return fullPath.StartsWith(basePath, comparison);
            }
            catch
            {
                return false;
            }
        }

        private int SafeCount(string folder)
        {
            try
            {
                return Directory.GetFiles(folder, "*.mail").Length;
            }
            catch
            {
                return 0;
            }
        }

        private Grpc.MailFileInfo? TryBuildMailFileInfo(string fullPath, Grpc.MailFolderKind folderKind)
        {
            try
            {
                var fi = new FileInfo(fullPath);
                if (!fi.Exists)
                {
                    return null;
                }

                string clientId = string.Empty;
                var attempt = 0;

                try
                {
                    // Best-effort: this is only used for UI listing and should not block mail sending.
                    var wrapper = FileUtils.ReadMailFromFile(fullPath);
                    var headers = wrapper.Message?.Headers;
                    if (headers != null)
                    {
                        foreach (var h in headers)
                        {
                            if (string.IsNullOrEmpty(clientId) && string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase))
                            {
                                clientId = h.Value;
                            }
                            else if (attempt == 0 && string.Equals(h.Name, "X-Attempt-Count", StringComparison.OrdinalIgnoreCase))
                            {
                                int.TryParse(h.Value, out attempt);
                            }

                            if (!string.IsNullOrEmpty(clientId) && attempt != 0)
                            {
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore header extraction problems.
                }

                return new Grpc.MailFileInfo
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    Size = fi.Length,
                    CreatedUtc = fi.CreationTimeUtc.ToString("o"),
                    ModifiedUtc = fi.LastWriteTimeUtc.ToString("o"),
                    ClientId = clientId ?? string.Empty,
                    AttemptCount = attempt,
                    FileRef = new Grpc.MailFileRef
                    {
                        Folder = folderKind,
                        Name = fi.Name,
                    },
                };
            }
            catch
            {
                return null;
            }
        }

        private string ResolveLogsFolder()
        {
            var section = this.configuration.GetSection("FileLogging");
            var configured = section["Path"];
            var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(root, "Logs");
            }

            return Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured);
        }

        private async Task<FileStream> OpenForReadWithRetryAsync(string path, long position, CancellationToken ct)
        {
            var delay = 50;
            var attempts = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (position > 0)
                    {
                        fs.Seek(position, SeekOrigin.Begin);
                    }

                    return fs;
                }
                catch (IOException) when (attempts < 5)
                {
                    attempts++;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 2000);
                }
            }
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
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        private static bool IsAdmin(ServerCallContext context)
        {
            var roleClaim = context.GetHttpContext()?.User?.Claims?.FirstOrDefault(c => c.Type == ClaimTypes.Role);
            return string.Equals(roleClaim?.Value, "Admin", StringComparison.OrdinalIgnoreCase);
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

        private void EnforceClientAuth(ServerCallContext context, Grpc.MailMessage message)
        {
            var sharedSecret = this.configuration["Security:SharedClientSecret"];
            if (string.IsNullOrWhiteSpace(sharedSecret))
            {
                return;
            }

            var clientIdValue = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-client-id")?.Value;
            var suppliedPass = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-client-pass")?.Value;
            if (string.IsNullOrWhiteSpace(clientIdValue) || string.IsNullOrWhiteSpace(suppliedPass))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing client credentials"));
            }

            var expected = ComputePassword(clientIdValue, sharedSecret);
            if (!ConstantEquals(expected, suppliedPass))
            {
                this.logger.LogWarning("Client auth failed for id {ClientId}", clientIdValue);
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid client credentials"));
            }

            if (!message.Headers.Any(h => string.Equals(h.Name, "X-Client-ID", StringComparison.OrdinalIgnoreCase)))
            {
                message.Headers.Add(new Grpc.Header { Name = "X-Client-ID", Value = clientIdValue });
            }
        }

        private string EnsureAuthenticatedClientId(ServerCallContext context)
        {
            var sharedSecret = this.configuration["Security:SharedClientSecret"];
            if (string.IsNullOrWhiteSpace(sharedSecret))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Client shared-secret authentication is not configured"));
            }

            var clientIdValue = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-client-id")?.Value;
            var suppliedPass = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-client-pass")?.Value;
            if (string.IsNullOrWhiteSpace(clientIdValue) || string.IsNullOrWhiteSpace(suppliedPass))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing client credentials"));
            }

            var expected = ComputePassword(clientIdValue, sharedSecret);
            if (!ConstantEquals(expected, suppliedPass))
            {
                this.logger.LogWarning("Client auth failed for id {ClientId}", clientIdValue);
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid client credentials"));
            }

            return clientIdValue;
        }

        private string ResolveAllowListClientId(ServerCallContext context, string? requestedClientId, bool requireExplicitClientIdForAdmin)
        {
            if (IsAdmin(context))
            {
                return this.ValidateRequestedAllowListClientId(requestedClientId, requireExplicitClientIdForAdmin);
            }

            if (this.IsSharedSecretAdmin(context, out var adminId))
            {
                this.EnforceAdminReplayProtection(context, adminId);
                return this.ValidateRequestedAllowListClientId(requestedClientId, requireExplicitClientIdForAdmin);
            }

            return this.EnsureAuthenticatedClientId(context);
        }

        private string ValidateRequestedAllowListClientId(string? requestedClientId, bool requireExplicitClientIdForAdmin)
        {
            var trimmed = (requestedClientId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed) && requireExplicitClientIdForAdmin)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "client_id is required for admin allow-list management"));
            }

            return trimmed;
        }

        private void EnsureStagingEnvironment(ServerCallContext context, string methodName)
        {
            var clientIdValue = context.RequestHeaders.FirstOrDefault(h => string.Equals(h.Key, "x-client-id", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var adminIdValue = context.RequestHeaders.FirstOrDefault(h => string.Equals(h.Key, "x-admin-id", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
            var requestedPath = context.GetHttpContext()?.Request?.Path.Value ?? string.Empty;
            var stagingRoutingEnabled = this.configuration.GetValue<bool?>("StagingMailRouting:Enabled");
            var forceMailpitOnly = this.configuration.GetValue<bool?>("StagingMailRouting:ForceMailpitOnly");
            var allowListDbPath = this.configuration["StagingMailRouting:AllowListDbPath"] ?? string.Empty;
            var isStaging = string.Equals(this.env.EnvironmentName, "Staging", StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(this.env.EnvironmentName, "Staging", StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogWarning(
                    "STAGING_ENDPOINT_GUARD rejected request. Method={Method}; Reason={Reason}; RequiredEnvironment={RequiredEnvironment}; ActualEnvironment={ActualEnvironment}; IsStaging={IsStaging}; StagingMailRoutingEnabled={StagingMailRoutingEnabled}; ForceMailpitOnly={ForceMailpitOnly}; AllowListDbPath={AllowListDbPath}; ClientId={ClientId}; AdminId={AdminId}; RequestPath={RequestPath}; ApplicationName={ApplicationName}",
                    methodName,
                    "Environment is not Staging",
                    "Staging",
                    this.env.EnvironmentName ?? string.Empty,
                    isStaging,
                    stagingRoutingEnabled?.ToString(CultureInfo.InvariantCulture) ?? "<unset>",
                    forceMailpitOnly?.ToString(CultureInfo.InvariantCulture) ?? "<unset>",
                    string.IsNullOrWhiteSpace(allowListDbPath) ? "<default>" : allowListDbPath,
                    clientIdValue,
                    adminIdValue,
                    requestedPath,
                    this.env.ApplicationName ?? string.Empty);

                throw new RpcException(new Status(StatusCode.NotFound, "Staging-only endpoint"));
            }

            this.logger.LogInformation(
                "STAGING_ENDPOINT_GUARD accepted request. Method={Method}; RequiredEnvironment={RequiredEnvironment}; ActualEnvironment={ActualEnvironment}; IsStaging={IsStaging}; StagingMailRoutingEnabled={StagingMailRoutingEnabled}; ForceMailpitOnly={ForceMailpitOnly}; AllowListDbPath={AllowListDbPath}; ClientId={ClientId}; AdminId={AdminId}; RequestPath={RequestPath}; ApplicationName={ApplicationName}",
                methodName,
                "Staging",
                this.env.EnvironmentName ?? string.Empty,
                isStaging,
                stagingRoutingEnabled?.ToString(CultureInfo.InvariantCulture) ?? "<unset>",
                forceMailpitOnly?.ToString(CultureInfo.InvariantCulture) ?? "<unset>",
                string.IsNullOrWhiteSpace(allowListDbPath) ? "<default>" : allowListDbPath,
                clientIdValue,
                adminIdValue,
                requestedPath,
                this.env.ApplicationName ?? string.Empty);
        }

        private string ValidateAllowedTestRecipientEmail(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "email_address is required"));
            }

            try
            {
                var address = new System.Net.Mail.MailAddress(trimmed);
                return address.Address.Trim().ToLowerInvariant();
            }
            catch (FormatException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "email_address is invalid"));
            }
        }

        private bool CanAcceptMergeBatch(string mergeId, ServerCallContext context, out bool shouldReopen)
        {
            shouldReopen = false;
            try
            {
                var stateDbPath = this.ResolveStateDbPath();
                if (string.IsNullOrWhiteSpace(stateDbPath))
                {
                    return true;
                }

                var state = this.stateStore.TryGetMergeJobStateAsync(stateDbPath, mergeId, context.CancellationToken).GetAwaiter().GetResult();
                if (state == null)
                {
                    return true;
                }

                if (state.Status != SqliteDispatcherStateStore.MergeJobDispatchStatus.Completed)
                {
                    return true;
                }

                if (!state.CompletedUtc.HasValue)
                {
                    return false;
                }

                var age = DateTimeOffset.UtcNow - state.CompletedUtc.Value;
                if (age <= MergeReopenWindow)
                {
                    shouldReopen = true;
                    return true;
                }

                this.logger.LogInformation("Rejecting merge batch because merge is closed (MergeId={MergeId}; AgeSeconds={AgeSeconds})", mergeId, (int)age.TotalSeconds);
                return false;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed evaluating merge closure state (MergeId={MergeId})", mergeId);
                return true;
            }
        }

        private void TryReopenMerge(string mergeId, ServerCallContext context)
        {
            try
            {
                var stateDbPath = this.ResolveStateDbPath();
                if (string.IsNullOrWhiteSpace(stateDbPath))
                {
                    return;
                }

                _ = this.stateStore.TryReopenMergeJobAsync(stateDbPath, mergeId, MergeReopenWindow, context.CancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed reopening merge job (MergeId={MergeId})", mergeId);
            }
        }

        private string ResolveStateDbPath()
        {
            var configured = this.configuration["mailforge:dispatcher:stateDbPath"];
            var root = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(root, "dispatcher_state.db");
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(root, configured);
        }

        public override Task<Grpc.ListMergeDispatchStateReply> ListMergeDispatchState(Grpc.ListMergeDispatchStateRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            var take = request?.Take ?? 0;
            if (take <= 0)
            {
                take = DefaultMergeTake;
            }

            if (take > MaxMergeTake)
            {
                take = MaxMergeTake;
            }

            var reply = new Grpc.ListMergeDispatchStateReply();

            try
            {
                var stateDbPath = this.ResolveStateDbPath();
                var rows = this.stateStore.ListMergeDispatchStateRowsAsync(stateDbPath, take, context.CancellationToken).GetAwaiter().GetResult();

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    reply.Rows.Add(new Grpc.MergeDispatchStateRow
                    {
                        MergeId = row.MergeId ?? string.Empty,
                        TemplatePath = row.TemplatePath ?? string.Empty,
                        WorkerAddress = row.WorkerAddress ?? string.Empty,
                        FenceToken = row.FenceToken,
                        Status = row.Status ?? string.Empty,
                        LastError = row.LastError ?? string.Empty,
                        DispatchedUtc = row.UpdatedUtc == DateTimeOffset.MinValue ? string.Empty : row.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
                        CompletedUtc = row.CompletedUtc.HasValue ? row.CompletedUtc.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty,
                    });
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "ListMergeDispatchState failed");
            }

            return Task.FromResult(reply);
        }

        /// <summary>
        /// Streams an attachment upload to the queue server and returns an attachment token.
        /// </summary>
        /// <param name="requestStream">Client streaming request containing start metadata followed by bytes.</param>
        /// <param name="context">gRPC call context.</param>
        /// <returns>Reply containing the resulting token.</returns>
        public override async Task<Grpc.UploadAttachmentReply> UploadAttachment(IAsyncStreamReader<Grpc.UploadAttachmentRequest> requestStream, ServerCallContext context)
        {
            this.EnforceClientAuth(context, new Grpc.MailMessage());

            // Best-effort client id for manifest metadata.
            var clientIdValue = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-client-id")?.Value ?? string.Empty;

            Grpc.UploadAttachmentStart? start = null;

            if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "UploadAttachment requires a start message"));
            }

            if (requestStream.Current?.Start == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "First message must be start"));
            }

            start = requestStream.Current.Start;

            var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
            var folder = this.configuration["Attachments:Path"];
            var baseFolder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(root, "Attachments") : (Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder));

            var maxBytes = AttachmentStoreOptions.DefaultMaxUploadBytes;
            var maxConfig = this.configuration["Attachments:MaxBytes"];
            if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
            {
                maxBytes = configuredMax;
            }

            var store = new DiskAttachmentStore(new AttachmentStoreOptions
            {
                BaseFolder = baseFolder,
                MaxUploadBytes = maxBytes,
            });

            Task<byte[]?> ReadChunkAsync(CancellationToken ct)
            {
                return MailService.ReadChunkInternalAsync(requestStream, ct);
            }

            var result = await store.SaveUploadAsync(
                start.Token,
                start.Length > 0 ? start.Length : null,
                string.IsNullOrWhiteSpace(start.Sha256Base64) ? null : start.Sha256Base64,
                start.FileName,
                start.ContentType,
                clientIdValue,
                ReadChunkAsync,
                context.CancellationToken).ConfigureAwait(false);

            try
            {
                if (result.Success)
                {
                    var info = store.GetInfo(result.Token);
                    if (info.Exists)
                    {
                        var manifest = new AttachmentStoreManifest
                        {
                            Token = result.Token,
                            ClientId = info.ClientId ?? string.Empty,
                            FileName = info.FileName ?? string.Empty,
                            ContentType = info.ContentType ?? string.Empty,
                            Length = info.Length,
                            Sha256Base64 = info.Sha256Base64 ?? string.Empty,
                            UploadedUtc = info.UploadedUtc,
                            RefCount = info.RefCount,
                            MergeOwnerId = info.MergeOwnerId ?? string.Empty,
                        };

                        var indexDbPath = this.ResolveAttachmentIndexDbPath();
                        await this.attachmentIndexStore.UpsertFromManifestAsync(indexDbPath, manifest, info.Ready, context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed updating attachment index after upload (Token={Token})", result.Token);
            }

            return new Grpc.UploadAttachmentReply
            {
                Success = result.Success,
                Token = result.Token,
                ReceivedBytes = result.ReceivedBytes,
                Message = result.Message,
            };
        }

        public override Task<Grpc.GetAttachmentInfoReply> GetAttachmentInfo(Grpc.GetAttachmentInfoRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "token is required"));
            }

            var store = this.CreateAttachmentStore();
            var info = store.GetInfo(request.Token);

            return Task.FromResult(new Grpc.GetAttachmentInfoReply
            {
                Exists = info.Exists,
                Ready = info.Ready,
                RefCount = info.RefCount,
                Length = info.Length,
                Sha256Base64 = info.Sha256Base64 ?? string.Empty,
                UploadedUtc = info.UploadedUtc == default ? string.Empty : info.UploadedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                FileName = info.FileName ?? string.Empty,
                ContentType = info.ContentType ?? string.Empty,
                ClientId = info.ClientId ?? string.Empty,
                MergeOwnerId = info.MergeOwnerId ?? string.Empty,
            });
        }

        public override async Task DownloadAttachment(Grpc.DownloadAttachmentRequest request, IServerStreamWriter<Grpc.DownloadAttachmentReply> responseStream, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "token is required"));
            }

            var store = this.CreateAttachmentStore();
            var info = store.GetInfo(request.Token);
            if (!info.Exists || !info.Ready)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Attachment not found or not ready"));
            }

            var dataPath = store.ResolveDataPath(request.Token);
            if (!File.Exists(dataPath))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Attachment data not found"));
            }

            var offset = Math.Max(0, request.Offset);
            var remaining = request.Length > 0 ? request.Length : long.MaxValue;
            var buffer = new byte[AttachmentDownloadBufferSize];

            await using var stream = new FileStream(
                dataPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite,
                    BufferSize = AttachmentDownloadBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            if (offset > 0)
            {
                stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            }

            await responseStream.WriteAsync(new Grpc.DownloadAttachmentReply
            {
                Token = info.Token ?? request.Token,
                FileName = info.FileName ?? string.Empty,
                ContentType = info.ContentType ?? string.Empty,
                TotalLength = info.Length,
            }).ConfigureAwait(false);

            while (remaining > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var readLength = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer, 0, readLength, context.CancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                remaining -= read;
                await responseStream.WriteAsync(new Grpc.DownloadAttachmentReply
                {
                    Token = info.Token ?? request.Token,
                    FileName = info.FileName ?? string.Empty,
                    ContentType = info.ContentType ?? string.Empty,
                    TotalLength = info.Length,
                    Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                }).ConfigureAwait(false);
            }
        }

        public override Task<Grpc.GetAttachmentManifestReply> GetAttachmentManifest(Grpc.GetAttachmentManifestRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "token is required"));
            }

            var store = this.CreateAttachmentStore();
            var manifestJson = store.TryGetManifestJson(request.Token);

            return Task.FromResult(new Grpc.GetAttachmentManifestReply
            {
                Exists = manifestJson != null,
                ManifestJson = manifestJson ?? string.Empty,
            });
        }

        public override Task<Grpc.DeleteAttachmentReply> DeleteAttachment(Grpc.DeleteAttachmentRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "token is required"));
            }

            var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
            var folder = this.configuration["Attachments:Path"];
            var baseFolder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(root, "Attachments") : (Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder));

            var maxBytes = AttachmentStoreOptions.DefaultMaxUploadBytes;
            var maxConfig = this.configuration["Attachments:MaxBytes"];
            if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
            {
                maxBytes = configuredMax;
            }

            var store = new DiskAttachmentStore(new AttachmentStoreOptions
            {
                BaseFolder = baseFolder,
                MaxUploadBytes = maxBytes,
            });

            var ok = store.TryDelete(request.Token, request.Force);

            if (ok)
            {
                try
                {
                    var indexDbPath = this.ResolveAttachmentIndexDbPath();
                    this.attachmentIndexStore.DeleteAsync(indexDbPath, request.Token, context.CancellationToken).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Failed deleting token from attachment index (Token={Token})", request.Token);
                }
            }

            return Task.FromResult(new Grpc.DeleteAttachmentReply
            {
                Success = ok,
                Message = ok ? string.Empty : "Attachment not deleted (it may be referenced or not exist)",
            });
        }

        public override Task<Grpc.ListAttachmentsReply> ListAttachments(Grpc.ListAttachmentsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            DateTimeOffset? olderThanUtc = null;
            if (!string.IsNullOrWhiteSpace(request.OlderThanUtc))
            {
                if (!DateTimeOffset.TryParse(request.OlderThanUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "older_than_utc must be an ISO 8601 timestamp"));
                }

                olderThanUtc = parsed;
            }

            DateTimeOffset? newerThanUtc = null;
            if (!string.IsNullOrWhiteSpace(request.NewerThanUtc))
            {
                if (!DateTimeOffset.TryParse(request.NewerThanUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "newer_than_utc must be an ISO 8601 timestamp"));
                }

                newerThanUtc = parsed;
            }

            int? minRefCount = null;
            if (request.MinRefCount != null)
            {
                minRefCount = request.MinRefCount.Value;
            }

            int? maxRefCount = null;
            if (request.MaxRefCount != null)
            {
                maxRefCount = request.MaxRefCount.Value;
            }

            long? minLength = null;
            if (request.MinLength != null)
            {
                minLength = request.MinLength.Value;
            }

            long? maxLength = null;
            if (request.MaxLength != null)
            {
                maxLength = request.MaxLength.Value;
            }

            try
            {
                var indexDbPath = this.ResolveAttachmentIndexDbPath();
                var (total, items, nextPageToken) = this.attachmentIndexStore.ListAsync(
                        indexDbPath,
                        request.ClientId,
                        request.MergeOwnerId,
                        olderThanUtc,
                        newerThanUtc,
                        minRefCount,
                        maxRefCount,
                        minLength,
                        maxLength,
                        request.OnlyOrphans,
                        request.OnlyLarge,
                        request.LargeThresholdBytes,
                        request.SortBy,
                        request.SortDesc,
                        request.PageToken,
                        request.Skip,
                        request.Take,
                        context.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                var reply = new Grpc.ListAttachmentsReply
                {
                    Total = total,
                    NextPageToken = nextPageToken ?? string.Empty,
                };

                foreach (var item in items)
                {
                    reply.Items.Add(new Grpc.AttachmentListItem
                    {
                        Token = item.Token ?? string.Empty,
                        Exists = true,
                        Ready = item.Ready,
                        RefCount = item.RefCount,
                        Length = item.Length,
                        Sha256Base64 = item.Sha256Base64 ?? string.Empty,
                        UploadedUtc = item.UploadedUtc == default ? string.Empty : item.UploadedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        FileName = item.FileName ?? string.Empty,
                        ContentType = item.ContentType ?? string.Empty,
                        ClientId = item.ClientId ?? string.Empty,
                        MergeOwnerId = item.MergeOwnerId ?? string.Empty,
                    });
                }

                return Task.FromResult(reply);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "ListAttachments failed using attachment index. Falling back to filesystem scan.");

                var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
                var folder = this.configuration["Attachments:Path"];
                var baseFolder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(root, "Attachments") : (Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder));

                var maxBytes = AttachmentStoreOptions.DefaultMaxUploadBytes;
                var maxConfig = this.configuration["Attachments:MaxBytes"];
                if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
                {
                    maxBytes = configuredMax;
                }

                var store = new DiskAttachmentStore(new AttachmentStoreOptions
                {
                    BaseFolder = baseFolder,
                    MaxUploadBytes = maxBytes,
                }, this.attachmentIndexNotifier);

                var (total, items) = store.ListAttachments(
                    request.ClientId,
                    request.MergeOwnerId,
                    olderThanUtc,
                    newerThanUtc,
                    minRefCount,
                    maxRefCount,
                    minLength,
                    maxLength,
                    request.OnlyOrphans,
                    request.OnlyLarge,
                    request.LargeThresholdBytes,
                    request.Skip,
                    request.Take);

                var reply = new Grpc.ListAttachmentsReply
                {
                    Total = total,
                    NextPageToken = string.Empty,
                };

                foreach (var item in items)
                {
                    reply.Items.Add(new Grpc.AttachmentListItem
                    {
                        Token = item.Token ?? string.Empty,
                        Exists = item.Exists,
                        Ready = item.Ready,
                        RefCount = item.RefCount,
                        Length = item.Length,
                        Sha256Base64 = item.Sha256Base64 ?? string.Empty,
                        UploadedUtc = item.UploadedUtc == default ? string.Empty : item.UploadedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        FileName = item.FileName ?? string.Empty,
                        ContentType = item.ContentType ?? string.Empty,
                        ClientId = item.ClientId ?? string.Empty,
                        MergeOwnerId = item.MergeOwnerId ?? string.Empty,
                    });
                }

                return Task.FromResult(reply);
            }
        }

        public override Task<Grpc.DeleteAttachmentsByQueryReply> DeleteAttachmentsByQuery(Grpc.DeleteAttachmentsByQueryRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            if (!request.Force && string.IsNullOrWhiteSpace(request.OlderThanUtc))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "older_than_utc is required unless force=true"));
            }

            DateTimeOffset? olderThanUtc = null;
            if (!string.IsNullOrWhiteSpace(request.OlderThanUtc))
            {
                if (!DateTimeOffset.TryParse(request.OlderThanUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "older_than_utc must be an ISO 8601 timestamp"));
                }

                olderThanUtc = parsed;
            }

            DateTimeOffset? newerThanUtc = null;
            if (!string.IsNullOrWhiteSpace(request.NewerThanUtc))
            {
                if (!DateTimeOffset.TryParse(request.NewerThanUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "newer_than_utc must be an ISO 8601 timestamp"));
                }

                newerThanUtc = parsed;
            }

            int? minRefCount = null;
            if (request.MinRefCount != null)
            {
                minRefCount = request.MinRefCount.Value;
            }

            int? maxRefCount = null;
            if (request.MaxRefCount != null)
            {
                maxRefCount = request.MaxRefCount.Value;
            }

            long? minLength = null;
            if (request.MinLength != null)
            {
                minLength = request.MinLength.Value;
            }

            long? maxLength = null;
            if (request.MaxLength != null)
            {
                maxLength = request.MaxLength.Value;
            }

            if (!request.Force)
            {
                minRefCount = 0;
                maxRefCount = 0;
            }

            try
            {
                var indexDbPath = this.ResolveAttachmentIndexDbPath();

                var (total, items, nextPageToken) = this.attachmentIndexStore.ListAsync(
                        indexDbPath,
                        request.ClientId,
                        request.MergeOwnerId,
                        olderThanUtc,
                        newerThanUtc,
                        minRefCount,
                        maxRefCount,
                        minLength,
                        maxLength,
                        request.OnlyOrphans,
                        request.OnlyLarge,
                        request.LargeThresholdBytes,
                        request.SortBy,
                        request.SortDesc,
                        request.PageToken,
                        request.Skip,
                        request.Take,
                        context.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                var reply = new Grpc.DeleteAttachmentsByQueryReply
                {
                    TotalMatched = total,
                    DeletedCount = 0,
                    SkippedInUseCount = 0,
                    SkippedNotFoundCount = 0,
                    NextPageToken = nextPageToken ?? string.Empty,
                    Message = string.Empty,
                };

                if (items == null || items.Length == 0)
                {
                    reply.Message = "No attachments matched the query.";
                    return Task.FromResult(reply);
                }

                var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
                var folder = this.configuration["Attachments:Path"];
                var baseFolder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(root, "Attachments") : (Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder));

                var maxBytesCfg = AttachmentStoreOptions.DefaultMaxUploadBytes;
                var maxConfig = this.configuration["Attachments:MaxBytes"];
                if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
                {
                    maxBytesCfg = configuredMax;
                }

                var store = new DiskAttachmentStore(new AttachmentStoreOptions
                {
                    BaseFolder = baseFolder,
                    MaxUploadBytes = maxBytesCfg,
                }, this.attachmentIndexNotifier);

                foreach (var item in items)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    var token = item?.Token;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    if (!request.Force && item.RefCount != 0)
                    {
                        reply.SkippedInUseCount++;
                        continue;
                    }

                    if (request.DryRun)
                    {
                        reply.Tokens.Add(token);
                        reply.DeletedCount++;
                        continue;
                    }

                    if (!store.ExistsReady(token) && !store.GetInfo(token).Exists)
                    {
                        reply.SkippedNotFoundCount++;
                        try
                        {
                            this.attachmentIndexStore.DeleteAsync(indexDbPath, token, context.CancellationToken).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            this.logger.LogWarning(ex, "Failed deleting token from attachment index (Token={Token})", token);
                        }

                        continue;
                    }

                    var deleted = store.TryDelete(token, request.Force);
                    if (!deleted)
                    {
                        reply.SkippedInUseCount++;
                        continue;
                    }

                    reply.Tokens.Add(token);
                    reply.DeletedCount++;

                    try
                    {
                        this.attachmentIndexStore.DeleteAsync(indexDbPath, token, context.CancellationToken).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex, "Failed deleting token from attachment index (Token={Token})", token);
                    }
                }

                reply.Message = request.DryRun
                    ? "Dry-run only. No attachments were deleted."
                    : "Delete complete.";

                return Task.FromResult(reply);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "DeleteAttachmentsByQuery failed");
                throw new RpcException(new Status(StatusCode.Internal, "DeleteAttachmentsByQuery failed: " + ex.Message));
            }
        }

        private static async Task<byte[]?> ReadChunkInternalAsync(IAsyncStreamReader<Grpc.UploadAttachmentRequest> requestStream, CancellationToken ct)
        {
            if (!await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                return null;
            }

            var current = requestStream.Current;
            if (current == null)
            {
                return null;
            }

            if (current.Chunk == null || current.Chunk.Length == 0)
            {
                return Array.Empty<byte>();
            }

            return current.Chunk.ToByteArray();
        }

        private string ResolveAttachmentIndexDbPath()
        {
            var configured = this.configuration["Attachments:IndexDbPath"];
            var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;

            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(root, "attachment_index.db");
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(root, configured);
        }

        public override Task<Grpc.GetAttachmentStatsReply> GetAttachmentStats(Grpc.GetAttachmentStatsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            var threshold = request.LargeThresholdBytes;
            if (threshold <= 0)
            {
                threshold = 10L * 1024L * 1024L;
            }

            try
            {
                var indexDbPath = this.ResolveAttachmentIndexDbPath();
                var stats = this.attachmentIndexStore.GetStatsAsync(
                        indexDbPath,
                        threshold,
                        string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId,
                        string.IsNullOrWhiteSpace(request.MergeOwnerId) ? null : request.MergeOwnerId,
                        context.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                return Task.FromResult(new Grpc.GetAttachmentStatsReply
                {
                    Total = new Grpc.AttachmentStats { Count = stats.TotalCount, Bytes = stats.TotalBytes },
                    Orphans = new Grpc.AttachmentStats { Count = stats.OrphanCount, Bytes = stats.OrphanBytes },
                    Large = new Grpc.AttachmentStats { Count = stats.LargeCount, Bytes = stats.LargeBytes },
                });
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "GetAttachmentStats failed");
                throw new RpcException(new Status(StatusCode.Internal, "GetAttachmentStats failed: " + ex.Message));
            }
        }

        public override Task<Grpc.PreviewOrphansReply> PreviewOrphans(Grpc.PreviewOrphansRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            DateTimeOffset? olderThanUtc = null;
            if (!string.IsNullOrWhiteSpace(request.OlderThanUtc))
            {
                if (!DateTimeOffset.TryParse(request.OlderThanUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "older_than_utc must be an ISO 8601 timestamp"));
                }

                olderThanUtc = parsed;
            }

            var take = request.Take;
            if (take <= 0)
            {
                take = DefaultListTake;
            }

            if (take > MaxListTake)
            {
                take = MaxListTake;
            }

            int? minRefCount = null;
            if (request.MinRefCount != null)
            {
                minRefCount = request.MinRefCount.Value;
            }

            int? maxRefCount = null;
            if (request.MaxRefCount != null)
            {
                maxRefCount = request.MaxRefCount.Value;
            }

            // Default for this endpoint: only orphans.
            // Wrappers allow explicitly overriding behaviour if required.
            if (!minRefCount.HasValue)
            {
                minRefCount = 0;
            }

            if (!maxRefCount.HasValue)
            {
                maxRefCount = 0;
            }

            try
            {
                var indexDbPath = this.ResolveAttachmentIndexDbPath();

                var (total, items, nextPageToken) = this.attachmentIndexStore.ListAsync(
                        indexDbPath,
                        request.ClientId,
                        request.MergeOwnerId,
                        olderThanUtc,
                        newerThanUtc: null,
                        minRefCount: minRefCount,
                        maxRefCount: maxRefCount,
                        minLength: null,
                        maxLength: null,
                        onlyOrphans: true,
                        onlyLarge: false,
                        largeThresholdBytes: 0,
                        // 1 = ATTACHMENT_SORT_BY_UPLOADED_UTC
                        sortBy: (Grpc.AttachmentSortBy)1,
                        sortDesc: false,
                        pageToken: request.PageToken,
                        skip: 0,
                        take: take,
                        cancellationToken: context.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                var reply = new Grpc.PreviewOrphansReply
                {
                    Total = total,
                    NextPageToken = nextPageToken ?? string.Empty,
                };

                foreach (var item in items)
                {
                    reply.Items.Add(new Grpc.AttachmentListItem
                    {
                        Token = item.Token ?? string.Empty,
                        Exists = true,
                        Ready = item.Ready,
                        RefCount = item.RefCount,
                        Length = item.Length,
                        Sha256Base64 = item.Sha256Base64 ?? string.Empty,
                        UploadedUtc = item.UploadedUtc == default ? string.Empty : item.UploadedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        FileName = item.FileName ?? string.Empty,
                        ContentType = item.ContentType ?? string.Empty,
                        ClientId = item.ClientId ?? string.Empty,
                        MergeOwnerId = item.MergeOwnerId ?? string.Empty,
                    });
                }

                return Task.FromResult(reply);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PreviewOrphans failed");
                throw new RpcException(new Status(StatusCode.Internal, "PreviewOrphans failed: " + ex.Message));
            }
        }

        public override Task<Grpc.PreviewLargeAttachmentsReply> PreviewLargeAttachments(Grpc.PreviewLargeAttachmentsRequest request, ServerCallContext context)
        {
            this.EnsureAdmin(context);

            if (request == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request is required"));
            }

            var threshold = request.LargeThresholdBytes;
            if (threshold <= 0)
            {
                threshold = 10L * 1024L * 1024L;
            }

            var take = request.Take;
            if (take <= 0)
            {
                take = DefaultListTake;
            }

            if (take > MaxListTake)
            {
                take = MaxListTake;
            }

            int? minRefCount = null;
            if (request.MinRefCount != null)
            {
                minRefCount = request.MinRefCount.Value;
            }

            int? maxRefCount = null;
            if (request.MaxRefCount != null)
            {
                maxRefCount = request.MaxRefCount.Value;
            }

            long? minLength = null;
            if (request.MinLength != null)
            {
                minLength = request.MinLength.Value;
            }

            long? maxLength = null;
            if (request.MaxLength != null)
            {
                maxLength = request.MaxLength.Value;
            }

            try
            {
                var indexDbPath = this.ResolveAttachmentIndexDbPath();

                var (total, items, nextPageToken) = this.attachmentIndexStore.ListAsync(
                        indexDbPath,
                        request.ClientId,
                        request.MergeOwnerId,
                        olderThanUtc: null,
                        newerThanUtc: null,
                        minRefCount: minRefCount,
                        maxRefCount: maxRefCount,
                        minLength: minLength,
                        maxLength: maxLength,
                        onlyOrphans: false,
                        onlyLarge: true,
                        largeThresholdBytes: threshold,
                        // 2 = ATTACHMENT_SORT_BY_LENGTH
                        sortBy: (Grpc.AttachmentSortBy)2,
                        sortDesc: true,
                        pageToken: request.PageToken,
                        skip: 0,
                        take: take,
                        cancellationToken: context.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                var reply = new Grpc.PreviewLargeAttachmentsReply
                {
                    Total = total,
                    NextPageToken = nextPageToken ?? string.Empty,
                };

                foreach (var item in items)
                {
                    reply.Items.Add(new Grpc.AttachmentListItem
                    {
                        Token = item.Token ?? string.Empty,
                        Exists = true,
                        Ready = item.Ready,
                        RefCount = item.RefCount,
                        Length = item.Length,
                        Sha256Base64 = item.Sha256Base64 ?? string.Empty,
                        UploadedUtc = item.UploadedUtc == default ? string.Empty : item.UploadedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        FileName = item.FileName ?? string.Empty,
                        ContentType = item.ContentType ?? string.Empty,
                        ClientId = item.ClientId ?? string.Empty,
                        MergeOwnerId = item.MergeOwnerId ?? string.Empty,
                    });
                }

                return Task.FromResult(reply);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PreviewLargeAttachments failed");
                throw new RpcException(new Status(StatusCode.Internal, "PreviewLargeAttachments failed: " + ex.Message));
            }
        }

        private DiskAttachmentStore CreateAttachmentStore()
        {
            var root = this.env.ContentRootPath ?? AppContext.BaseDirectory;
            var folder = this.configuration["Attachments:Path"];
            var baseFolder = string.IsNullOrWhiteSpace(folder)
                ? Path.Combine(root, "Attachments")
                : Path.IsPathRooted(folder)
                    ? folder
                    : Path.Combine(root, folder);

            var maxBytes = AttachmentStoreOptions.DefaultMaxUploadBytes;
            var maxConfig = this.configuration["Attachments:MaxBytes"];
            if (!string.IsNullOrWhiteSpace(maxConfig) && long.TryParse(maxConfig, out var configuredMax) && configuredMax > 0)
            {
                maxBytes = configuredMax;
            }

            return new DiskAttachmentStore(new AttachmentStoreOptions
            {
                BaseFolder = baseFolder,
                MaxUploadBytes = maxBytes,
            }, this.attachmentIndexNotifier);
        }
    }
}
