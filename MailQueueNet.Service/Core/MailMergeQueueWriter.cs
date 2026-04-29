//-----------------------------------------------------------------------
// <copyright file="MailMergeQueueWriter.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using MailQueueNet.Common.FileExtensions;
    using MailQueueNet.Service.Utilities;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Writes mail-merge queue items to the dedicated mail-merge queue folder.
    /// </summary>
    public sealed class MailMergeQueueWriter
    {
        private readonly ILogger<MailMergeQueueWriter> logger;

        private readonly object gate = new object();

        private int mergeIdCounter;

        /// <summary>
        /// Initialises a new instance of the <see cref="MailMergeQueueWriter"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public MailMergeQueueWriter(ILogger<MailMergeQueueWriter> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Enqueues a mail merge batch by ensuring a single template file exists for the
        /// supplied <paramref name="mergeId"/> and then writing the batch merge rows to a
        /// JSONL file that shares the template base name.
        /// </summary>
        /// <param name="settings">Current service settings.</param>
        /// <param name="message">Mail message with optional settings.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="jsonLines">Merge rows (JSONL lines) for this request.</param>
        /// <returns>
        /// Tuple describing the created template and batch. <see langword="null"/> when
        /// the write fails.
        /// </returns>
        public (string TemplateFileName, int BatchId)? TryQueue(Grpc.Settings settings, Grpc.MailMessageWithSettings message, string mergeId, string[]? jsonLines)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrWhiteSpace(mergeId))
            {
                throw new ArgumentException("Merge id is required.", nameof(mergeId));
            }

            var folder = this.ResolveMergeFolder(settings);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            var templatePath = this.EnsureTemplateFile(folder, message, mergeId);
            if (string.IsNullOrWhiteSpace(templatePath))
            {
                return null;
            }

            var batchId = this.AllocateBatchId(templatePath);
            if (!this.TryWriteBatchJsonl(templatePath, batchId, jsonLines))
            {
                return null;
            }

            var lineCount = jsonLines == null ? 0 : jsonLines.Count(l => !string.IsNullOrWhiteSpace(l));
            this.logger.LogInformation(
                "Mail merge batch written (MergeId={MergeId}; Template={Template}; BatchId={BatchId}; Lines={Lines}; Folder={Folder})",
                mergeId,
                Path.GetFileName(templatePath),
                batchId,
                lineCount,
                folder);

            return (Path.GetFileName(templatePath), batchId);
        }

        /// <summary>
        /// Attempts to delete a previously written JSONL batch file.
        /// </summary>
        /// <param name="settings">Current service settings.</param>
        /// <param name="templateFileName">Template file name returned by queueing.</param>
        /// <param name="batchId">Batch identifier to delete.</param>
        /// <returns><see langword="true"/> when the file is deleted or does not exist; otherwise <see langword="false"/>.</returns>
        public bool TryDeleteBatch(Grpc.Settings settings, string templateFileName, int batchId)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (string.IsNullOrWhiteSpace(templateFileName))
            {
                throw new ArgumentException("Template file name is required.", nameof(templateFileName));
            }

            if (batchId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(batchId));
            }

            var folder = this.ResolveMergeFolder(settings);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var templatePath = Path.Combine(folder, templateFileName);
            var batchPath = GetBatchPath(templatePath, batchId);

            try
            {
                if (!File.Exists(batchPath))
                {
                    return true;
                }

                // Do not delete the file as batch ids are allocated by scanning existing files.
                // Instead, truncate the batch to a zero-length tombstone so subsequent batches
                // receive monotonically increasing batch ids even after acknowledgements.
                using (var fs = new FileStream(batchPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    fs.SetLength(0);
                }

                return true;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed deleting merge batch file. Template={Template} BatchId={BatchId} Path={Path}", templateFileName, batchId, batchPath);
                return false;
            }
        }

        /// <summary>
        /// Attempts to delete the template file for a merge id.
        /// </summary>
        /// <param name="settings">Current service settings.</param>
        /// <param name="templateFileName">Template file name.</param>
        /// <returns><see langword="true"/> when deleted or not present; otherwise <see langword="false"/>.</returns>
        public bool TryDeleteTemplate(Grpc.Settings settings, string templateFileName)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (string.IsNullOrWhiteSpace(templateFileName))
            {
                throw new ArgumentException("Template file name is required.", nameof(templateFileName));
            }

            var folder = this.ResolveMergeFolder(settings);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var templatePath = Path.Combine(folder, templateFileName);
            try
            {
                if (!File.Exists(templatePath))
                {
                    return true;
                }

                File.Delete(templatePath);
                return true;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed deleting merge template file. Template={Template} Path={Path}", templateFileName, templatePath);
                return false;
            }
        }

        /// <summary>
        /// Determines whether a merge id can accept a new batch based on the presence of a template file.
        /// </summary>
        /// <param name="settings">Current service settings.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <returns><see langword="true"/> if a template exists; otherwise <see langword="false"/>.</returns>
        public bool HasTemplate(Grpc.Settings settings, string mergeId)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return false;
            }

            var folder = this.ResolveMergeFolder(settings);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var existing = this.TryFindExistingTemplate(folder, mergeId);
            return !string.IsNullOrWhiteSpace(existing);
        }

        private static string GetBatchPath(string templatePath, int batchId)
        {
            return templatePath + "." + batchId.ToString(CultureInfo.InvariantCulture) + ".jsonl";
        }

        private string ResolveMergeFolder(Grpc.Settings settings)
        {
            var rawFolder = settings.MailMergeQueueFolder;
            string folder = rawFolder;
            try
            {
                folder = Files.MapPath(rawFolder);
            }
            catch
            {
            }

            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Mail merge queue folder cannot be created. Folder='{Folder}'", folder);
                return string.Empty;
            }
        }

        private string EnsureTemplateFile(string folder, Grpc.MailMessageWithSettings message, string mergeId)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return string.Empty;
            }

            lock (this.gate)
            {
                var existing = this.TryFindExistingTemplate(folder, mergeId);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    this.logger.LogDebug(
                        "Using existing mail merge template (MergeId={MergeId}; Template={Template}; Folder={Folder})",
                        mergeId,
                        Path.GetFileName(existing),
                        folder);

                    return existing;
                }

                var counter = Interlocked.Increment(ref this.mergeIdCounter);
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyyMMddHHmmss}_{1}_{2}.mail",
                    DateTime.UtcNow,
                    mergeId,
                    counter.ToString(CultureInfo.InvariantCulture).PadLeft(8, '0'));

                var destPath = Path.Combine(folder, fileName);

                var tempPath = Files.CreateEmptyTempFile();
                if (string.IsNullOrWhiteSpace(tempPath))
                {
                    this.logger.LogError("Failed to create temp file for mail merge template.");
                    return string.Empty;
                }

                try
                {
                    if (!FileUtils.WriteMailToFile(message, tempPath))
                    {
                        return string.Empty;
                    }

                    File.Move(tempPath, destPath, false);

                    this.logger.LogInformation(
                        "Created new mail merge template (MergeId={MergeId}; Template={Template}; Path={Path})",
                        mergeId,
                        fileName,
                        destPath);

                    return destPath;
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Failed to enqueue mail merge template. Path='{Path}'", destPath);
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                    }

                    return string.Empty;
                }
            }
        }

        private string TryFindExistingTemplate(string folder, string mergeId)
        {
            try
            {
                var pattern = "*_" + mergeId + "_*.mail";
                var candidates = Directory.GetFiles(folder, pattern).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (candidates.Length > 0)
                {
                    return candidates[0];
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private int AllocateBatchId(string templatePath)
        {
            lock (this.gate)
            {
                var folder = Path.GetDirectoryName(templatePath) ?? string.Empty;
                var baseName = Path.GetFileName(templatePath);
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(baseName))
                {
                    return 0;
                }

                var prefix = baseName + ".";

                int max = -1;
                try
                {
                    foreach (var file in Directory.GetFiles(folder, baseName + ".*.jsonl"))
                    {
                        var name = Path.GetFileName(file);
                        if (name == null)
                        {
                            continue;
                        }

                        if (!name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var remainder = name.Substring(prefix.Length);
                        var idx = remainder.IndexOf(".jsonl", StringComparison.Ordinal);
                        if (idx <= 0)
                        {
                            continue;
                        }

                        var numberPart = remainder.Substring(0, idx);
                        if (int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        {
                            if (id > max)
                            {
                                max = id;
                            }
                        }
                    }
                }
                catch
                {
                    return 0;
                }

                return max + 1;
            }
        }

        private bool TryWriteBatchJsonl(string templatePath, int batchId, string[]? jsonLines)
        {
            var target = GetBatchPath(templatePath, batchId);

            var tempPath = Files.CreateEmptyTempFile();
            if (string.IsNullOrWhiteSpace(tempPath))
            {
                this.logger.LogError("Failed to create temp file for mail merge batch.");
                return false;
            }

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    if (jsonLines != null && jsonLines.Length > 0)
                    {
                        foreach (var line in jsonLines)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                continue;
                            }

                            sw.WriteLine(line);
                        }
                    }
                }

                File.Move(tempPath, target, false);
                return true;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to enqueue mail merge batch. Path='{Path}'", target);
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                return false;
            }
        }
    }
}
