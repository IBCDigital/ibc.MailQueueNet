//-----------------------------------------------------------------------
// <copyright file="AttachmentIndexNotifier.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Threading;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Bridges attachment store events into the SQLite attachment index.
    /// </summary>
    internal sealed class AttachmentIndexNotifier : IAttachmentIndexNotifier
    {
        private readonly IConfiguration configuration;
        private readonly ILogger<AttachmentIndexNotifier> logger;
        private readonly SqliteAttachmentIndexStore store;

        /// <summary>
        /// Synchronisation gate for resolving and caching the attachment index database path.
        /// </summary>
        private readonly object dbPathGate = new object();

        /// <summary>
        /// The resolved, writable attachment index database path.
        /// </summary>
        private string? resolvedDbPath;

        /// <summary>
        /// Value indicating whether a fallback-path warning has been logged.
        /// </summary>
        private bool dbPathFallbackLogged;

        /// <summary>
        /// Initialises a new instance of the <see cref="AttachmentIndexNotifier"/> class.
        /// </summary>
        /// <param name="configuration">Application configuration.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="store">The SQLite attachment index store.</param>
        public AttachmentIndexNotifier(IConfiguration configuration, ILogger<AttachmentIndexNotifier> logger, SqliteAttachmentIndexStore store)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <inheritdoc />
        public void OnRefCountChanged(string token, int refCount, string mergeOwnerId)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                this.store.UpdateRefCountAndMergeOwnerIdAsync(this.ResolveAttachmentIndexDbPath(), token, refCount, mergeOwnerId ?? string.Empty, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed updating attachment index refcount (Token={Token})", token);
            }
        }

        /// <inheritdoc />
        public void OnDeleted(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            try
            {
                this.store.DeleteAsync(this.ResolveAttachmentIndexDbPath(), token, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed deleting attachment token from index (Token={Token})", token);
            }
        }

        /// <inheritdoc />
        public void OnUpserted(string token)
        {
            // Current implementation does not attempt to re-read manifests on demand.
            // Upload completion paths already upsert full rows.
            _ = token;
        }

        /// <summary>
        /// Resolves the attachment index database path, falling back to a writable temp folder when the
        /// configured/preferred path is not writable.
        /// </summary>
        /// <returns>The resolved database path.</returns>
        private string ResolveAttachmentIndexDbPath()
        {
            lock (this.dbPathGate)
            {
                if (!string.IsNullOrWhiteSpace(this.resolvedDbPath))
                {
                    return this.resolvedDbPath;
                }

                var configured = this.configuration["Attachments:IndexDbPath"];
                var root = AppContext.BaseDirectory;

                var preferred = string.IsNullOrWhiteSpace(configured)
                    ? System.IO.Path.Combine(root, "attachment_index.db")
                    : (System.IO.Path.IsPathRooted(configured) ? configured : System.IO.Path.Combine(root, configured));

                this.resolvedDbPath = this.TryGetWritableAttachmentIndexDbPath(preferred);
                return this.resolvedDbPath;
            }
        }

        /// <summary>
        /// Attempts to use the preferred attachment index database path and falls back to a temp folder
        /// when the path is not writable.
        /// </summary>
        /// <param name="preferred">The preferred path.</param>
        /// <returns>A writable path for the attachment index database.</returns>
        private string TryGetWritableAttachmentIndexDbPath(string preferred)
        {
            if (string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            try
            {
                var folder = System.IO.Path.GetDirectoryName(preferred);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    System.IO.Directory.CreateDirectory(folder);
                }

                if (System.IO.File.Exists(preferred))
                {
                    using var fs = new System.IO.FileStream(preferred, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                    return preferred;
                }

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var probe = System.IO.Path.Combine(folder, ".write_probe_" + Guid.NewGuid().ToString("N"));
                    System.IO.File.WriteAllText(probe, "probe");
                    System.IO.File.Delete(probe);
                }

                return preferred;
            }
            catch (Exception ex)
            {
                var fallbackFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MailQueueNet", "attachments");
                var fallback = System.IO.Path.Combine(fallbackFolder, "attachment_index.db");

                try
                {
                    System.IO.Directory.CreateDirectory(fallbackFolder);
                }
                catch
                {
                    return preferred;
                }

                if (!this.dbPathFallbackLogged)
                {
                    this.dbPathFallbackLogged = true;
                    this.logger.LogWarning(
                        ex,
                        "Attachment index database path is not writable. Falling back to '{FallbackPath}'. PreferredPath='{PreferredPath}'.",
                        fallback,
                        preferred);
                }

                return fallback;
            }
        }
    }
}
