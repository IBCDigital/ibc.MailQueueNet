// <copyright file="SqliteStagingRecipientAllowListStore.cs" company="IBC Digital">
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
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// SQLite-backed storage for staging-only recipient allow-lists.
    /// </summary>
    public sealed class SqliteStagingRecipientAllowListStore : IStagingRecipientAllowListStore
    {
        private readonly string dbPath;

        /// <summary>
        /// Initialises a new instance of the <see cref="SqliteStagingRecipientAllowListStore"/> class.
        /// </summary>
        /// <param name="configuration">Application configuration.</param>
        public SqliteStagingRecipientAllowListStore(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var configured = configuration["StagingMailRouting:AllowListDbPath"];
            this.dbPath = string.IsNullOrWhiteSpace(configured)
                ? "/data/db/staging_mail_routing.db"
                : configured;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> ListAsync(string clientId, CancellationToken cancellationToken)
        {
            var normalisedClientId = NormaliseClientId(clientId);
            if (string.IsNullOrWhiteSpace(normalisedClientId))
            {
                return Array.Empty<string>();
            }

            await this.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            var items = new List<string>();

            await using var conn = this.OpenConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT email_address
FROM staging_allowed_recipient
WHERE client_id = $client_id
ORDER BY email_address;";
            cmd.Parameters.AddWithValue("$client_id", normalisedClientId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(reader.GetString(0));
            }

            return items;
        }

        /// <inheritdoc />
        public async Task<bool> AddAsync(string clientId, string emailAddress, CancellationToken cancellationToken)
        {
            var normalisedClientId = NormaliseClientId(clientId);
            var normalisedEmail = NormaliseEmail(emailAddress);
            if (string.IsNullOrWhiteSpace(normalisedClientId) || string.IsNullOrWhiteSpace(normalisedEmail))
            {
                return false;
            }

            await this.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            await using var conn = this.OpenConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO staging_allowed_recipient (client_id, email_address, created_utc)
VALUES ($client_id, $email_address, $created_utc)
ON CONFLICT(client_id, email_address) DO NOTHING;";
            cmd.Parameters.AddWithValue("$client_id", normalisedClientId);
            cmd.Parameters.AddWithValue("$email_address", normalisedEmail);
            cmd.Parameters.AddWithValue("$created_utc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            var changed = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return changed > 0;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string clientId, string emailAddress, CancellationToken cancellationToken)
        {
            var normalisedClientId = NormaliseClientId(clientId);
            var normalisedEmail = NormaliseEmail(emailAddress);
            if (string.IsNullOrWhiteSpace(normalisedClientId) || string.IsNullOrWhiteSpace(normalisedEmail))
            {
                return false;
            }

            await this.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            await using var conn = this.OpenConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM staging_allowed_recipient
WHERE client_id = $client_id AND email_address = $email_address;";
            cmd.Parameters.AddWithValue("$client_id", normalisedClientId);
            cmd.Parameters.AddWithValue("$email_address", normalisedEmail);

            var changed = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return changed > 0;
        }

        private static string NormaliseClientId(string clientId)
        {
            return (clientId ?? string.Empty).Trim();
        }

        private static string NormaliseEmail(string emailAddress)
        {
            return (emailAddress ?? string.Empty).Trim().ToLowerInvariant();
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            var folder = Path.GetDirectoryName(this.dbPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            await using var conn = this.OpenConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS staging_allowed_recipient (
    client_id TEXT NOT NULL,
    email_address TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    PRIMARY KEY (client_id, email_address)
);";
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private SqliteConnection OpenConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = this.dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            };

            return new SqliteConnection(builder.ToString());
        }
    }
}
