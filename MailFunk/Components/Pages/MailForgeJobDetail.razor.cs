//-----------------------------------------------------------------------
// <copyright file="MailForgeJobDetail.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MailForge.Grpc;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Displays detailed information for a single MailForge merge job.
    /// </summary>
    public sealed partial class MailForgeJobDetail
    {
        private bool loading = true;
        private bool refreshing;
        private string error = string.Empty;
        private GetMergeJobDetailReply? detail;

        private bool previewOpen;
        private bool previewLoading;
        private int previewBatchId = -1;
        private string previewError = string.Empty;
        private PreviewMergeBatchReply? previewReply;

        [Inject]
        private MailForgeService.MailForgeServiceClient ForgeClient { get; set; } = default!;

        /// <summary>
        /// Gets the merge identifier.
        /// </summary>
        [Parameter]
        public string MergeId { get; set; } = string.Empty;

        /// <inheritdoc />
        protected override async Task OnParametersSetAsync()
        {
            await this.RefreshInternalAsync().ConfigureAwait(false);
        }

        private async Task PauseAsync()
        {
            await this.CallAndRefreshAsync(async ct => await this.ForgeClient.PauseMergeJobAsync(new PauseMergeJobRequest
            {
                JobId = this.MergeId,
            }, cancellationToken: ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private async Task ResumeAsync()
        {
            await this.CallAndRefreshAsync(async ct => await this.ForgeClient.ResumeMergeJobAsync(new ResumeMergeJobRequest
            {
                JobId = this.MergeId,
            }, cancellationToken: ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private async Task DeleteAsync()
        {
            await this.CallAndRefreshAsync(async ct => await this.ForgeClient.DeleteMergeJobAsync(new DeleteMergeJobRequest
            {
                MergeId = this.MergeId,
            }, cancellationToken: ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private async Task OpenBatchPreviewAsync(int batchId)
        {
            if (batchId < 0)
            {
                return;
            }

            this.previewOpen = true;
            this.previewLoading = true;
            this.previewBatchId = batchId;
            this.previewError = string.Empty;
            this.previewReply = null;

            try
            {
                this.previewReply = await this.ForgeClient.PreviewMergeBatchAsync(new PreviewMergeBatchRequest
                {
                    MergeId = this.MergeId,
                    BatchId = batchId,
                    Take = 50,
                    Skip = 0,
                }).ConfigureAwait(false);

                if (this.previewReply == null || !this.previewReply.Success)
                {
                    this.previewError = this.previewReply?.Message ?? "Preview failed.";
                }
            }
            catch (Exception ex)
            {
                this.previewReply = null;
                this.previewError = ex.Message;
            }
            finally
            {
                this.previewLoading = false;
            }
        }

        private void CloseBatchPreview()
        {
            this.previewOpen = false;
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
            if (string.IsNullOrWhiteSpace(this.MergeId))
            {
                this.detail = null;
                this.loading = false;
                return;
            }

            try
            {
                this.loading = true;
                this.error = string.Empty;

                this.detail = await this.ForgeClient.GetMergeJobDetailAsync(new GetMergeJobDetailRequest
                {
                    MergeId = this.MergeId,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.detail = null;
                this.error = ex.Message;
            }
            finally
            {
                this.loading = false;
            }
        }

        private async Task CallAndRefreshAsync(Func<CancellationToken, Task> action)
        {
            if (this.refreshing)
            {
                return;
            }

            if (action == null)
            {
                return;
            }

            this.refreshing = true;
            try
            {
                this.error = string.Empty;
                await action(CancellationToken.None).ConfigureAwait(false);
                await this.RefreshInternalAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.refreshing = false;
            }
        }
    }
}
