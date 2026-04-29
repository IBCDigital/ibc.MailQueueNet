//-----------------------------------------------------------------------
// <copyright file="SqliteAttachmentIndexStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides a SQLite-backed index of attachments for efficient listing and search.
    /// </summary>
    internal sealed class SqliteAttachmentIndexStore
    {
        /// <summary>
        /// Current schema version.
        /// </summary>
        private const int SchemaVersion = 1;

        private readonly ILogger<SqliteAttachmentIndexStore> logger;

        /// <summary>
        /// Initialises a new instance of the <see cref="SqliteAttachmentIndexStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance for diagnostics.</param>
        public SqliteAttachmentIndexStore(ILogger<SqliteAttachmentIndexStore> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Ensures the attachment index schema exists.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
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
        /// Inserts or updates an attachment row.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="manifest">Manifest data used to populate the index.</param>
        /// <param name="ready">Value indicating whether the attachment is ready.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UpsertFromManifestAsync(string dbPath, AttachmentStoreManifest manifest, bool ready, CancellationToken cancellationToken)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (string.IsNullOrWhiteSpace(manifest.Token))
            {
                return;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO attachments (
  token,
  uploaded_utc,
  length,
  ref_count,
  client_id,
  merge_owner_id,
  file_name,
  content_type,
  sha256_base64,
  ready,
  updated_utc)
VALUES (
  $token,
  $uploaded_utc,
  $length,
  $ref_count,
  $client_id,
  $merge_owner_id,
  $file_name,
  $content_type,
  $sha256_base64,
  $ready,
  $updated_utc)
ON CONFLICT(token) DO UPDATE SET
  uploaded_utc = excluded.uploaded_utc,
  length = excluded.length,
  ref_count = excluded.ref_count,
  client_id = excluded.client_id,
  merge_owner_id = excluded.merge_owner_id,
  file_name = excluded.file_name,
  content_type = excluded.content_type,
  sha256_base64 = excluded.sha256_base64,
  ready = excluded.ready,
  updated_utc = excluded.updated_utc;";

            cmd.Parameters.AddWithValue("$token", manifest.Token);
            cmd.Parameters.AddWithValue("$uploaded_utc", manifest.UploadedUtc.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$length", manifest.Length);
            cmd.Parameters.AddWithValue("$ref_count", manifest.RefCount);
            cmd.Parameters.AddWithValue("$client_id", manifest.ClientId ?? string.Empty);
            cmd.Parameters.AddWithValue("$merge_owner_id", manifest.MergeOwnerId ?? string.Empty);
            cmd.Parameters.AddWithValue("$file_name", manifest.FileName ?? string.Empty);
            cmd.Parameters.AddWithValue("$content_type", manifest.ContentType ?? string.Empty);
            cmd.Parameters.AddWithValue("$sha256_base64", manifest.Sha256Base64 ?? string.Empty);
            cmd.Parameters.AddWithValue("$ready", ready ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates the reference count for a token.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="token">Attachment token.</param>
        /// <param name="refCount">New reference count.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UpdateRefCountAsync(string dbPath, string token, int refCount, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE attachments
SET ref_count = $ref_count,
    updated_utc = $updated_utc
WHERE token = $token;";
            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$ref_count", refCount);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a token from the index.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="token">Attachment token.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task DeleteAsync(string dbPath, string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM attachments WHERE token = $token;";
            cmd.Parameters.AddWithValue("$token", token);

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists attachments using indexed filters, optional sorting, and cursor-based paging.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="clientId">Optional client id filter.</param>
        /// <param name="mergeOwnerId">Optional merge owner id filter.</param>
        /// <param name="olderThanUtc">Optional upper bound on uploaded time.</param>
        /// <param name="newerThanUtc">Optional lower bound on uploaded time.</param>
        /// <param name="minRefCount">Optional minimum reference count.</param>
        /// <param name="maxRefCount">Optional maximum reference count.</param>
        /// <param name="minLength">Optional minimum length in bytes.</param>
        /// <param name="maxLength">Optional maximum length in bytes.</param>
        /// <param name="onlyOrphans">When true, only include ref_count = 0.</param>
        /// <param name="onlyLarge">When true, only include attachments larger than the threshold.</param>
        /// <param name="largeThresholdBytes">Threshold for large attachments.</param>
        /// <param name="sortBy">Sort selector.</param>
        /// <param name="sortDesc">Sort direction.</param>
        /// <param name="pageToken">Optional cursor token for stable paging.</param>
        /// <param name="skip">Skip for legacy paging (ignored when <paramref name="pageToken"/> supplied).</param>
        /// <param name="take">Page size.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A total count, items page, and a next-page token when more results are available.</returns>
        public async Task<(int Total, AttachmentIndexRow[] Items, string NextPageToken)> ListAsync(
            string dbPath,
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
            MailQueueNet.Grpc.AttachmentSortBy sortBy,
            bool sortDesc,
            string? pageToken,
            int skip,
            int take,
            CancellationToken cancellationToken)
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

            if (skip < 0)
            {
                skip = 0;
            }

            if (largeThresholdBytes <= 0)
            {
                largeThresholdBytes = 10L * 1024L * 1024L;
            }

            var where = new StringBuilder("WHERE 1=1");
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                where.Append(" AND client_id = $client_id");
                parameters.Add(new SqliteParameter("$client_id", clientId));
            }

            if (!string.IsNullOrWhiteSpace(mergeOwnerId))
            {
                where.Append(" AND merge_owner_id = $merge_owner_id");
                parameters.Add(new SqliteParameter("$merge_owner_id", mergeOwnerId));
            }

            if (olderThanUtc.HasValue)
            {
                where.Append(" AND uploaded_utc < $older_than");
                parameters.Add(new SqliteParameter("$older_than", olderThanUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
            }

            if (newerThanUtc.HasValue)
            {
                where.Append(" AND uploaded_utc > $newer_than");
                parameters.Add(new SqliteParameter("$newer_than", newerThanUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
            }

            if (minRefCount.HasValue)
            {
                where.Append(" AND ref_count >= $min_ref");
                parameters.Add(new SqliteParameter("$min_ref", minRefCount.Value));
            }

            if (maxRefCount.HasValue)
            {
                where.Append(" AND ref_count <= $max_ref");
                parameters.Add(new SqliteParameter("$max_ref", maxRefCount.Value));
            }

            if (minLength.HasValue)
            {
                where.Append(" AND length >= $min_len");
                parameters.Add(new SqliteParameter("$min_len", minLength.Value));
            }

            if (maxLength.HasValue)
            {
                where.Append(" AND length <= $max_len");
                parameters.Add(new SqliteParameter("$max_len", maxLength.Value));
            }

            if (onlyOrphans)
            {
                where.Append(" AND ref_count = 0");
            }

            if (onlyLarge)
            {
                where.Append(" AND length > $large_threshold");
                parameters.Add(new SqliteParameter("$large_threshold", largeThresholdBytes));
            }

            var orderBy = this.BuildOrderByClause(sortBy, sortDesc);

            var cursor = AttachmentQueryCursor.TryParse(pageToken);
            if (cursor != null)
            {
                this.AppendCursorWhereClause(where, parameters, sortBy, sortDesc, cursor);
            }

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var countSql = "SELECT COUNT(1) FROM attachments " + where + ";";
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = countSql;
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(p);
            }

            var countObj = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var total = Convert.ToInt32(countObj ?? 0, CultureInfo.InvariantCulture);

            var dataSql = new StringBuilder();
            dataSql.Append(@"SELECT token, uploaded_utc, length, ref_count, client_id, merge_owner_id, file_name, content_type, sha256_base64, ready
FROM attachments ");
            dataSql.Append(where);
            dataSql.Append('\n');
            dataSql.Append(orderBy);
            dataSql.Append("\nLIMIT $take");

            // Only apply OFFSET for legacy skip/take when not using page tokens.
            if (cursor == null)
            {
                dataSql.Append(" OFFSET $skip");
            }

            dataSql.Append(';');

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = dataSql.ToString();
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(new SqliteParameter(p.ParameterName!, p.Value));
            }

            cmd.Parameters.AddWithValue("$take", take);
            if (cursor == null)
            {
                cmd.Parameters.AddWithValue("$skip", skip);
            }

            var items = new List<AttachmentIndexRow>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = new AttachmentIndexRow
                {
                    Token = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    UploadedUtc = ParseUtc(reader.IsDBNull(1) ? string.Empty : reader.GetString(1)),
                    Length = reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                    RefCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    ClientId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    MergeOwnerId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    FileName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    ContentType = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    Sha256Base64 = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    Ready = !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
                };

                items.Add(row);
            }

            var nextPageTokenOut = string.Empty;
            if (items.Count == take)
            {
                var last = items[^1];
                nextPageTokenOut = this.BuildNextPageToken(sortBy, last);
            }

            return (total, items.ToArray(), nextPageTokenOut);
        }

        /// <summary>
        /// Computes aggregate attachment statistics using the index.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="largeThresholdBytes">Threshold used to classify large attachments.</param>
        /// <param name="clientId">Optional client id filter.</param>
        /// <param name="mergeOwnerId">Optional merge owner id filter.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Tuple containing counts and size totals for total/orphan/large groupings.</returns>
        public async Task<(long TotalCount, long TotalBytes, long OrphanCount, long OrphanBytes, long LargeCount, long LargeBytes)> GetStatsAsync(
            string dbPath,
            long largeThresholdBytes,
            string? clientId,
            string? mergeOwnerId,
            CancellationToken cancellationToken)
        {
            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            if (largeThresholdBytes <= 0)
            {
                largeThresholdBytes = 10L * 1024L * 1024L;
            }

            var where = new StringBuilder("WHERE 1=1");
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                where.Append(" AND client_id = $client_id");
                parameters.Add(new SqliteParameter("$client_id", clientId));
            }

            if (!string.IsNullOrWhiteSpace(mergeOwnerId))
            {
                where.Append(" AND merge_owner_id = $merge_owner_id");
                parameters.Add(new SqliteParameter("$merge_owner_id", mergeOwnerId));
            }

            var sql = new StringBuilder();
            sql.Append("SELECT ");
            sql.Append("COUNT(1) AS total_count, ");
            sql.Append("COALESCE(SUM(length), 0) AS total_bytes, ");
            sql.Append("COALESCE(SUM(CASE WHEN ref_count = 0 THEN 1 ELSE 0 END), 0) AS orphan_count, ");
            sql.Append("COALESCE(SUM(CASE WHEN ref_count = 0 THEN length ELSE 0 END), 0) AS orphan_bytes, ");
            sql.Append("COALESCE(SUM(CASE WHEN length > $large_threshold THEN 1 ELSE 0 END), 0) AS large_count, ");
            sql.Append("COALESCE(SUM(CASE WHEN length > $large_threshold THEN length ELSE 0 END), 0) AS large_bytes ");
            sql.Append("FROM attachments ");
            sql.Append(where);
            sql.Append(';');

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }

            cmd.Parameters.AddWithValue("$large_threshold", largeThresholdBytes);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (0, 0, 0, 0, 0, 0);
            }

            long ReadInt64(int ordinal)
            {
                if (reader.IsDBNull(ordinal))
                {
                    return 0;
                }

                try
                {
                    return reader.GetInt64(ordinal);
                }
                catch
                {
                    return Convert.ToInt64(reader.GetValue(ordinal) ?? 0, CultureInfo.InvariantCulture);
                }
            }

            var totalCount = ReadInt64(0);
            var totalBytes = ReadInt64(1);
            var orphanCount = ReadInt64(2);
            var orphanBytes = ReadInt64(3);
            var largeCount = ReadInt64(4);
            var largeBytes = ReadInt64(5);

            return (totalCount, totalBytes, orphanCount, orphanBytes, largeCount, largeBytes);
        }

        private string BuildNextPageToken(MailQueueNet.Grpc.AttachmentSortBy sortBy, AttachmentIndexRow last)
        {
            // Cursor encoding must match AppendCursorWhereClause.
            // We encode a (primary sort key, uploaded_utc, token) triple.
            var key = this.GetSortKeyString(sortBy, last);
            var raw = key + "|" + last.UploadedUtc.ToString("o", CultureInfo.InvariantCulture) + "|" + (last.Token ?? string.Empty);
            var bytes = Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes);
        }

        private string BuildOrderByClause(MailQueueNet.Grpc.AttachmentSortBy sortBy, bool sortDesc)
        {
            var dir = sortDesc ? "DESC" : "ASC";

            // Workspace uses proto3 enums; generated member names can vary with generator settings.
            // Use numeric values to avoid tight coupling to enum identifier casing.
            var sortByValue = (int)sortBy;

            // 2 = ATTACHMENT_SORT_BY_LENGTH
            if (sortByValue == 2)
            {
                return "ORDER BY length " + dir + ", uploaded_utc " + dir + ", token " + dir;
            }

            // 3 = ATTACHMENT_SORT_BY_REF_COUNT
            if (sortByValue == 3)
            {
                return "ORDER BY ref_count " + dir + ", uploaded_utc " + dir + ", token " + dir;
            }

            // Default: uploaded utc.
            return "ORDER BY uploaded_utc " + dir + ", token " + dir;
        }

        private void AppendCursorWhereClause(
            StringBuilder where,
            List<SqliteParameter> parameters,
            MailQueueNet.Grpc.AttachmentSortBy sortBy,
            bool sortDesc,
            AttachmentQueryCursor cursor)
        {
            // Cursor is a base64-encoded string: "{key}|{uploaded_utc}|{token}".
            // "key" meaning depends on sortBy.
            // The predicate is built to match the ORDER BY (primary, uploaded_utc, token).
            // We use strict inequality to avoid repeating the last row.
            if (cursor == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(cursor.Token))
            {
                return;
            }

            // The existing AttachmentQueryCursor only supports uploaded_utc+token.
            // For sort/cursor support we parse the extended token here.
            var extended = AttachmentQueryCursor.TryParseExtended(cursor.Token);
            if (extended == null)
            {
                // Fallback to uploaded_utc+token only.
                this.AppendUploadedUtcTokenCursor(where, parameters, sortDesc, cursor);
                return;
            }

            var op = sortDesc ? "<" : ">";

            var sortByValue = (int)sortBy;

            if (sortByValue == 2)
            {
                // length
                where.Append(" AND (length " + op + " $cursor_len OR (length = $cursor_len AND uploaded_utc " + op + " $cursor_uploaded) OR (length = $cursor_len AND uploaded_utc = $cursor_uploaded AND token " + op + " $cursor_token))");
                parameters.Add(new SqliteParameter("$cursor_len", extended.Length));
                parameters.Add(new SqliteParameter("$cursor_uploaded", extended.UploadedUtc.ToString("o", CultureInfo.InvariantCulture)));
                parameters.Add(new SqliteParameter("$cursor_token", extended.Token));
                return;
            }

            if (sortByValue == 3)
            {
                // ref_count
                where.Append(" AND (ref_count " + op + " $cursor_ref OR (ref_count = $cursor_ref AND uploaded_utc " + op + " $cursor_uploaded) OR (ref_count = $cursor_ref AND uploaded_utc = $cursor_uploaded AND token " + op + " $cursor_token))");
                parameters.Add(new SqliteParameter("$cursor_ref", extended.RefCount));
                parameters.Add(new SqliteParameter("$cursor_uploaded", extended.UploadedUtc.ToString("o", CultureInfo.InvariantCulture)));
                parameters.Add(new SqliteParameter("$cursor_token", extended.Token));
                return;
            }

            where.Append(" AND (uploaded_utc " + op + " $cursor_uploaded OR (uploaded_utc = $cursor_uploaded AND token " + op + " $cursor_token))");
            parameters.Add(new SqliteParameter("$cursor_uploaded", extended.UploadedUtc.ToString("o", CultureInfo.InvariantCulture)));
            parameters.Add(new SqliteParameter("$cursor_token", extended.Token));
        }

        private void AppendUploadedUtcTokenCursor(StringBuilder where, List<SqliteParameter> parameters, bool sortDesc, AttachmentQueryCursor cursor)
        {
            var op = sortDesc ? "<" : ">";
            where.Append(" AND (uploaded_utc " + op + " $cursor_uploaded OR (uploaded_utc = $cursor_uploaded AND token " + op + " $cursor_token))");
            parameters.Add(new SqliteParameter("$cursor_uploaded", cursor.UploadedUtc.ToString("o", CultureInfo.InvariantCulture)));
            parameters.Add(new SqliteParameter("$cursor_token", cursor.Token));
        }

        private string GetSortKeyString(MailQueueNet.Grpc.AttachmentSortBy sortBy, AttachmentIndexRow last)
        {
            var sortByValue = (int)sortBy;

            if (sortByValue == 2)
            {
                return last.Length.ToString(CultureInfo.InvariantCulture);
            }

            if (sortByValue == 3)
            {
                return last.RefCount.ToString(CultureInfo.InvariantCulture);
            }

            return string.Empty;
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

CREATE TABLE IF NOT EXISTS attachments (
  token TEXT NOT NULL PRIMARY KEY,
  uploaded_utc TEXT NOT NULL,
  length INTEGER NOT NULL,
  ref_count INTEGER NOT NULL,
  client_id TEXT NOT NULL,
  merge_owner_id TEXT NOT NULL,
  file_name TEXT NOT NULL,
  content_type TEXT NOT NULL,
  sha256_base64 TEXT NOT NULL,
  ready INTEGER NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_attachments_client_id ON attachments(client_id);
CREATE INDEX IF NOT EXISTS idx_attachments_merge_owner_id ON attachments(merge_owner_id);
CREATE INDEX IF NOT EXISTS idx_attachments_uploaded_utc ON attachments(uploaded_utc);
CREATE INDEX IF NOT EXISTS idx_attachments_ref_count ON attachments(ref_count);
CREATE INDEX IF NOT EXISTS idx_attachments_length ON attachments(length);
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
                    this.logger.LogInformation("Attachment index schema initialised (Version={Version})", SchemaVersion);
                }
            }
        }

        /// <summary>
        /// Updates the reference count, and optionally sets the merge owner id when the current value is empty.
        /// This is designed for use by store event notifications that do not have full manifest context.
        /// </summary>
        /// <param name="dbPath">Database path.</param>
        /// <param name="token">Attachment token.</param>
        /// <param name="refCount">Updated reference count.</param>
        /// <param name="mergeOwnerId">Merge owner id when known; otherwise empty.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UpdateRefCountAndMergeOwnerIdAsync(string dbPath, string token, int refCount, string mergeOwnerId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE attachments
SET ref_count = $ref_count,
    merge_owner_id = CASE
        WHEN ($merge_owner_id <> '' AND merge_owner_id = '') THEN $merge_owner_id
        ELSE merge_owner_id
    END,
    updated_utc = $updated_utc
WHERE token = $token;";

            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$ref_count", refCount);
            cmd.Parameters.AddWithValue("$merge_owner_id", mergeOwnerId ?? string.Empty);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
