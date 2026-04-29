// <copyright file="DiskAttachmentStore.cs" company="IBC Digital">
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
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Stores uploaded attachment bytes on local disk under a dedicated folder.
    /// </summary>
    internal sealed class DiskAttachmentStore
    {
        private const int DefaultBufferSize = 64 * 1024;

        private readonly AttachmentStoreOptions options;
        private readonly IAttachmentIndexNotifier indexNotifier;

        /// <summary>
        /// Initialises a new instance of the <see cref="DiskAttachmentStore"/> class.
        /// </summary>
        /// <param name="options">Attachment store options.</param>
        /// <param name="indexNotifier">
        /// Optional notifier used to update an external attachment index. When not supplied, a no-op notifier is used.
        /// </param>
        public DiskAttachmentStore(AttachmentStoreOptions options, IAttachmentIndexNotifier? indexNotifier = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.indexNotifier = indexNotifier ?? NullAttachmentIndexNotifier.Instance;

            if (string.IsNullOrWhiteSpace(this.options.BaseFolder))
            {
                throw new ArgumentException("Base folder is required", nameof(options));
            }
        }

        /// <summary>
        /// Streams an attachment upload into a local spool file.
        /// </summary>
        /// <param name="token">Optional token. When null or empty, a new token is generated.</param>
        /// <param name="length">Optional expected length in bytes.</param>
        /// <param name="sha256Base64">Optional expected SHA-256 digest (base64) of the uploaded bytes.</param>
        /// <param name="fileName">Original file name.</param>
        /// <param name="contentType">Content type.</param>
        /// <param name="clientId">Uploading client id if known.</param>
        /// <param name="readChunkAsync">Chunk reader that yields successive byte buffers.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Upload result details.</returns>
        public async Task<AttachmentUploadResult> SaveUploadAsync(
            string? token,
            long? length,
            string? sha256Base64,
            string? fileName,
            string? contentType,
            string? clientId,
            Func<CancellationToken, Task<byte[]?>> readChunkAsync,
            CancellationToken ct)
        {
            if (readChunkAsync == null)
            {
                throw new ArgumentNullException(nameof(readChunkAsync));
            }

            Directory.CreateDirectory(this.options.BaseFolder);

            var effectiveToken = string.IsNullOrWhiteSpace(token) ? Guid.NewGuid().ToString("N") : token;
            if (!IsSafeToken(effectiveToken))
            {
                return new AttachmentUploadResult
                {
                    Success = false,
                    Token = string.Empty,
                    ReceivedBytes = 0,
                    Message = "Invalid token",
                };
            }

            var (folder, dataPath, readyPath, manifestPath, uploadingManifestPath) = this.GetPaths(effectiveToken);

            Directory.CreateDirectory(folder);

            long received = 0;
            try
            {
                var uploadingManifest = new AttachmentStoreManifest
                {
                    Token = effectiveToken,
                    ClientId = clientId ?? string.Empty,
                    FileName = fileName ?? string.Empty,
                    ContentType = contentType ?? string.Empty,
                    ContentId = string.Empty,
                    Inline = false,
                    Length = 0,
                    Sha256Base64 = string.Empty,
                    UploadedUtc = DateTimeOffset.UtcNow,
                    RefCount = 0,
                    MergeOwnerId = string.Empty,
                };

                await File.WriteAllTextAsync(uploadingManifestPath, JsonSerializer.Serialize(uploadingManifest), Encoding.UTF8, ct).ConfigureAwait(false);

                await using var fs = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var sha = SHA256.Create();

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var chunk = await readChunkAsync(ct).ConfigureAwait(false);
                    if (chunk == null || chunk.Length == 0)
                    {
                        break;
                    }

                    received += chunk.Length;

                    if (received > this.options.MaxUploadBytes)
                    {
                        throw new InvalidOperationException("Attachment exceeds maximum allowed size");
                    }

                    if (length.HasValue && received > length.Value)
                    {
                        throw new InvalidOperationException("Attachment exceeds declared length");
                    }

                    sha.TransformBlock(chunk, 0, chunk.Length, null, 0);
                    await fs.WriteAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                if (length.HasValue && received != length.Value)
                {
                    throw new InvalidOperationException("Attachment length mismatch");
                }

                var actualBase64 = Convert.ToBase64String(sha.Hash ?? Array.Empty<byte>());
                if (!string.IsNullOrWhiteSpace(sha256Base64))
                {
                    if (!FixedTimeEqualsBase64(sha256Base64, actualBase64))
                    {
                        throw new InvalidOperationException("Attachment hash mismatch");
                    }
                }

                var manifest = new AttachmentStoreManifest
                {
                    Token = effectiveToken,
                    ClientId = clientId ?? string.Empty,
                    FileName = fileName ?? string.Empty,
                    ContentType = contentType ?? string.Empty,
                    ContentId = string.Empty,
                    Inline = false,
                    Length = received,
                    Sha256Base64 = actualBase64,
                    UploadedUtc = DateTimeOffset.UtcNow,
                    RefCount = 0,
                    MergeOwnerId = string.Empty,
                };

                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), Encoding.UTF8, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(readyPath, string.Empty, Encoding.UTF8, ct).ConfigureAwait(false);

                TryDeleteFile(uploadingManifestPath);

                return new AttachmentUploadResult
                {
                    Success = true,
                    Token = effectiveToken,
                    ReceivedBytes = received,
                    Message = string.Empty,
                };
            }
            catch (Exception ex)
            {
                TryDeleteFile(dataPath);
                TryDeleteFile(manifestPath);
                TryDeleteFile(readyPath);
                TryDeleteFile(uploadingManifestPath);

                return new AttachmentUploadResult
                {
                    Success = false,
                    Token = string.Empty,
                    ReceivedBytes = received,
                    Message = ex.Message,
                };
            }
        }

        /// <summary>
        /// Resolves the on-disk file path for an attachment token.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        /// <returns>Absolute file path to the stored attachment data.</returns>
        public string ResolveDataPath(string token)
        {
            var (_, dataPath, _, _, _) = this.GetPaths(token);
            return dataPath;
        }

        /// <summary>
        /// Determines whether a stored attachment exists and is ready.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        /// <returns><see langword="true"/> if present and ready; otherwise <see langword="false"/>.</returns>
        public bool ExistsReady(string token)
        {
            var (_, dataPath, readyPath, _, _) = this.GetPaths(token);
            return File.Exists(dataPath) && File.Exists(readyPath);
        }

        /// <summary>
        /// Attempts to increment reference counts for the provided tokens.
        /// </summary>
        /// <param name="tokens">Tokens to reference.</param>
        /// <param name="mergeOwnerId">Optional merge owner identifier.</param>
        public void AddReferences(string[] tokens, string? mergeOwnerId)
        {
            if (tokens == null || tokens.Length == 0)
            {
                return;
            }

            foreach (var token in tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                this.TryUpdateRefCount(token, +1, mergeOwnerId);
            }
        }

        /// <summary>
        /// Attempts to decrement reference counts for the provided tokens.
        /// </summary>
        /// <param name="tokens">Tokens to dereference.</param>
        public void ReleaseReferences(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0)
            {
                return;
            }

            foreach (var token in tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                this.TryUpdateRefCount(token, -1, null);
            }
        }

        /// <summary>
        /// Deletes unreferenced attachments older than the configured TTL.
        /// Also deletes incomplete uploads beyond the configured TTL.
        /// </summary>
        public void Cleanup()
        {
            try
            {
                if (!Directory.Exists(this.options.BaseFolder))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;

                foreach (var manifestPath in Directory.EnumerateFiles(this.options.BaseFolder, "*.manifest.json", SearchOption.AllDirectories))
                {
                    AttachmentStoreManifest? manifest;
                    try
                    {
                        var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                        manifest = JsonSerializer.Deserialize<AttachmentStoreManifest>(json);
                    }
                    catch
                    {
                        continue;
                    }

                    if (manifest == null)
                    {
                        continue;
                    }

                    if (manifest.RefCount > 0)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(manifest.Token))
                    {
                        continue;
                    }

                    if (now - manifest.UploadedUtc < this.options.UnreferencedTtl)
                    {
                        continue;
                    }

                    this.TryDeleteAttachment(manifest.Token);
                }

                foreach (var uploadingPath in Directory.EnumerateFiles(this.options.BaseFolder, "*.manifest.json.uploading", SearchOption.AllDirectories))
                {
                    try
                    {
                        var age = now - File.GetLastWriteTimeUtc(uploadingPath);
                        if (age < this.options.IncompleteUploadTtl)
                        {
                            continue;
                        }

                        var token = Path.GetFileName(uploadingPath)?.Replace(".manifest.json.uploading", string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            this.TryDeleteAttachment(token);
                        }
                        else
                        {
                            TryDeleteFile(uploadingPath);
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
        }

        /// <summary>
        /// Attempts to delete all files for a given attachment token.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        public void TryDeleteAttachment(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var (_, dataPath, readyPath, manifestPath, uploadingManifestPath) = this.GetPaths(token);
            TryDeleteFile(dataPath);
            TryDeleteFile(readyPath);
            TryDeleteFile(manifestPath);
            TryDeleteFile(uploadingManifestPath);

            try
            {
                this.indexNotifier.OnDeleted(token);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Reads attachment metadata for administrative inspection.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        /// <returns>Attachment info if present.</returns>
        public AttachmentInfo GetInfo(string token)
        {
            var info = new AttachmentInfo
            {
                Token = token ?? string.Empty,
            };

            if (string.IsNullOrWhiteSpace(token) || !IsSafeToken(token))
            {
                return info;
            }

            var (_, dataPath, readyPath, manifestPath, uploadingManifestPath) = this.GetPaths(token);

            info.Exists = File.Exists(dataPath) || File.Exists(manifestPath) || File.Exists(uploadingManifestPath);
            info.Ready = File.Exists(readyPath) && File.Exists(manifestPath) && File.Exists(dataPath);

            if (!File.Exists(manifestPath))
            {
                return info;
            }

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<AttachmentStoreManifest>(json);
                if (manifest == null)
                {
                    return info;
                }

                info.Token = manifest.Token ?? token;
                info.Length = manifest.Length;
                info.Sha256Base64 = manifest.Sha256Base64 ?? string.Empty;
                info.UploadedUtc = manifest.UploadedUtc;
                info.RefCount = manifest.RefCount;
                info.FileName = manifest.FileName ?? string.Empty;
                info.ContentType = manifest.ContentType ?? string.Empty;
                info.ClientId = manifest.ClientId ?? string.Empty;
                info.MergeOwnerId = manifest.MergeOwnerId ?? string.Empty;
            }
            catch
            {
            }

            return info;
        }

        /// <summary>
        /// Attempts to delete an attachment token.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        /// <param name="force">When true, deletes even if ref-count is non-zero.</param>
        /// <returns><see langword="true"/> if deleted; otherwise <see langword="false"/>.</returns>
        public bool TryDelete(string token, bool force)
        {
            if (string.IsNullOrWhiteSpace(token) || !IsSafeToken(token))
            {
                return false;
            }

            if (!force)
            {
                var info = this.GetInfo(token);
                if (info.RefCount > 0)
                {
                    return false;
                }
            }

            this.TryDeleteAttachment(token);
            return true;
        }

        /// <summary>
        /// Validates that each supplied token exists on disk and is ready.
        /// </summary>
        /// <param name="tokens">Tokens to validate.</param>
        /// <returns>A validation result describing any invalid tokens.</returns>
        public AttachmentTokenValidationResult ValidateTokensReady(string[] tokens)
        {
            var result = new AttachmentTokenValidationResult();

            if (tokens == null || tokens.Length == 0)
            {
                return result;
            }

            foreach (var token in tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IsSafeToken(token))
                {
                    result.MissingTokens.Add(token);
                    continue;
                }

                var (_, dataPath, readyPath, manifestPath, uploadingManifestPath) = this.GetPaths(token);

                var dataExists = File.Exists(dataPath);
                var manifestExists = File.Exists(manifestPath);
                var readyExists = File.Exists(readyPath);
                var uploadingExists = File.Exists(uploadingManifestPath);

                if (!dataExists && !manifestExists && !readyExists && !uploadingExists)
                {
                    result.MissingTokens.Add(token);
                    continue;
                }

                if (!(dataExists && manifestExists && readyExists))
                {
                    result.NotReadyTokens.Add(token);
                }
            }

            return result;
        }

        /// <summary>
        /// Tries to load the raw manifest JSON for an attachment.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        /// <returns>
        /// The raw manifest JSON if present; otherwise <see langword="null"/>.
        /// </returns>
        public string? TryGetManifestJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || !IsSafeToken(token))
            {
                return null;
            }

            var (_, _, _, manifestPath, _) = this.GetPaths(token);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(manifestPath, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lists attachments by scanning manifest files and applying server-side filters.
        /// </summary>
        /// <param name="clientId">Optional client id to match (case-insensitive).</param>
        /// <param name="mergeOwnerId">Optional merge owner id to match (case-insensitive).</param>
        /// <param name="olderThanUtc">Optional limit to only include attachments uploaded before this time.</param>
        /// <param name="newerThanUtc">Optional limit to only include attachments uploaded after this time.</param>
        /// <param name="minRefCount">Optional minimum reference count (inclusive).</param>
        /// <param name="maxRefCount">Optional maximum reference count (inclusive).</param>
        /// <param name="minLength">Optional minimum size in bytes (inclusive).</param>
        /// <param name="maxLength">Optional maximum size in bytes (inclusive).</param>
        /// <param name="onlyOrphans">When true, only include attachments with a ref-count of zero.</param>
        /// <param name="onlyLarge">When true, only include attachments over <paramref name="largeThresholdBytes"/>.</param>
        /// <param name="largeThresholdBytes">Threshold used by <paramref name="onlyLarge"/> when enabled.</param>
        /// <param name="skip">Number of results to skip for paging.</param>
        /// <param name="take">Number of results to take for paging.</param>
        /// <returns>A tuple containing the total count and the selected page.</returns>
        public (int Total, AttachmentListItem[] Items) ListAttachments(
            string? clientId,
            string? mergeOwnerId,
            DateTimeOffset? olderThanUtc,
            DateTimeOffset? newerThanUtc,
            int? minRefCount,
            int? maxRefCount,
            long? minLength,
            long? maxLength,
            bool onlyOrphans,
            bool onlyLarge,
            long largeThresholdBytes,
            int skip,
            int take)
        {
            if (!Directory.Exists(this.options.BaseFolder))
            {
                return (0, Array.Empty<AttachmentListItem>());
            }

            if (take <= 0)
            {
                take = 50;
            }

            if (take > 500)
            {
                take = 500;
            }

            if (skip < 0)
            {
                skip = 0;
            }

            if (largeThresholdBytes <= 0)
            {
                largeThresholdBytes = 10L * 1024L * 1024L;
            }

            var clientIdFilter = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
            var mergeOwnerFilter = string.IsNullOrWhiteSpace(mergeOwnerId) ? null : mergeOwnerId;

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(this.options.BaseFolder, "*.manifest.json", SearchOption.AllDirectories);
            }
            catch
            {
                return (0, Array.Empty<AttachmentListItem>());
            }

            var results = new List<AttachmentListItem>();

            foreach (var manifestPath in manifests)
            {
                AttachmentStoreManifest? manifest;
                try
                {
                    var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                    manifest = JsonSerializer.Deserialize<AttachmentStoreManifest>(json);
                }
                catch
                {
                    continue;
                }

                if (manifest == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Token))
                {
                    continue;
                }

                if (clientIdFilter != null && !string.Equals(manifest.ClientId ?? string.Empty, clientIdFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mergeOwnerFilter != null && !string.Equals(manifest.MergeOwnerId ?? string.Empty, mergeOwnerFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (olderThanUtc.HasValue && manifest.UploadedUtc >= olderThanUtc.Value)
                {
                    continue;
                }

                if (newerThanUtc.HasValue && manifest.UploadedUtc <= newerThanUtc.Value)
                {
                    continue;
                }

                if (minRefCount.HasValue && manifest.RefCount < minRefCount.Value)
                {
                    continue;
                }

                if (maxRefCount.HasValue && manifest.RefCount > maxRefCount.Value)
                {
                    continue;
                }

                if (minLength.HasValue && manifest.Length < minLength.Value)
                {
                    continue;
                }

                if (maxLength.HasValue && manifest.Length > maxLength.Value)
                {
                    continue;
                }

                if (onlyOrphans && manifest.RefCount != 0)
                {
                    continue;
                }

                if (onlyLarge && manifest.Length <= largeThresholdBytes)
                {
                    continue;
                }

                var (_, dataPath, readyPath, _, _) = this.GetPaths(manifest.Token);

                var item = new AttachmentListItem
                {
                    Token = manifest.Token ?? string.Empty,
                    Exists = File.Exists(dataPath) || File.Exists(manifestPath),
                    Ready = File.Exists(dataPath) && File.Exists(readyPath),
                    RefCount = manifest.RefCount,
                    Length = manifest.Length,
                    Sha256Base64 = manifest.Sha256Base64 ?? string.Empty,
                    UploadedUtc = manifest.UploadedUtc,
                    FileName = manifest.FileName ?? string.Empty,
                    ContentType = manifest.ContentType ?? string.Empty,
                    ClientId = manifest.ClientId ?? string.Empty,
                    MergeOwnerId = manifest.MergeOwnerId ?? string.Empty,
                };

                results.Add(item);
            }

            var ordered = results
                .OrderByDescending(r => r.UploadedUtc)
                .ThenBy(r => r.Token, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var total = ordered.Length;

            var page = ordered
                .Skip(skip)
                .Take(take)
                .ToArray();

            return (total, page);
        }

        /// <summary>
        /// Deletes orphaned attachments (ref-count == 0) that belong to the supplied merge owner id.
        /// This is intended to be called when a merge template is deleted.
        /// </summary>
        /// <param name="mergeOwnerId">The merge owner id to match (case-insensitive).</param>
        /// <returns>The number of attachment tokens deleted.</returns>
        public int DeleteOrphanedByMergeOwnerId(string mergeOwnerId)
        {
            if (string.IsNullOrWhiteSpace(mergeOwnerId))
            {
                return 0;
            }

            if (!Directory.Exists(this.options.BaseFolder))
            {
                return 0;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(this.options.BaseFolder, "*.manifest.json", SearchOption.AllDirectories);
            }
            catch
            {
                return 0;
            }

            var deleted = 0;

            foreach (var manifestPath in manifests)
            {
                AttachmentStoreManifest? manifest;
                try
                {
                    var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                    manifest = JsonSerializer.Deserialize<AttachmentStoreManifest>(json);
                }
                catch
                {
                    continue;
                }

                if (manifest == null)
                {
                    continue;
                }

                if (manifest.RefCount != 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Token))
                {
                    continue;
                }

                if (!string.Equals(manifest.MergeOwnerId ?? string.Empty, mergeOwnerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                this.TryDeleteAttachment(manifest.Token);
                deleted++;
            }

            return deleted;
        }

        private (string Folder, string DataPath, string ReadyPath, string ManifestPath, string UploadingManifestPath) GetPaths(string token)
        {
            var safe = token;
            if (safe.Length < 4)
            {
                safe = safe.PadRight(4, '0');
            }

            var folder = Path.Combine(this.options.BaseFolder, safe.Substring(0, 2), safe.Substring(2, 2));
            var dataPath = Path.Combine(folder, safe + ".bin");
            var readyPath = Path.Combine(folder, safe + ".ready");
            var manifestPath = Path.Combine(folder, safe + ".manifest.json");
            var uploadingManifestPath = Path.Combine(folder, safe + ".manifest.json.uploading");
            return (folder, dataPath, readyPath, manifestPath, uploadingManifestPath);
        }

        private void TryUpdateRefCount(string token, int delta, string? mergeOwnerId)
        {
            if (!IsSafeToken(token))
            {
                return;
            }

            var (_, _, readyPath, manifestPath, _) = this.GetPaths(token);

            if (!File.Exists(readyPath) || !File.Exists(manifestPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<AttachmentStoreManifest>(json);
                if (manifest == null)
                {
                    return;
                }

                var next = manifest.RefCount + delta;
                if (next < 0)
                {
                    next = 0;
                }

                manifest.RefCount = next;

                if (!string.IsNullOrWhiteSpace(mergeOwnerId) && string.IsNullOrWhiteSpace(manifest.MergeOwnerId))
                {
                    manifest.MergeOwnerId = mergeOwnerId;
                }

                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest), Encoding.UTF8);

                try
                {
                    this.indexNotifier.OnRefCountChanged(manifest.Token ?? token, manifest.RefCount, manifest.MergeOwnerId ?? string.Empty);
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Keeping token validation near upload and manifest operations preserves attachment store readability.")]
        private static bool IsSafeToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            for (var i = 0; i < token.Length; i++)
            {
                var c = token[i];
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                {
                    return false;
                }
            }

            return token.Length >= 16;
        }

        private static bool FixedTimeEqualsBase64(string a, string b)
        {
            byte[] ba;
            byte[] bb;

            try
            {
                ba = Convert.FromBase64String(a);
                bb = Convert.FromBase64String(b);
            }
            catch
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(ba, bb);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
