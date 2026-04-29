//-----------------------------------------------------------------------
// <copyright file="SqliteFenceStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Security
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides persistent storage for the highest accepted dispatch fence token.
    /// </summary>
    /// <remarks>
    /// This is used to enforce “lease authority” on a MailForge worker instance.
    /// The queue service includes an <c>x-dispatch-fence</c> header on mutating calls.
    /// MailForge only accepts calls whose fence token is greater than or equal to the
    /// last accepted fence token.
    /// </remarks>
    public sealed class SqliteFenceStore
    {
        private const int SchemaVersion = 1;

        private readonly ILogger<SqliteFenceStore> logger;

        /// <summary>
        /// Initialises a new instance of the <see cref="SqliteFenceStore"/> class.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        public SqliteFenceStore(ILogger<SqliteFenceStore> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Ensures the fence database exists and the schema is initialised.
        /// </summary>
        /// <param name="dbPath">SQLite database path.</param>
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
        /// Validates a candidate fence token against the stored value, updating the stored value
        /// when the token is accepted.
        /// </summary>
        /// <param name="dbPath">SQLite database path.</param>
        /// <param name="candidateFenceToken">Fence token from the request header.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// <see langword="true"/> if the token is accepted (greater than or equal to the stored token);
        /// otherwise <see langword="false"/>.
        /// </returns>
        public async Task<bool> TryAcceptFenceAsync(string dbPath, long candidateFenceToken, CancellationToken cancellationToken)
        {
            await this.EnsureSchemaAsync(dbPath, cancellationToken).ConfigureAwait(false);

            await using var conn = OpenConnection(dbPath);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Use an immediate transaction so concurrent requests serialise.
            await using var dbTx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var tx = (SqliteTransaction)dbTx;

            long current;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT fence_token FROM fence WHERE fence_id = 1;";
                var obj = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                current = Convert.ToInt64(obj ?? 0, CultureInfo.InvariantCulture);
            }

            if (candidateFenceToken < current)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = @"
INSERT INTO fence (fence_id, fence_token, updated_utc)
VALUES (1, $fence_token, $updated_utc)
ON CONFLICT(fence_id) DO UPDATE SET
  fence_token = excluded.fence_token,
  updated_utc = excluded.updated_utc;";

                upsert.Parameters.AddWithValue("$fence_token", candidateFenceToken);
                upsert.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                _ = await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
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

CREATE TABLE IF NOT EXISTS fence (
  fence_id INTEGER NOT NULL PRIMARY KEY,
  fence_token INTEGER NOT NULL,
  updated_utc TEXT NOT NULL
);";

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
                    this.logger.LogInformation("Fence schema initialised (Version={Version})", SchemaVersion);
                }
            }
        }
    }
}
