//-----------------------------------------------------------------------
// <copyright file="Home.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MailForge.Grpc;
    using MailFunk.Models;
    using MailFunk.Services;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;

    public partial class Home : IDisposable
    {
        private readonly Dictionary<int, string> rangeLabels = new()
        {
            { 10, "Last 10 min" },
            { 30, "Last 30 min" },
            { 60, "Last hour" },
            { 180, "Last 3 hours" },
            { 1440, "Last day" },
            { 10080, "Last week" },
        };

        private List<MailFileEntry> queueFiles = new();
        private List<MailFileEntry> failedFiles = new();
        private int queueTake = 5;
        private int failedTake = 5;
        private MailFolderSummary summary = new(0, 0, string.Empty, string.Empty, IsRemoteAccessible: true, RemoteStatusMessage: string.Empty);
        private MailFileEntry? selectedFile;
        private bool refreshing;
        private DateTime lastRefreshUtc = DateTime.MinValue;
        private bool isPaused;
        private bool pauseBusy;
        private int pauseMinutes = 5;
        private int maxPauseMinutes = 30;
        private DateTime? autoResumeUtc;
        private string countdown = string.Empty;
        private Timer? statusTimer;

        // Usage stats
        private int selectedRangeMinutes = 60;
        private bool usageBusy;
        private string mostActiveClient = string.Empty;
        private int distinctClients;
        private long emailsInRange;
        private long failuresInRange;
        private string topFailureClient = string.Empty;
        private long sentInRange;
        private long failedInRange;

        // Mail merge summaries
        private bool mailMergeBusy;
        private List<MailMergeDashboardRow> activeMerges = new();
        private List<MailMergeDashboardRow> recentMerges = new();

        // Dispatch merge state
        private bool dispatchStateBusy;
        private List<MergeDispatchStateRow> dispatchRows = new();

        [Inject]
        private MailFunk.Services.IFolderSummaryService FolderSummary { get; set; } = default!;

        [Inject]
        private MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        [Inject]
        private MailFunk.Services.IMailMergeSummaryService MailMergeSummaryService { get; set; } = default!;

        [Inject]
        private MailForgeService.MailForgeServiceClient ForgeClient { get; set; } = default!;

        [Inject]
        private IMergeDispatchStateService MergeDispatchStateService { get; set; } = default!;

        private string LastRefreshLine => this.lastRefreshUtc == DateTime.MinValue ? "Not refreshed yet" : $"Last refresh: {this.lastRefreshUtc.ToLocalTime():u}";

        public void Dispose()
        {
            this.statusTimer?.Dispose();
        }

        protected override async Task OnInitializedAsync()
        {
            await this.RefreshInternalAsync();
            this.statusTimer = new Timer(async _ => await this.UpdateProcessingStatusAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private async Task UpdateProcessingStatusAsync()
        {
            try
            {
                var status = await this.GrpcClient.GetProcessingStatusAsync(new GetProcessingStatusRequest()).ConfigureAwait(false);
                this.isPaused = status.IsPaused;
                if (status.IsPaused && DateTime.TryParse(status.AutoResumeUtc, out var autoResume))
                {
                    this.autoResumeUtc = autoResume.ToUniversalTime();
                    var remaining = this.autoResumeUtc.Value - DateTime.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    this.countdown = remaining.ToString(remaining.TotalHours >= 1 ? "hh\\:mm\\:ss" : "mm\\:ss");
                }
                else
                {
                    this.autoResumeUtc = null;
                    this.countdown = string.Empty;
                }

                await this.InvokeAsync(this.StateHasChanged);
            }
            catch
            {
            }
        }

        private async Task TogglePauseAsync()
        {
            if (this.pauseBusy)
            {
                return;
            }

            this.pauseBusy = true;
            try
            {
                if (this.isPaused)
                {
                    _ = await this.GrpcClient.ResumeProcessingAsync(new ResumeProcessingRequest()).ConfigureAwait(false);
                }
                else
                {
                    _ = await this.GrpcClient.PauseProcessingAsync(new PauseProcessingRequest
                    {
                        RequestedMinutes = this.pauseMinutes,
                    }).ConfigureAwait(false);
                }

                await this.UpdateProcessingStatusAsync().ConfigureAwait(false);
            }
            finally
            {
                this.pauseBusy = false;
            }
        }

        private async Task RefreshAll()
        {
            if (this.refreshing)
            {
                return;
            }

            this.refreshing = true;
            try
            {
                await this.RefreshInternalAsync();
            }
            finally
            {
                this.refreshing = false;
            }
        }

        private async Task RefreshUsageStatsAsync()
        {
            try
            {
                this.usageBusy = true;
                var now = DateTimeOffset.UtcNow;
                var from = now.AddMinutes(-this.selectedRangeMinutes);
                var req = new UsageStatsRequest
                {
                    FromUtcUnixMs = from.ToUnixTimeMilliseconds(),
                    ToUtcUnixMs = now.ToUnixTimeMilliseconds(),
                };

                var reply = await this.GrpcClient.GetUsageStatsAsync(req);
                this.mostActiveClient = reply.MostActiveClientId ?? string.Empty;
                this.distinctClients = reply.DistinctClients;
                this.emailsInRange = reply.TotalEmails;
                this.failuresInRange = reply.TotalFailures;
                this.topFailureClient = reply.TopFailureClientId ?? string.Empty;
                this.sentInRange = reply.SentInPeriod;
                this.failedInRange = reply.FailedInPeriod;
            }
            catch
            {
                this.mostActiveClient = "";
                this.distinctClients = 0;
                this.emailsInRange = 0;
                this.failuresInRange = 0;
                this.topFailureClient = "";
                this.sentInRange = 0;
                this.failedInRange = 0;
            }
            finally
            {
                this.usageBusy = false;
            }
        }

        private async Task OnRangeChanged(int value)
        {
            this.selectedRangeMinutes = value;
            await this.RefreshUsageStatsAsync();
            await this.InvokeAsync(this.StateHasChanged);
        }

        private async Task RefreshInternalAsync()
        {
            this.summary = this.FolderSummary.GetSummary();
            this.LoadQueue();
            this.LoadFailed();
            if (this.selectedFile != null)
            {
                var selectedName = this.selectedFile.Name;
                var selectedKind = this.selectedFile.FolderKind;

                var stillExists = (!string.IsNullOrWhiteSpace(selectedName) && selectedKind != MailFolderKind.Unspecified)
                    ? this.queueFiles.Any(f => f.FolderKind == selectedKind && string.Equals(f.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ||
                      this.failedFiles.Any(f => f.FolderKind == selectedKind && string.Equals(f.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                    : this.queueFiles.Any(f => string.Equals(f.FullPath, this.selectedFile.FullPath, StringComparison.OrdinalIgnoreCase)) ||
                      this.failedFiles.Any(f => string.Equals(f.FullPath, this.selectedFile.FullPath, StringComparison.OrdinalIgnoreCase));

                if (!stillExists)
                {
                    this.selectedFile = null;
                }
            }

            await this.RefreshUsageStatsAsync();
            await this.RefreshMailMergeSummariesAsync();
            await this.RefreshMergeDispatchStateAsync();

            this.lastRefreshUtc = DateTime.UtcNow;
            this.StateHasChanged();
        }

        private async Task RefreshMailMergeSummariesAsync()
        {
            try
            {
                this.mailMergeBusy = true;
                var reply = await this.MailMergeSummaryService.ListAsync(50, CancellationToken.None).ConfigureAwait(false);

                var forgeJobs = new Dictionary<string, MergeJobSummary>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var forgeReply = await this.ForgeClient.ListMergeJobsAsync(new ListMergeJobsRequest
                    {
                        Take = 250,
                    }).ConfigureAwait(false);

                    if (forgeReply?.Jobs != null)
                    {
                        foreach (var j in forgeReply.Jobs)
                        {
                            if (j != null && !string.IsNullOrWhiteSpace(j.MergeId))
                            {
                                forgeJobs[j.MergeId] = j;
                            }
                        }
                    }
                }
                catch
                {
                }

                this.activeMerges = this.MapMailMergeRows(reply?.Active, forgeJobs);
                this.recentMerges = this.MapMailMergeRows(reply?.Recent, forgeJobs);
            }
            catch
            {
                this.activeMerges = new List<MailMergeDashboardRow>();
                this.recentMerges = new List<MailMergeDashboardRow>();
            }
            finally
            {
                this.mailMergeBusy = false;
            }
        }

        private List<MailMergeDashboardRow> MapMailMergeRows(
            System.Collections.Generic.IEnumerable<MailMergeSummary>? merges,
            Dictionary<string, MergeJobSummary> forgeJobs)
        {
            var rows = new List<MailMergeDashboardRow>();

            if (merges == null)
            {
                return rows;
            }

            foreach (var m in merges)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.MergeId))
                {
                    continue;
                }

                _ = forgeJobs.TryGetValue(m.MergeId, out var job);
                var status = !string.IsNullOrWhiteSpace(job?.Status) ? job.Status : (m.Status ?? string.Empty);

                rows.Add(new MailMergeDashboardRow(
                    mergeId: m.MergeId,
                    status: status,
                    batchCount: m.BatchCount,
                    templateFileName: m.TemplateFileName ?? string.Empty,
                    templateFullPath: m.TemplateFullPath ?? string.Empty,
                    completed: job?.CompletedRows ?? 0,
                    total: job?.TotalRows ?? 0,
                    failed: job?.FailedRows ?? 0));
            }

            return rows;
        }

        private void SelectMergeTemplate(MailMergeDashboardRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.TemplateFileName))
            {
                return;
            }

            this.selectedFile = new MailFileEntry(
                FolderKind: MailFolderKind.MailMergeQueue,
                Name: row.TemplateFileName,
                FullPath: row.TemplateFullPath,
                SizeBytes: 0,
                CreatedUtc: DateTime.UtcNow,
                ModifiedUtc: DateTime.UtcNow,
                ClientId: string.Empty,
                AttemptCount: 0,
                FileRef: new MailFileRef
                {
                    Folder = MailFolderKind.MailMergeQueue,
                    Name = row.TemplateFileName,
                });

            this.StateHasChanged();
        }

        private async Task RefreshMergeDispatchStateAsync()
        {
            try
            {
                this.dispatchStateBusy = true;
                var reply = await this.MergeDispatchStateService.ListAsync(100, CancellationToken.None).ConfigureAwait(false);
                this.dispatchRows = reply?.Rows?.ToList() ?? new List<MergeDispatchStateRow>();
            }
            catch
            {
                this.dispatchRows = new List<MergeDispatchStateRow>();
            }
            finally
            {
                this.dispatchStateBusy = false;
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }

        private void LoadQueue()
        {
            this.queueFiles = this.FolderSummary.ListFiles(MailFolderKind.Queue, 0, this.queueTake).ToList();
        }

        private void LoadFailed()
        {
            this.failedFiles = this.FolderSummary.ListFiles(MailFolderKind.Failed, 0, this.failedTake).ToList();
        }

        private void LoadMoreQueue()
        {
            this.queueTake += 10;
            this.LoadQueue();
            this.StateHasChanged();
        }

        private void LoadMoreFailed()
        {
            this.failedTake += 10;
            this.LoadFailed();
            this.StateHasChanged();
        }

        private void OnQueueSelectedChanged(MailFileEntry? entry) => this.selectedFile = entry;
        private void OnFailedSelectedChanged(MailFileEntry? entry) => this.selectedFile = entry;
    }
}
