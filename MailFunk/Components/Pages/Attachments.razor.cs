//-----------------------------------------------------------------------
// <copyright file="Attachments.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;
    using Microsoft.JSInterop;
    using MudBlazor;

    /// <summary>
    /// Provides an admin UI for searching and managing uploaded attachments.
    /// </summary>
    public partial class Attachments : ComponentBase
    {
        private const int DefaultTake = 50;
        private const int MaxTake = 500;
        private const int DefaultLargeThresholdMb = 10;

        private readonly List<AttachmentListItem> items = new List<AttachmentListItem>();
        private readonly List<string> pageHistory = new List<string>();

        [Inject]
        private MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        [Inject]
        private ISnackbar Snackbar { get; set; } = default!;

        [Inject]
        private IJSRuntime JsRuntime { get; set; } = default!;

        private bool busy;
        private string error = string.Empty;

        private string filterClientId = string.Empty;
        private string filterMergeOwnerId = string.Empty;
        private bool onlyOrphans;
        private bool onlyLarge;
        private int largeThresholdMb = DefaultLargeThresholdMb;
        private DateTime? olderThan;

        private int take = DefaultTake;
        private int total;
        private string pageToken = string.Empty;
        private string nextPageToken = string.Empty;

        private bool canPrev;
        private bool canNext;

        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            await this.RefreshAsync().ConfigureAwait(false);
        }

        private async Task RefreshAsync()
        {
            this.pageHistory.Clear();
            this.pageToken = string.Empty;
            await this.LoadAsync(reset: true).ConfigureAwait(false);
        }

        private async Task ApplyFiltersAsync()
        {
            await this.RefreshAsync().ConfigureAwait(false);
        }

        private async Task ClearFiltersAsync()
        {
            this.filterClientId = string.Empty;
            this.filterMergeOwnerId = string.Empty;
            this.onlyOrphans = false;
            this.onlyLarge = false;
            this.largeThresholdMb = DefaultLargeThresholdMb;
            this.olderThan = null;

            await this.RefreshAsync().ConfigureAwait(false);
        }

        private async Task NextPageAsync()
        {
            if (string.IsNullOrWhiteSpace(this.nextPageToken))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(this.pageToken))
            {
                this.pageHistory.Add(this.pageToken);
            }
            else
            {
                this.pageHistory.Add(string.Empty);
            }

            this.pageToken = this.nextPageToken;
            await this.LoadAsync(reset: false).ConfigureAwait(false);
        }

        private async Task PrevPageAsync()
        {
            if (this.pageHistory.Count <= 0)
            {
                return;
            }

            var lastIndex = this.pageHistory.Count - 1;
            this.pageToken = this.pageHistory[lastIndex];
            this.pageHistory.RemoveAt(lastIndex);
            await this.LoadAsync(reset: false).ConfigureAwait(false);
        }

        private async Task LoadAsync(bool reset)
        {
            if (this.busy)
            {
                return;
            }

            this.busy = true;
            this.error = string.Empty;

            try
            {
                await Task.Yield();

                var thresholdBytes = (long)Math.Max(1, this.largeThresholdMb) * 1024L * 1024L;

                AttachmentListResult result;

                if (this.onlyOrphans && !this.onlyLarge)
                {
                    var req = new PreviewOrphansRequest
                    {
                        ClientId = this.filterClientId ?? string.Empty,
                        MergeOwnerId = this.filterMergeOwnerId ?? string.Empty,
                        OlderThanUtc = this.olderThan.HasValue ? this.olderThan.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty,
                        PageToken = this.pageToken ?? string.Empty,
                        Take = this.ClampTake(this.take),
                    };

                    result = await this.ListOrphansAsync(req).ConfigureAwait(false);
                }
                else if (this.onlyLarge && !this.onlyOrphans)
                {
                    var req = new PreviewLargeAttachmentsRequest
                    {
                        ClientId = this.filterClientId ?? string.Empty,
                        MergeOwnerId = this.filterMergeOwnerId ?? string.Empty,
                        LargeThresholdBytes = thresholdBytes,
                        PageToken = this.pageToken ?? string.Empty,
                        Take = this.ClampTake(this.take),
                    };

                    result = await this.ListLargeAsync(req).ConfigureAwait(false);
                }
                else
                {
                    var req = new ListAttachmentsRequest
                    {
                        ClientId = this.filterClientId ?? string.Empty,
                        MergeOwnerId = this.filterMergeOwnerId ?? string.Empty,
                        OlderThanUtc = this.olderThan.HasValue ? this.olderThan.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty,
                        OnlyOrphans = this.onlyOrphans,
                        OnlyLarge = this.onlyLarge,
                        LargeThresholdBytes = this.onlyLarge ? thresholdBytes : 0,
                        PageToken = this.pageToken ?? string.Empty,
                        Skip = 0,
                        Take = this.ClampTake(this.take),
                        SortDesc = true,
                    };

                    // Default sort: uploaded desc.
                    req.SortBy = (AttachmentSortBy)1;

                    var reply = await this.GrpcClient.ListAttachmentsAsync(req).ResponseAsync.ConfigureAwait(false);
                    result = new AttachmentListResult(reply.Total, reply.Items, reply.NextPageToken);
                }

                this.total = result.Total;
                this.items.Clear();
                this.items.AddRange(result.Items);

                this.nextPageToken = result.NextPageToken ?? string.Empty;

                this.canPrev = this.pageHistory.Count > 0;
                this.canNext = !string.IsNullOrWhiteSpace(this.nextPageToken);

                if (reset)
                {
                    this.pageHistory.Clear();
                }
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
                await this.InvokeAsync(this.StateHasChanged).ConfigureAwait(false);
            }
        }

        private async Task<AttachmentListResult> ListOrphansAsync(PreviewOrphansRequest req)
        {
            var reply = await this.GrpcClient.PreviewOrphansAsync(req).ResponseAsync.ConfigureAwait(false);
            return new AttachmentListResult(reply.Total, reply.Items, reply.NextPageToken);
        }

        private async Task<AttachmentListResult> ListLargeAsync(PreviewLargeAttachmentsRequest req)
        {
            var reply = await this.GrpcClient.PreviewLargeAttachmentsAsync(req).ResponseAsync.ConfigureAwait(false);
            return new AttachmentListResult(reply.Total, reply.Items, reply.NextPageToken);
        }

        private int ClampTake(int requested)
        {
            if (requested <= 0)
            {
                return DefaultTake;
            }

            if (requested > MaxTake)
            {
                return MaxTake;
            }

            return requested;
        }

        private string FormatSize(long bytes)
        {
            const double Kb = 1024;
            const double Mb = 1024 * 1024;
            const double Gb = 1024 * 1024 * 1024;

            if (bytes >= (long)Gb)
            {
                return (bytes / Gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= (long)Mb)
            {
                return (bytes / Mb).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            }

            if (bytes >= (long)Kb)
            {
                return (bytes / Kb).ToString("0.00", CultureInfo.InvariantCulture) + " KB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        private async Task CopyTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                await this.JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", token).ConfigureAwait(false);
                this.Snackbar.Add("Token copied.", Severity.Success);
            }
            catch
            {
                this.Snackbar.Add("Failed copying token.", Severity.Warning);
            }
        }

        private async Task ViewManifestAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                var reply = await this.GrpcClient.GetAttachmentManifestAsync(new GetAttachmentManifestRequest { Token = token }).ResponseAsync.ConfigureAwait(false);
                if (!reply.Exists)
                {
                    this.Snackbar.Add("Manifest not found.", Severity.Warning);
                    return;
                }

                // Minimal UX: copy manifest JSON to clipboard.
                await this.JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", reply.ManifestJson ?? string.Empty).ConfigureAwait(false);
                this.Snackbar.Add("Manifest JSON copied.", Severity.Success);
            }
            catch (Exception ex)
            {
                this.Snackbar.Add("Manifest request failed: " + ex.Message, Severity.Warning);
            }
        }

        private async Task DownloadAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            // Minimal implementation: copy a curl hint to clipboard.
            // A full browser download can be implemented later via an API endpoint or JS stream.
            try
            {
                var hint = "Use DownloadAttachment from an API client for token: " + token;
                await this.JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", hint).ConfigureAwait(false);
                this.Snackbar.Add("Download hint copied.", Severity.Info);
            }
            catch
            {
                this.Snackbar.Add("Failed copying download hint.", Severity.Warning);
            }
        }

        private async Task DeleteAsync(string token, bool isOrphan)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                var reply = await this.GrpcClient.DeleteAttachmentAsync(new DeleteAttachmentRequest { Token = token, Force = isOrphan }).ResponseAsync.ConfigureAwait(false);
                if (reply.Success)
                {
                    this.Snackbar.Add("Attachment deleted.", Severity.Success);
                    await this.RefreshAsync().ConfigureAwait(false);
                    return;
                }

                this.Snackbar.Add(string.IsNullOrWhiteSpace(reply.Message) ? "Delete failed." : reply.Message, Severity.Warning);
            }
            catch (Exception ex)
            {
                this.Snackbar.Add("Delete failed: " + ex.Message, Severity.Warning);
            }
        }

        private sealed class AttachmentListResult
        {
            public AttachmentListResult(int total, IEnumerable<AttachmentListItem> items, string? nextPageToken)
            {
                this.Total = total;
                this.Items = items;
                this.NextPageToken = nextPageToken;
            }

            public int Total { get; }

            public IEnumerable<AttachmentListItem> Items { get; }

            public string? NextPageToken { get; }
        }
    }
}
