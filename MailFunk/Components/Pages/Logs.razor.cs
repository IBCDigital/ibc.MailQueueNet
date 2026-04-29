//-----------------------------------------------------------------------
// <copyright file="Logs.razor.cs" company="IBC Digital">
//   Copyright (c) 2025 IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Grpc.Core;
    using MailForge.Grpc;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Log viewer component code-behind. Supports viewing local MailFunk logs and server logs, with optional live streaming for server logs.
    /// Displays a selectable list of log files and a preview pane which shows the content (tail-first for fast initial render).
    /// </summary>
    public partial class Logs : ComponentBase, IDisposable
    {
        [Inject]
        public MailGrpcService.MailGrpcServiceClient Grpc { get; set; } = default!;

        [Inject]
        public MailForgeService.MailForgeServiceClient ForgeGrpc { get; set; } = default!;

        [Inject]
        public IConfiguration Config { get; set; } = default!;

        [Inject]
        public IWebHostEnvironment Env { get; set; } = default!;

        private string source = "mailfunk";
        private string? selectedFile;
        private string[] mfFiles = Array.Empty<string>();
        private string[] srvFiles = Array.Empty<string>();
        private string[] forgeFiles = Array.Empty<string>();
        private string content = string.Empty;
        private bool live;
        private bool listBusy;
        private bool contentBusy;
        private int tailBytes = 128 * 1024; // 128KB initial tail
        private Timer? timer;
        private AsyncServerStreamingCall<ReadLogReply>? stream;
        private CancellationTokenSource? streamCts;

        public void Dispose()
        {
            this.StopStreaming();
        }

        protected override async Task OnInitializedAsync()
        {
            await this.LoadFilesAsync();
        }

        private async Task OnSourceChanged(string value)
        {
            this.source = value;
            this.selectedFile = null;
            this.content = string.Empty;
            this.live = false;
            this.StopStreaming();
            await this.LoadFilesAsync();
            await this.InvokeAsync(this.StateHasChanged);
        }

        private async Task LoadFilesAsync()
        {
            this.listBusy = true;
            this.StateHasChanged();

            if (this.source == "mailfunk")
            {
                var path = this.ResolveMailFunkLogsFolder();
                try
                {
                    this.mfFiles = Directory.GetFiles(path, "log_*.txt").Select(Path.GetFileName).OrderByDescending(x => x).ToArray();
                }
                catch
                {
                    this.mfFiles = Array.Empty<string>();
                }
            }
            else if (this.source == "server")
            {
                try
                {
                    var list = await this.Grpc.ListServerLogFilesAsync(new ListLogsRequest());
                    this.srvFiles = list.Files.Select(f => f.Name ?? string.Empty).ToArray();
                }
                catch
                {
                    this.srvFiles = Array.Empty<string>();
                }
            }
            else
            {
                try
                {
                    var list = await this.ForgeGrpc.ListWorkerLogFilesAsync(new ListLogsRequest());
                    this.forgeFiles = list.Files.Select(f => f.Name ?? string.Empty).ToArray();
                }
                catch
                {
                    this.forgeFiles = Array.Empty<string>();
                }
            }

            this.listBusy = false;
            await this.InvokeAsync(this.StateHasChanged);
        }

        private async Task OnFileSelectedAsync(string file)
        {
            this.selectedFile = file;
            this.content = string.Empty;
            this.StopStreaming();
            await this.InvokeAsync(this.StateHasChanged);
            await this.LoadContentAsync();
        }

        private async Task LoadContentAsync()
        {
            this.StopStreaming();
            this.contentBusy = true;
            this.StateHasChanged();

            if (string.IsNullOrWhiteSpace(this.selectedFile))
            {
                this.contentBusy = false;
                this.content = string.Empty;
                this.StateHasChanged();
                return;
            }

            if (this.source == "mailfunk")
            {
                var path = Path.Combine(this.ResolveMailFunkLogsFolder(), this.selectedFile);
                try
                {
                    this.content = await this.ReadTailAsync(path, this.tailBytes);
                }
                catch
                {
                    this.content = string.Empty;
                }

                this.contentBusy = false;
                this.StateHasChanged();
                this.SetupTimer();
            }
            else
            {
                if (this.live)
                {
                    await this.StartRemoteStreamingAsync();
                }
                else
                {
                    try
                    {
                        ReadLogReply reply;
                        if (this.source == "server")
                        {
                            reply = await this.Grpc.ReadServerLogAsync(new ReadLogRequest { Name = this.selectedFile, TailBytes = this.tailBytes });
                        }
                        else
                        {
                            reply = await this.ForgeGrpc.ReadWorkerLogAsync(new ReadLogRequest { Name = this.selectedFile, TailBytes = this.tailBytes });
                        }

                        this.content = reply.Content ?? string.Empty;
                    }
                    catch
                    {
                        this.content = string.Empty;
                    }

                    this.contentBusy = false;
                    this.StateHasChanged();
                }
            }
        }

        private async Task StartRemoteStreamingAsync()
        {
            if (string.IsNullOrWhiteSpace(this.selectedFile))
            {
                this.contentBusy = false;
                this.StateHasChanged();
                return;
            }

            this.content = string.Empty;
            this.StateHasChanged();

            this.streamCts = new CancellationTokenSource();
            try
            {
                if (this.source == "server")
                {
                    this.stream = this.Grpc.StreamServerLog(new ReadLogRequest { Name = this.selectedFile, TailBytes = this.tailBytes }, cancellationToken: this.streamCts.Token);
                }
                else
                {
                    this.stream = this.ForgeGrpc.StreamWorkerLog(new ReadLogRequest { Name = this.selectedFile, TailBytes = this.tailBytes }, cancellationToken: this.streamCts.Token);
                }

                var first = true;
                await foreach (var msg in this.stream.ResponseStream.ReadAllAsync(this.streamCts.Token))
                {
                    this.content += msg.Content;
                    if (first)
                    {
                        first = false;
                        this.contentBusy = false;
                    }

                    this.StateHasChanged();
                }
            }
            catch
            {
                this.contentBusy = false;
            }
        }

        private async Task<string> ReadTailAsync(string path, int maxTailBytes)
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            long toRead = Math.Min(len, maxTailBytes);
            fs.Seek(-toRead, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return await sr.ReadToEndAsync();
        }

        private string ResolveMailFunkLogsFolder()
        {
            var section = this.Config.GetSection("FileLogging");
            var configuredPath = section["Path"];
            var basePath = this.Env.ContentRootPath ?? AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.Combine(basePath, "Logs");
            }

            return Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(basePath, configuredPath);
        }

        private void SetupTimer()
        {
            this.timer?.Dispose();
            if (!this.live || string.IsNullOrWhiteSpace(this.selectedFile) || this.source != "mailfunk")
            {
                return;
            }

            this.timer = new Timer(async _ => await this.InvokeAsync(async () => await this.LoadContentAsync()), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void StopStreaming()
        {
            try
            {
                this.streamCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                this.stream?.Dispose();
            }
            catch
            {
            }

            this.stream = null;
            this.streamCts = null;
            this.timer?.Dispose();
        }
    }
}
