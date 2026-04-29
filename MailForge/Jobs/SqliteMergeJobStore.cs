//-----------------------------------------------------------------------
// <copyright file="SqliteMergeJobStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides persistence for merge job state in a per-job SQLite database.
    /// </summary>
    public sealed class SqliteMergeJobStore
    {
        private readonly ILogger<SqliteMergeJobStore> logger;

        /// <summary>
        /// Initialises a new instance of the <see cref="SqliteMergeJobStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public SqliteMergeJobStore(ILogger<SqliteMergeJobStore> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Returns the job folder path for a given work root and job id.
        /// </summary>
        /// <param name="jobWorkRoot">Work root path.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <returns>Folder path for the job.</returns>
        public static string GetJobFolder(string jobWorkRoot, string jobId)
        {
            return Path.Combine(jobWorkRoot, jobId);
        }

        /// <summary>
        /// Returns the job database path for a given work root and job id.
        /// </summary>
        /// <param name="jobWorkRoot">Work root path.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <returns>Database path for the job.</returns>
        public static string GetJobDatabasePath(string jobWorkRoot, string jobId)
        {
            return Path.Combine(GetJobFolder(jobWorkRoot, jobId), "job.db");
        }

        /// <summary>
        /// Ensures that the per-job database exists and has a valid schema.
        /// </summary>
        /// <param name="jobWorkRoot">Work root path.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Database path.</returns>
        public async Task<string> EnsureJobDatabaseAsync(string jobWorkRoot, string jobId, CancellationToken cancellationToken)
        {
            var folder = GetJobFolder(jobWorkRoot, jobId);
            Directory.CreateDirectory(folder);

            var dbPath = GetJobDatabasePath(jobWorkRoot, jobId);

            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            return dbPath;
        }

        /// <summary>
        /// Creates or updates a job row.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="clientId">Client identifier.</param>
        /// <param name="engine">Template engine identifier.</param>
        /// <param name="inputJsonPath">Path to the job input payload.</param>
        /// <param name="jobWorkRoot">Job work root.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Updated snapshot.</returns>
        public async Task<MergeJobSnapshot> UpsertJobAsync(string dbPath, string jobId, string clientId, string engine, string inputJsonPath, string jobWorkRoot, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO jobs(job_id, client_id, engine, input_json_path, status, total, completed, failed, last_error, updated_utc, job_work_root, started_utc)
VALUES($job_id, $client_id, $engine, $input_json_path, $status, 0, 0, 0, '', $updated_utc, $job_work_root, COALESCE((SELECT started_utc FROM jobs WHERE job_id = $job_id), $updated_utc))
ON CONFLICT(job_id)
DO UPDATE SET
  client_id = excluded.client_id,
  engine = excluded.engine,
  input_json_path = excluded.input_json_path,
  updated_utc = excluded.updated_utc,
  job_work_root = excluded.job_work_root,
  started_utc = COALESCE(jobs.started_utc, excluded.started_utc);
";

            cmd.Parameters.AddWithValue("$job_id", jobId);
            cmd.Parameters.AddWithValue("$client_id", clientId ?? string.Empty);
            cmd.Parameters.AddWithValue("$engine", engine ?? string.Empty);
            cmd.Parameters.AddWithValue("$input_json_path", inputJsonPath ?? string.Empty);
            cmd.Parameters.AddWithValue("$status", MergeJobStatus.Pending.ToString());
            cmd.Parameters.AddWithValue("$updated_utc", now);
            cmd.Parameters.AddWithValue("$job_work_root", jobWorkRoot ?? string.Empty);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return await this.ReadJobAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Backwards-compatible overload that omits the job work root.
        /// </summary>
        public Task<MergeJobSnapshot> UpsertJobAsync(string dbPath, string jobId, string clientId, string engine, string inputJsonPath, CancellationToken cancellationToken)
        {
            return this.UpsertJobAsync(dbPath, jobId, clientId, engine, inputJsonPath, string.Empty, cancellationToken);
        }

        /// <summary>
        /// Attempts to read a job from the job database.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Snapshot or null.</returns>
        public async Task<MergeJobSnapshot?> TryReadJobAsync(string dbPath, string jobId, CancellationToken cancellationToken)
        {
            try
            {
                return await this.ReadJobAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Updates the job status and last error.
        /// </summary>
        public async Task<MergeJobSnapshot> UpdateJobStatusAsync(string dbPath, string jobId, MergeJobStatus status, string? lastError, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var clearFinishedUtc = status == MergeJobStatus.Pending || status == MergeJobStatus.Running;
            var finishedUtc = status == MergeJobStatus.Completed || status == MergeJobStatus.Failed || status == MergeJobStatus.Cancelled
                ? now
                : null;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE jobs
SET status = $status,
    last_error = $last_error,
    updated_utc = $updated_utc,
    finished_utc = CASE
        WHEN $clear_finished_utc = 1 THEN NULL
        ELSE COALESCE(finished_utc, $finished_utc)
    END
WHERE job_id = $job_id;
";

            cmd.Parameters.AddWithValue("$job_id", jobId);
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$last_error", lastError ?? string.Empty);
            cmd.Parameters.AddWithValue("$updated_utc", now);
            cmd.Parameters.AddWithValue("$finished_utc", (object?)finishedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clear_finished_utc", clearFinishedUtc ? 1 : 0);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return await this.ReadJobAsync(dbPath, jobId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates the job counters.
        /// </summary>
        public async Task UpdateJobCountersAsync(string dbPath, string jobId, long total, long completed, long failed, string? lastError, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE jobs
SET total = $total,
    completed = $completed,
    failed = $failed,
    last_error = COALESCE(NULLIF($last_error, ''), last_error),
    updated_utc = $updated_utc
WHERE job_id = $job_id;
";

            cmd.Parameters.AddWithValue("$job_id", jobId);
            cmd.Parameters.AddWithValue("$total", total);
            cmd.Parameters.AddWithValue("$completed", completed);
            cmd.Parameters.AddWithValue("$failed", failed);
            cmd.Parameters.AddWithValue("$last_error", lastError ?? string.Empty);
            cmd.Parameters.AddWithValue("$updated_utc", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Persists the protobuf-serialised mail template for a job.
        /// </summary>
        public async Task SetTemplateAsync(string dbPath, string jobId, byte[] templateBytes, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET template_proto = $template_proto WHERE job_id = $job_id;";
            cmd.Parameters.AddWithValue("$job_id", jobId);
            cmd.Parameters.AddWithValue("$template_proto", templateBytes ?? Array.Empty<byte>());

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Attempts to load the protobuf-serialised template from the job database.
        /// </summary>
        public async Task<byte[]?> TryGetTemplateAsync(string dbPath, string jobId, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT template_proto FROM jobs WHERE job_id = $job_id LIMIT 1;";
            cmd.Parameters.AddWithValue("$job_id", jobId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (reader.IsDBNull(0))
            {
                return null;
            }

            return (byte[])reader.GetValue(0);
        }

        /// <summary>
        /// Upserts a batch row for admin visibility.
        /// </summary>
        public async Task UpsertBatchAsync(string dbPath, string mergeId, int batchId, MergeJobStatus status, long total, long completed, long failed, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO batches(job_id, batch_id, status, total, completed, failed, updated_utc)
VALUES($job_id, $batch_id, $status, $total, $completed, $failed, $updated_utc)
ON CONFLICT(job_id, batch_id)
DO UPDATE SET
  status = excluded.status,
  total = excluded.total,
  completed = excluded.completed,
  failed = excluded.failed,
  updated_utc = excluded.updated_utc;
";

            cmd.Parameters.AddWithValue("$job_id", mergeId);
            cmd.Parameters.AddWithValue("$batch_id", batchId);
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$total", total);
            cmd.Parameters.AddWithValue("$completed", completed);
            cmd.Parameters.AddWithValue("$failed", failed);
            cmd.Parameters.AddWithValue("$updated_utc", now);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists persisted batch rows for a merge.
        /// </summary>
        public async Task<IReadOnlyList<MergeBatchSnapshot>> ListBatchesAsync(string dbPath, string mergeId, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            var results = new List<MergeBatchSnapshot>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT batch_id, status, total, completed, failed, updated_utc FROM batches WHERE job_id = $job_id ORDER BY batch_id ASC;";
            cmd.Parameters.AddWithValue("$job_id", mergeId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var statusRaw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                Enum.TryParse(statusRaw, ignoreCase: true, out MergeJobStatus status);

                var updatedUtc = DateTime.UtcNow;
                var updatedUtcRaw = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                if (!string.IsNullOrWhiteSpace(updatedUtcRaw) && DateTime.TryParse(updatedUtcRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                {
                    updatedUtc = dt;
                }

                results.Add(new MergeBatchSnapshot
                {
                    MergeId = mergeId,
                    BatchId = id,
                    Status = status,
                    TotalRows = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    CompletedRows = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    FailedRows = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    UpdatedUtc = updatedUtc,
                });
            }

            return results;
        }

        /// <summary>
        /// Resolves a job database path by checking the standard per-job folder locations.
        /// </summary>
        public static string? TryResolveJobDatabasePath(string jobId, IEnumerable<string> candidateRoots)
        {
            foreach (var root in candidateRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                try
                {
                    var db = GetJobDatabasePath(root, jobId);
                    if (File.Exists(db))
                    {
                        return db;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static SqliteConnection OpenConnection(string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            };

            return new SqliteConnection(cs.ToString());
        }

        private async Task InitialiseSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS jobs(
  job_id TEXT PRIMARY KEY,
  client_id TEXT,
  engine TEXT,
  input_json_path TEXT,
  status TEXT,
  total INTEGER,
  completed INTEGER,
  failed INTEGER,
  last_error TEXT,
  updated_utc TEXT,
  job_work_root TEXT,
  started_utc TEXT,
  finished_utc TEXT,
  template_proto BLOB
);

CREATE TABLE IF NOT EXISTS batches(
  job_id TEXT NOT NULL,
  batch_id INTEGER NOT NULL,
  status TEXT,
  total INTEGER,
  completed INTEGER,
  failed INTEGER,
  updated_utc TEXT,
  PRIMARY KEY(job_id, batch_id)
);
";

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureSchemaAdditionsAsync(SqliteConnection conn, CancellationToken cancellationToken)
        {
            await this.TryEnsureColumnAsync(conn, "jobs", "job_work_root", "TEXT", cancellationToken).ConfigureAwait(false);
            await this.TryEnsureColumnAsync(conn, "jobs", "started_utc", "TEXT", cancellationToken).ConfigureAwait(false);
            await this.TryEnsureColumnAsync(conn, "jobs", "finished_utc", "TEXT", cancellationToken).ConfigureAwait(false);
            await this.TryEnsureColumnAsync(conn, "jobs", "template_proto", "BLOB", cancellationToken).ConfigureAwait(false);
        }

        private async Task TryEnsureColumnAsync(SqliteConnection conn, string table, string column, string type, CancellationToken cancellationToken)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<MergeJobSnapshot> ReadJobAsync(string dbPath, string jobId, CancellationToken cancellationToken)
        {
            using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await this.InitialiseSchemaAsync(conn, cancellationToken).ConfigureAwait(false);
            await this.EnsureSchemaAdditionsAsync(conn, cancellationToken).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT job_id, client_id, engine, status, total, completed, failed, last_error, updated_utc, job_work_root, started_utc, finished_utc
FROM jobs
WHERE job_id = $job_id
LIMIT 1;
";
            cmd.Parameters.AddWithValue("$job_id", jobId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Job not found");
            }

            var statusRaw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            Enum.TryParse(statusRaw, ignoreCase: true, out MergeJobStatus status);

            var updatedUtc = DateTime.UtcNow;
            var updatedUtcRaw = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            if (!string.IsNullOrWhiteSpace(updatedUtcRaw) && DateTime.TryParse(updatedUtcRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updatedDt))
            {
                updatedUtc = updatedDt;
            }

            var startedUtc = (DateTime?)null;
            var startedUtcRaw = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
            if (!string.IsNullOrWhiteSpace(startedUtcRaw) && DateTime.TryParse(startedUtcRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedDt))
            {
                startedUtc = startedDt;
            }

            var finishedUtc = (DateTime?)null;
            var finishedUtcRaw = reader.IsDBNull(11) ? string.Empty : reader.GetString(11);
            if (!string.IsNullOrWhiteSpace(finishedUtcRaw) && DateTime.TryParse(finishedUtcRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var finishedDt))
            {
                finishedUtc = finishedDt;
            }

            return new MergeJobSnapshot
            {
                JobId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                ClientId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Engine = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Status = status,
                Total = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                Completed = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                Failed = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                LastError = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                UpdatedUtc = updatedUtc,
                JobWorkRoot = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                StartedUtc = startedUtc,
                FinishedUtc = finishedUtc,
            };
        }
    }
}
