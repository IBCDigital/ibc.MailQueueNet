//-----------------------------------------------------------------------
// <copyright file="MailForgeDashboard.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Text.Json;
    using MailForge.Grpc;
    using MailFunk.Models;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Displays MailForge merge job summaries.
    /// </summary>
    public sealed partial class MailForgeDashboard
    {
        private bool loading = true;
        private bool refreshing;
        private string error = string.Empty;
        private List<MergeJobSummary> allJobs = new();
        private List<MergeJobSummary> jobs = new();

        private string selectedMergeId = string.Empty;

        private DateTime? filterFrom;
        private DateTime? filterTo;

        private bool previewBusy;
        private string previewError = string.Empty;
        private GetMergeJobDetailReply? selectedDetail;
        private int selectedBatchId = -1;
        private List<int> batchOptions = new();
        private string batchPreviewRaw = string.Empty;
        private List<string> batchPreviewColumns = new();
        private List<Dictionary<string, string>> batchPreviewTableRows = new();
        private List<JsonTreeNode> batchPreviewTreeRoots = new();

        [Inject]
        private MailForgeService.MailForgeServiceClient ForgeClient { get; set; } = default!;

        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            this.ResetDateRange();
            await this.RefreshInternalAsync().ConfigureAwait(false);
        }

        private Task OnFilterFromChangedAsync(DateTime? value)
        {
            this.filterFrom = value;
            this.ApplyDateFilter();
            return Task.CompletedTask;
        }

        private Task OnFilterToChangedAsync(DateTime? value)
        {
            this.filterTo = value;
            this.ApplyDateFilter();
            return Task.CompletedTask;
        }

        private void ResetDateRange()
        {
            var today = DateTime.Today;
            this.filterFrom = today.AddDays(-2);
            this.filterTo = today;
            this.ApplyDateFilter();
        }

        private async Task SelectMergeAsync(MergeJobSummary job)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.MergeId))
            {
                return;
            }

            this.selectedMergeId = job.MergeId;
            await this.LoadPreviewAsync(job.MergeId).ConfigureAwait(false);
        }

        private string GetMergeRowStyle(MergeJobSummary job, int rowNumber)
        {
            _ = rowNumber;

            if (job == null)
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(this.selectedMergeId))
            {
                return string.Empty;
            }

            if (!string.Equals(job.MergeId, this.selectedMergeId, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "background-color: var(--mud-palette-primary-lighten);";
        }

        private async Task OnSelectedBatchChangedAsync(int batchId)
        {
            this.selectedBatchId = batchId;
            await this.LoadBatchPreviewAsync().ConfigureAwait(false);
        }

        private async Task RefreshAsync()
        {
            if (this.refreshing)
            {
                return;
            }

            this.refreshing = true;
            try
            {
                await this.RefreshInternalAsync().ConfigureAwait(false);
            }
            finally
            {
                this.refreshing = false;
            }
        }

        private async Task RefreshInternalAsync()
        {
            try
            {
                this.loading = true;
                this.error = string.Empty;
                var reply = await this.ForgeClient.ListMergeJobsAsync(new ListMergeJobsRequest { Take = 500 }).ConfigureAwait(false);
                this.allJobs = reply?.Jobs == null ? new List<MergeJobSummary>() : new List<MergeJobSummary>(reply.Jobs);
                this.ApplyDateFilter();
            }
            catch (Exception ex)
            {
                this.allJobs = new List<MergeJobSummary>();
                this.jobs = new List<MergeJobSummary>();
                this.error = ex.Message;
            }
            finally
            {
                this.loading = false;
            }
        }

        private void ApplyDateFilter()
        {
            if (this.allJobs.Count == 0)
            {
                this.jobs = new List<MergeJobSummary>();
                return;
            }

            if (!this.filterFrom.HasValue && !this.filterTo.HasValue)
            {
                this.jobs = new List<MergeJobSummary>(this.allJobs);
                return;
            }

            DateTimeOffset? fromUtc = null;
            if (this.filterFrom.HasValue)
            {
                fromUtc = new DateTimeOffset(this.filterFrom.Value.Date, TimeZoneInfo.Local.GetUtcOffset(this.filterFrom.Value.Date)).ToUniversalTime();
            }

            DateTimeOffset? toUtcExclusive = null;
            if (this.filterTo.HasValue)
            {
                var toLocalExclusive = this.filterTo.Value.Date.AddDays(1);
                toUtcExclusive = new DateTimeOffset(toLocalExclusive, TimeZoneInfo.Local.GetUtcOffset(toLocalExclusive)).ToUniversalTime();
            }

            var filtered = new List<MergeJobSummary>();
            foreach (var job in this.allJobs)
            {
                if (job == null)
                {
                    continue;
                }

                if (!TryParseUtc(job.StartedUtc, out var startedUtc))
                {
                    continue;
                }

                if (fromUtc.HasValue && startedUtc < fromUtc.Value)
                {
                    continue;
                }

                if (toUtcExclusive.HasValue && startedUtc >= toUtcExclusive.Value)
                {
                    continue;
                }

                filtered.Add(job);
            }

            this.jobs = filtered;
        }

        private static bool TryParseUtc(string? value, out DateTimeOffset parsed)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                parsed = default;
                return false;
            }

            if (DateTimeOffset.TryParse(value, out parsed))
            {
                return true;
            }

            parsed = default;
            return false;
        }

        private async Task LoadPreviewAsync(string mergeId)
        {
            try
            {
                this.previewBusy = true;
                this.previewError = string.Empty;
                this.selectedDetail = null;
                this.batchOptions = new List<int>();
                this.selectedBatchId = -1;
                this.batchPreviewRaw = string.Empty;
                this.batchPreviewColumns = new List<string>();
                this.batchPreviewTableRows = new List<Dictionary<string, string>>();
                this.batchPreviewTreeRoots = new List<JsonTreeNode>();

                this.selectedDetail = await this.ForgeClient.GetMergeJobDetailAsync(new GetMergeJobDetailRequest
                {
                    MergeId = mergeId,
                }).ConfigureAwait(false);

                this.batchOptions = this.selectedDetail?.Batches == null
                    ? new List<int>()
                    : this.selectedDetail.Batches.Select(b => b.BatchId).Distinct().OrderByDescending(b => b).ToList();

                if (this.batchOptions.Count > 0)
                {
                    this.selectedBatchId = this.batchOptions[0];
                    await this.LoadBatchPreviewAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.previewError = ex.Message;
            }
            finally
            {
                this.previewBusy = false;
            }
        }

        private async Task LoadBatchPreviewAsync()
        {
            if (this.selectedDetail == null || this.selectedBatchId < 0)
            {
                this.batchPreviewRaw = string.Empty;
                this.batchPreviewColumns = new List<string>();
                this.batchPreviewTableRows = new List<Dictionary<string, string>>();
                this.batchPreviewTreeRoots = new List<JsonTreeNode>();
                return;
            }

            try
            {
                var reply = await this.ForgeClient.PreviewMergeBatchAsync(new PreviewMergeBatchRequest
                {
                    MergeId = this.selectedDetail.Summary?.MergeId ?? string.Empty,
                    BatchId = this.selectedBatchId,
                    Take = 50,
                    Skip = 0,
                }).ConfigureAwait(false);

                if (reply == null || !reply.Success)
                {
                    this.batchPreviewRaw = string.Empty;
                    this.batchPreviewColumns = new List<string>();
                    this.batchPreviewTableRows = new List<Dictionary<string, string>>();
                    this.batchPreviewTreeRoots = new List<JsonTreeNode>();
                    this.previewError = reply?.Message ?? "Batch preview failed.";
                    return;
                }

                var rows = reply?.Rows == null ? Array.Empty<string>() : reply.Rows.ToArray();
                this.batchPreviewRaw = string.Join("\n", rows);
                this.BuildFormattedRowsPreview(rows);
            }
            catch (Exception ex)
            {
                this.batchPreviewRaw = string.Empty;
                this.batchPreviewColumns = new List<string>();
                this.batchPreviewTableRows = new List<Dictionary<string, string>>();
                this.batchPreviewTreeRoots = new List<JsonTreeNode>();
                this.previewError = ex.Message;
            }
        }

        private void BuildFormattedRowsPreview(string[] rows)
        {
            this.batchPreviewColumns = new List<string>();
            this.batchPreviewTableRows = new List<Dictionary<string, string>>();
            this.batchPreviewTreeRoots = new List<JsonTreeNode>();

            if (rows == null || rows.Length == 0)
            {
                return;
            }

            var parsedRows = new List<JsonElement>();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shouldUseTree = false;

            for (var i = 0; i < rows.Length; i++)
            {
                var line = rows[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        shouldUseTree = true;
                        parsedRows.Add(doc.RootElement.Clone());
                        continue;
                    }

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        columns.Add(prop.Name);
                        if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            shouldUseTree = true;
                        }
                    }

                    parsedRows.Add(doc.RootElement.Clone());
                }
                catch
                {
                    shouldUseTree = true;
                }
            }

            if (!shouldUseTree)
            {
                this.batchPreviewColumns = columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var element in parsedRows)
                {
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in element.EnumerateObject())
                    {
                        row[prop.Name] = ToDisplayValue(prop.Value);
                    }

                    this.batchPreviewTableRows.Add(row);
                }

                return;
            }

            var roots = new List<JsonTreeNode>();
            for (var i = 0; i < rows.Length; i++)
            {
                var line = rows[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var children = ToTreeNodes(doc.RootElement);
                    roots.Add(new JsonTreeNode("Row " + (roots.Count + 1).ToString(), value: null, children));
                }
                catch
                {
                    roots.Add(new JsonTreeNode("Row " + (roots.Count + 1).ToString(), line));
                }
            }

            this.batchPreviewTreeRoots = roots;
        }

        private static IReadOnlyList<JsonTreeNode> ToTreeNodes(JsonElement element)
        {
            var children = new List<JsonTreeNode>();

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    children.Add(ToTreeNode(prop.Name, prop.Value));
                }

                return children;
            }

            children.Add(ToTreeNode("Value", element));
            return children;
        }

        private static JsonTreeNode ToTreeNode(string name, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var children = new List<JsonTreeNode>();
                foreach (var prop in value.EnumerateObject())
                {
                    children.Add(ToTreeNode(prop.Name, prop.Value));
                }

                return new JsonTreeNode(name, value: null, children);
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var children = new List<JsonTreeNode>();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    children.Add(ToTreeNode("[" + index.ToString() + "]", item));
                    index++;
                }

                return new JsonTreeNode(name, value: null, children);
            }

            return new JsonTreeNode(name, ToDisplayValue(value));
        }

        private static string ToDisplayValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString() ?? string.Empty;

                case JsonValueKind.Number:
                    return value.GetRawText();

                case JsonValueKind.True:
                    return "true";

                case JsonValueKind.False:
                    return "false";

                case JsonValueKind.Null:
                    return string.Empty;

                default:
                    return value.GetRawText();
            }
        }
    }
}
