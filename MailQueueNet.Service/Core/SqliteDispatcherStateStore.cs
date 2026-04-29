//-----------------------------------------------------------------------
// <copyright file="SqliteDispatcherStateStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
//
// Derived from “MailQueueNet” by Daniel Cohen Gindi
// (https://github.com/danielgindi/MailQueueNet).
//
// Original portions:
//   © 2014 Daniel Cohen Gindi (danielgindi@gmail.com)
//   Licensed under the MIT Licence.
// Modifications and additions:
//   © 2025 IBC Digital Pty Ltd
//   Distributed under the same MIT Licence.
//
// The above notice and this permission notice shall be included in
// all copies or substantial portions of this file.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides SQLite persistence for MailForge dispatcher lease details and merge-job
    /// dispatch state. This enables idempotent dispatch and lease fencing continuity
    /// across service restarts.
    /// </summary>
    public sealed class SqliteDispatcherStateStore
    {
        private const int SchemaVersion = 1;

        private readonly ILogger<SqliteDispatcherStateStore> logger;

        /// <summary>
        /// Initialises a new instance of the <see cref="SqliteDispatcherStateStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance for diagnostics.</param>
        public SqliteDispatcherStateStore(ILogger<SqliteDispatcherStateStore> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Ensures the SQLite schema exists for the specified database path.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task EnsureSchemaAsync(string dbPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                throw new ArgumentException("Database path is required.", nameof(dbPath));
            }

            var folder = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Attempts to load the last persisted lease snapshot.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The lease snapshot, or <see langword="null"/> if none is stored.</returns>
        public async Task<DispatcherLease?> TryLoadLeaseAsync(string dbPath, CancellationToken cancellationToken)
        {
            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT worker_address, fence_token, expires_utc
FROM leases
WHERE lease_id = 1;";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var worker = reader.GetString(0);
            var fence = reader.GetInt64(1);
            var expiresRaw = reader.GetString(2);
            var expires = DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            return new DispatcherLease
            {
                WorkerAddress = worker,
                FenceToken = fence,
                ExpiresUtc = expires,
            };
        }

        /// <summary>
        /// Persists the current lease snapshot.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="lease">Lease to persist.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task SaveLeaseAsync(string dbPath, DispatcherLease lease, CancellationToken cancellationToken)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO leases (lease_id, worker_address, fence_token, expires_utc, updated_utc)
VALUES (1, $worker_address, $fence_token, $expires_utc, $updated_utc)
ON CONFLICT(lease_id) DO UPDATE SET
  worker_address = excluded.worker_address,
  fence_token = excluded.fence_token,
  expires_utc = excluded.expires_utc,
  updated_utc = excluded.updated_utc;";

            cmd.Parameters.AddWithValue("$worker_address", lease.WorkerAddress ?? string.Empty);
            cmd.Parameters.AddWithValue("$fence_token", lease.FenceToken);
            cmd.Parameters.AddWithValue("$expires_utc", lease.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the maximum known fence token for coordination continuity.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Maximum fence token observed, or 0 if none recorded.</returns>
        public async Task<long> GetMaxFenceTokenAsync(string dbPath, CancellationToken cancellationToken)
        {
            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(fence_token), 0) FROM leases;";

            var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(obj ?? 0, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Checks whether a merge job has already been dispatched.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> when the merge id is already marked as dispatched.</returns>
        public async Task<bool> IsMergeJobDispatchedAsync(string dbPath, string mergeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return false;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM merge_jobs WHERE merge_id = $merge_id AND status = $status LIMIT 1;";
            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());
            cmd.Parameters.AddWithValue("$status", (int)MergeJobDispatchStatus.Dispatched);

            var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return obj != null;
        }

        /// <summary>
        /// Marks a merge job as dispatched.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="templatePath">Template file path.</param>
        /// <param name="workerAddress">Worker address used for dispatch.</param>
        /// <param name="fenceToken">Fence token used for dispatch.</param>
        /// <param name="message">Optional worker message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task MarkMergeJobDispatchedAsync(string dbPath, string mergeId, string templatePath, string workerAddress, long fenceToken, string? message, CancellationToken cancellationToken)
        {
            await this.UpsertMergeJobAsync(dbPath, mergeId, templatePath, MergeJobDispatchStatus.Dispatched, workerAddress, fenceToken, message, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks a merge job dispatch attempt as failed.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="templatePath">Template file path.</param>
        /// <param name="message">Failure detail.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task MarkMergeJobDispatchFailedAsync(string dbPath, string mergeId, string templatePath, string? message, CancellationToken cancellationToken)
        {
            await this.UpsertMergeJobAsync(dbPath, mergeId, templatePath, MergeJobDispatchStatus.Failed, string.Empty, 0, message, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the current dispatch status and timestamps for a merge id.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The merge job state, or <see langword="null"/> when not found.</returns>
        public async Task<MergeJobState?> TryGetMergeJobStateAsync(string dbPath, string mergeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return null;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT merge_id, template_path, status, worker_address, fence_token, message, updated_utc, completed_utc
FROM merge_jobs
WHERE merge_id = $merge_id
LIMIT 1;";
            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var templatePath = reader.GetString(1);
            var status = (MergeJobDispatchStatus)reader.GetInt32(2);
            var worker = reader.GetString(3);
            var fence = reader.GetInt64(4);
            var message = reader.GetString(5);
            var updatedUtc = ParseUtc(reader.GetString(6));

            DateTimeOffset? completedUtc = null;
            if (!reader.IsDBNull(7))
            {
                var raw = reader.GetString(7);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    completedUtc = ParseUtc(raw);
                }
            }

            return new MergeJobState(
                mergeId.Trim(),
                templatePath,
                status,
                worker,
                fence,
                message,
                updatedUtc,
                completedUtc);
        }

        /// <summary>
        /// Marks a merge job as completed.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task MarkMergeJobCompletedAsync(string dbPath, string mergeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                throw new ArgumentException("Merge id is required.", nameof(mergeId));
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE merge_jobs
SET status = $status,
    completed_utc = $completed_utc,
    updated_utc = $updated_utc
WHERE merge_id = $merge_id;";

            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());
            cmd.Parameters.AddWithValue("$status", (int)MergeJobDispatchStatus.Completed);
            cmd.Parameters.AddWithValue("$completed_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Attempts to reopen a completed merge job.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="reopenWindow">Amount of time after completion in which the job may be reopened.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> if the merge was reopened; otherwise <see langword="false"/>.</returns>
        public async Task<bool> TryReopenMergeJobAsync(string dbPath, string mergeId, TimeSpan reopenWindow, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return false;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var cutoff = DateTimeOffset.UtcNow.Subtract(reopenWindow).ToString("o", CultureInfo.InvariantCulture);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE merge_jobs
SET status = $status,
    completed_utc = NULL,
    updated_utc = $updated_utc
WHERE merge_id = $merge_id
  AND status = $completed_status
  AND completed_utc IS NOT NULL
  AND completed_utc >= $cutoff;";

            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());
            cmd.Parameters.AddWithValue("$status", (int)MergeJobDispatchStatus.Dispatched);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$completed_status", (int)MergeJobDispatchStatus.Completed);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return rows > 0;
        }

        /// <summary>
        /// Attempts to delete a completed merge job record.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="deleteAfter">Minimum age of completion required before deletion.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see langword="true"/> when the record was removed; otherwise <see langword="false"/>.</returns>
        public async Task<bool> TryDeleteCompletedMergeJobAsync(string dbPath, string mergeId, TimeSpan deleteAfter, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                return false;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var cutoff = DateTimeOffset.UtcNow.Subtract(deleteAfter).ToString("o", CultureInfo.InvariantCulture);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM merge_jobs
WHERE merge_id = $merge_id
  AND status = $status
  AND completed_utc IS NOT NULL
  AND completed_utc <= $cutoff;";

            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());
            cmd.Parameters.AddWithValue("$status", (int)MergeJobDispatchStatus.Completed);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return rows > 0;
        }

        /// <summary>
        /// Lists merge dispatch state rows from the dispatcher state database.
        /// </summary>
        /// <param name="dbPath">SQLite database file path.</param>
        /// <param name="take">Maximum number of rows to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of merge dispatch state rows ordered by most recent update.</returns>
        public async Task<MergeDispatchStateRow[]> ListMergeDispatchStateRowsAsync(string dbPath, int take, CancellationToken cancellationToken)
        {
            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            if (take <= 0)
            {
                take = 50;
            }

            if (take > 500)
            {
                take = 500;
            }

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT merge_id,
       template_path,
       status,
       worker_address,
       fence_token,
       message,
       updated_utc,
       completed_utc
FROM merge_jobs
ORDER BY COALESCE(completed_utc, updated_utc) DESC
LIMIT $take;";
            cmd.Parameters.AddWithValue("$take", take);

            var rows = new System.Collections.Generic.List<MergeDispatchStateRow>();

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mergeId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var templatePath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var statusInt = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var worker = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var fence = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                var message = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                var updatedUtc = reader.IsDBNull(6) ? DateTimeOffset.MinValue : ParseUtc(reader.GetString(6));

                DateTimeOffset? completedUtc = null;
                if (!reader.IsDBNull(7))
                {
                    var raw = reader.GetString(7);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        completedUtc = ParseUtc(raw);
                    }
                }

                var status = ((MergeJobDispatchStatus)statusInt).ToString();

                rows.Add(new MergeDispatchStateRow
                {
                    MergeId = mergeId,
                    TemplatePath = templatePath,
                    WorkerAddress = worker,
                    FenceToken = fence,
                    Status = status,
                    LastError = message,
                    UpdatedUtc = updatedUtc,
                    CompletedUtc = completedUtc,
                });
            }

            return rows.ToArray();
        }

        private static DateTimeOffset ParseUtc(string value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }

            return DateTimeOffset.MinValue;
        }

        private static SqliteConnection OpenConnection(string dbPath)
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            };

            var conn = new SqliteConnection(csb.ToString());
            conn.DefaultTimeout = 30;
            return conn;
        }

        private async Task InitialiseSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_info (
  version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS leases (
  lease_id INTEGER NOT NULL PRIMARY KEY,
  worker_address TEXT NOT NULL,
  fence_token INTEGER NOT NULL,
  expires_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS merge_jobs (
  merge_id TEXT NOT NULL PRIMARY KEY,
  template_path TEXT NOT NULL,
  status INTEGER NOT NULL,
  worker_address TEXT NOT NULL,
  fence_token INTEGER NOT NULL,
  message TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  completed_utc TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_merge_jobs_status ON merge_jobs(status);
";

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(1) FROM schema_info;";
                var countObj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var count = Convert.ToInt32(countObj ?? 0, CultureInfo.InvariantCulture);
                if (count <= 0)
                {
                    await using var insert = conn.CreateCommand();
                    insert.CommandText = "INSERT INTO schema_info (version) VALUES ($v);";
                    insert.Parameters.AddWithValue("$v", SchemaVersion);
                    _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    this.logger.LogInformation("Dispatcher state schema initialised (Version={Version})", SchemaVersion);
                }
            }
        }

        private async Task UpsertMergeJobAsync(string dbPath, string mergeId, string templatePath, MergeJobDispatchStatus status, string workerAddress, long fenceToken, string? message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mergeId))
            {
                throw new ArgumentException("Merge id is required.", nameof(mergeId));
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO merge_jobs (merge_id, template_path, status, worker_address, fence_token, message, updated_utc, completed_utc)
VALUES ($merge_id, $template_path, $status, $worker_address, $fence_token, $message, $updated_utc, NULL)
ON CONFLICT(merge_id) DO UPDATE SET
  template_path = excluded.template_path,
  status = excluded.status,
  worker_address = excluded.worker_address,
  fence_token = excluded.fence_token,
  message = excluded.message,
  updated_utc = excluded.updated_utc,
  completed_utc = CASE WHEN excluded.status = $completed_status THEN COALESCE(merge_jobs.completed_utc, excluded.updated_utc) ELSE NULL END;";

            cmd.Parameters.AddWithValue("$merge_id", mergeId.Trim());
            cmd.Parameters.AddWithValue("$template_path", templatePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$worker_address", workerAddress ?? string.Empty);
            cmd.Parameters.AddWithValue("$fence_token", fenceToken);
            cmd.Parameters.AddWithValue("$message", message ?? string.Empty);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$completed_status", (int)MergeJobDispatchStatus.Completed);

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Describes the dispatch state for a merge identifier.
        /// </summary>
        public enum MergeJobDispatchStatus
        {
            /// <summary>
            /// A merge job has been discovered but not yet dispatched.
            /// </summary>
            Pending = 0,

            /// <summary>
            /// The merge job has been accepted by a worker.
            /// </summary>
            Dispatched = 1,

            /// <summary>
            /// The last dispatch attempt failed.
            /// </summary>
            Failed = 2,

            /// <summary>
            /// The merge job was completed and is in the closure window.
            /// </summary>
            Completed = 3,
        }

        /// <summary>
        /// Represents a row in the merge dispatch state list.
        /// </summary>
        public sealed class MergeDispatchStateRow
        {
            /// <summary>
            /// Gets or sets the merge identifier.
            /// </summary>
            public string MergeId { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the template path.
            /// </summary>
            public string TemplatePath { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the worker address.
            /// </summary>
            public string WorkerAddress { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the fence token.
            /// </summary>
            public long FenceToken { get; set; }

            /// <summary>
            /// Gets or sets the dispatch status.
            /// </summary>
            public string Status { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the last error message, if any.
            /// </summary>
            public string LastError { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the UTC timestamp of the last update.
            /// </summary>
            public DateTimeOffset UpdatedUtc { get; set; }

            /// <summary>
            /// Gets or sets the UTC timestamp of completion, if applicable.
            /// </summary>
            public DateTimeOffset? CompletedUtc { get; set; }
        }
    }
}
