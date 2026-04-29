//-----------------------------------------------------------------------
// <copyright file="MergeJobSummaryProvider.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MailForge.Grpc;

    /// <summary>
    /// Provides read-only reporting queries over MailForge per-job SQLite state.
    /// </summary>
    public sealed class MergeJobSummaryProvider
    {
        private static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(7);

        private readonly SqliteMergeJobStore jobStore;

        /// <summary>
        /// Gets the underlying job store used for reporting queries.
        /// </summary>
        public SqliteMergeJobStore JobStore
        {
            get
            {
                return this.jobStore;
            }
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="MergeJobSummaryProvider"/> class.
        /// </summary>
        /// <param name="jobStore">Merge job store used to read job state.</param>
        public MergeJobSummaryProvider(SqliteMergeJobStore jobStore)
        {
            this.jobStore = jobStore;
        }

        /// <summary>
        /// Lists merge jobs by scanning for job DB files under a set of candidate job work roots.
        /// </summary>
        /// <param name="candidateWorkRoots">Candidate work roots to scan.</param>
        /// <param name="take">Maximum jobs to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of job summaries ordered by most recently updated.</returns>
        public async Task<IReadOnlyList<MergeJobSummary>> ListJobsAsync(IEnumerable<string> candidateWorkRoots, int take, CancellationToken cancellationToken)
        {
            if (take <= 0)
            {
                take = 50;
            }

            var roots = (candidateWorkRoots ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0)
            {
                return Array.Empty<MergeJobSummary>();
            }

            var dbFiles = new List<string>();
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    dbFiles.AddRange(Directory.GetFiles(root, "job.db", SearchOption.AllDirectories));
                }
                catch
                {
                }
            }

            var cutoffUtc = DateTime.UtcNow - DefaultLookback;

            var candidates = dbFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FileInfo(path))
                .Where(fi => fi.Exists && fi.LastWriteTimeUtc >= cutoffUtc)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(take)
                .Select(fi => fi.FullName)
                .ToArray();

            var results = new List<MergeJobSummary>();
            foreach (var dbPath in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mergeId = TryExtractJobIdFromJobDbPath(dbPath);
                if (string.IsNullOrWhiteSpace(mergeId))
                {
                    continue;
                }

                var snapshot = await this.jobStore.TryReadJobAsync(dbPath, mergeId, cancellationToken).ConfigureAwait(false);
                if (snapshot == null)
                {
                    continue;
                }

                var summary = ToSummary(snapshot);
                try
                {
                    var batches = await this.jobStore.ListBatchesAsync(dbPath, mergeId, cancellationToken).ConfigureAwait(false);
                    summary.BatchCount = batches.Count;
                }
                catch
                {
                    summary.BatchCount = 0;
                }

                results.Add(summary);
            }

            return results
                .OrderByDescending(r => r.FinishedUtc)
                .ThenByDescending(r => r.StartedUtc)
                .Take(take)
                .ToArray();
        }

        /// <summary>
        /// Reads a single job snapshot and maps it to the reporting detail reply.
        /// </summary>
        /// <param name="dbPath">Job database path.</param>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The job summary or null if not found.</returns>
        public async Task<MergeJobSummary?> TryGetJobSummaryAsync(string dbPath, string mergeId, CancellationToken cancellationToken)
        {
            var snapshot = await this.jobStore.TryReadJobAsync(dbPath, mergeId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
            {
                return null;
            }

            var summary = ToSummary(snapshot);
            try
            {
                var batches = await this.jobStore.ListBatchesAsync(dbPath, mergeId, cancellationToken).ConfigureAwait(false);
                summary.BatchCount = batches.Count;
            }
            catch
            {
                summary.BatchCount = 0;
            }

            return summary;
        }

        private static MergeJobSummary ToSummary(MergeJobSnapshot snapshot)
        {
            return new MergeJobSummary
            {
                MergeId = snapshot.JobId ?? string.Empty,
                Status = snapshot.Status.ToString(),
                ClientId = snapshot.ClientId ?? string.Empty,
                StartedUtc = snapshot.StartedUtc?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
                FinishedUtc = snapshot.FinishedUtc?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
                TotalRows = snapshot.Total,
                CompletedRows = snapshot.Completed,
                FailedRows = snapshot.Failed,
                BatchCount = 0,
                LastError = snapshot.LastError ?? string.Empty,
            };
        }

        private static string TryExtractJobIdFromJobDbPath(string dbPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return string.Empty;
                }

                return new DirectoryInfo(dir).Name;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
