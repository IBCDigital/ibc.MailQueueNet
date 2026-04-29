//-----------------------------------------------------------------------
// <copyright file="FileMergeBatchStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Implements <see cref="IMergeBatchStore"/> using an append-only JSONL file per merge id.
    /// </summary>
    public sealed class FileMergeBatchStore : IMergeBatchStore
    {
        /// <summary>
        /// The environment variable name used to configure the batch output folder.
        /// </summary>
        private const string BatchFolderEnvironmentVariableName = "MAILFORGE_BATCH_FOLDER";

        private readonly ILogger<FileMergeBatchStore> logger;

        /// <summary>
        /// Initialises a new instance of the <see cref="FileMergeBatchStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public FileMergeBatchStore(ILogger<FileMergeBatchStore> logger)
        {
            this.logger = logger;
        }

        /// <inheritdoc/>
        public Task<int> AppendAsync(string mergeId, string[] jsonLines, CancellationToken cancellationToken)
        {
            return this.AppendAsync(mergeId, string.Empty, -1, jsonLines, cancellationToken);
        }

        /// <summary>
        /// Appends merge rows to a per-batch file for the supplied merge id.
        /// </summary>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="templateFileName">Template file name in the queue merge folder.</param>
        /// <param name="batchId">Batch identifier.</param>
        /// <param name="jsonLines">JSONL lines to append.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of non-empty rows appended.</returns>
        public async Task<int> AppendAsync(string mergeId, string templateFileName, int batchId, string[] jsonLines, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                throw new ArgumentException("Merge id is required.", nameof(mergeId));
            }

            if (jsonLines == null || jsonLines.Length == 0)
            {
                return 0;
            }

            var baseFolder = this.ResolveAndEnsureBatchFolder();

            var fullPath = ResolveBatchPath(baseFolder, mergeId, templateFileName, batchId);

            try
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        var info = new FileInfo(fullPath);
                        if (info.Length > 0)
                        {
                            return 0;
                        }
                    }
                }
                catch
                {
                }

                await using var fs = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                await using var sw = new StreamWriter(fs, Encoding.UTF8);

                var appended = 0;
                foreach (var line in jsonLines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    await sw.WriteLineAsync(line).ConfigureAwait(false);
                    appended++;
                }

                return appended;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to append merge batch. MergeId={MergeId}; Template={Template}; BatchId={BatchId}", mergeId, templateFileName, batchId);
                throw;
            }
        }

        private string ResolveAndEnsureBatchFolder()
        {
            var configuredBaseFolder = Environment.GetEnvironmentVariable(BatchFolderEnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(configuredBaseFolder))
            {
                Directory.CreateDirectory(configuredBaseFolder);
                return configuredBaseFolder;
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

        private static string ResolveBatchPath(string baseFolder, string mergeId, string templateFileName, int batchId)
        {
            if (!string.IsNullOrWhiteSpace(templateFileName) && batchId >= 0)
            {
                var safeTemplateName = Path.GetFileName(templateFileName);
                return Path.Combine(baseFolder, mergeId + "__" + safeTemplateName + "__" + batchId.ToString() + ".jsonl");
            }

            return Path.Combine(baseFolder, mergeId + ".jsonl");
        }
    }
}
