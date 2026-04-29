//-----------------------------------------------------------------------
// <copyright file="SimpleFileLoggerProvider.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Common.Logging
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides a lightweight rolling file logger suitable for development diagnostics.
    /// Supports size-based rotation, daily rotation, limited retention, and optional
    /// category prefix filtering.
    /// </summary>
    public sealed class SimpleFileLoggerProvider : ILoggerProvider
    {
        /// <summary>
        /// Default file size limit used when an invalid size is provided.
        /// </summary>
        private const long DefaultFileSizeLimitBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Default maximum number of files retained when an invalid value is provided.
        /// </summary>
        private const int DefaultMaxFiles = 5;

        private readonly string directory;
        private readonly string filePrefix;
        private readonly string allowedCategoryPrefix;
        private readonly long fileSizeLimitBytes;
        private readonly int maxFiles;
        private readonly bool includeTimeInFileName;
        private readonly ConcurrentDictionary<string, SimpleFileLogger> loggers;
        private readonly object sync;

        private string currentFilePath;
        private DateTime currentFileUtcDate;
        private FileStream? stream;

        /// <summary>
        /// Initialises a new instance of the <see cref="SimpleFileLoggerProvider"/> class.
        /// </summary>
        /// <param name="directory">The base directory to write log files into.</param>
        /// <param name="fileSizeLimitBytes">The maximum size in bytes before a log file is rotated.</param>
        /// <param name="maxFiles">The maximum number of log files to retain.</param>
        /// <param name="filePrefix">The log file name prefix (for example, <c>service</c> or <c>admin</c>).</param>
        /// <param name="allowedCategoryPrefix">Only categories starting with this prefix are written. Use empty to accept all.</param>
        public SimpleFileLoggerProvider(
            string directory,
            long fileSizeLimitBytes,
            int maxFiles,
            string filePrefix = "log",
            string allowedCategoryPrefix = "")
            : this(directory, fileSizeLimitBytes, maxFiles, filePrefix, allowedCategoryPrefix, includeTimeInFileName: true)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="SimpleFileLoggerProvider"/> class.
        /// This overload matches the original MailForge provider signature and writes a single
        /// file per day named <c>{prefix}_yyyyMMdd.txt</c>.
        /// </summary>
        /// <param name="baseFolder">Base folder to write logs to.</param>
        /// <param name="prefix">File prefix (for example, service name).</param>
        public SimpleFileLoggerProvider(string baseFolder, string prefix)
            : this(
                baseFolder,
                long.MaxValue,
                maxFiles: 30,
                filePrefix: string.IsNullOrWhiteSpace(prefix) ? "log" : prefix,
                allowedCategoryPrefix: string.Empty,
                includeTimeInFileName: false)
        {
        }

        private SimpleFileLoggerProvider(
            string directory,
            long fileSizeLimitBytes,
            int maxFiles,
            string filePrefix,
            string allowedCategoryPrefix,
            bool includeTimeInFileName)
        {
            this.directory = directory ?? string.Empty;
            this.fileSizeLimitBytes = fileSizeLimitBytes > 0 ? fileSizeLimitBytes : DefaultFileSizeLimitBytes;
            this.maxFiles = maxFiles > 0 ? maxFiles : DefaultMaxFiles;
            this.filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "log" : filePrefix;
            this.allowedCategoryPrefix = allowedCategoryPrefix ?? string.Empty;
            this.includeTimeInFileName = includeTimeInFileName;

            this.loggers = new ConcurrentDictionary<string, SimpleFileLogger>();
            this.sync = new object();

            Directory.CreateDirectory(this.directory);
            this.currentFilePath = this.GetLatestFilePath();
            this.currentFileUtcDate = this.TryParseLogFileUtcDate(this.currentFilePath) ?? DateTime.UtcNow.Date;
            this.stream = new FileStream(this.currentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return this.loggers.GetOrAdd(categoryName ?? string.Empty, n => new SimpleFileLogger(this, n));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (this.sync)
            {
                try
                {
                    this.stream?.Dispose();
                }
                catch
                {
                }
                finally
                {
                    this.stream = null;
                }
            }
        }

        /// <summary>
        /// Writes a log entry to the current file, rotating if required.
        /// Any I/O errors are swallowed to avoid breaking application behaviour.
        /// </summary>
        /// <param name="category">The logger category name.</param>
        /// <param name="logLevel">The log level.</param>
        /// <param name="message">The formatted message.</param>
        /// <param name="exception">An optional exception.</param>
        internal void WriteLine(string category, LogLevel logLevel, string message, Exception? exception)
        {
            if (!this.AllowCategory(category))
            {
                return;
            }

            var line = $"{DateTime.UtcNow:O}|{logLevel}|{category}|{message}";
            if (exception != null)
            {
                line += "|" + exception;
            }

            lock (this.sync)
            {
                try
                {
                    this.RotateIfNeeded();
                    this.WriteLineToStream(line);
                }
                catch
                {
                    try
                    {
                        this.RotateToNewFile(DateTime.UtcNow);
                        this.WriteLineToStream(line);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool AllowCategory(string category)
        {
            if (string.IsNullOrEmpty(this.allowedCategoryPrefix))
            {
                return true;
            }

            return (category ?? string.Empty).StartsWith(this.allowedCategoryPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private void RotateIfNeeded()
        {
            if (this.stream == null)
            {
                this.RotateToNewFile(DateTime.UtcNow);
                return;
            }

            var nowUtc = DateTime.UtcNow;

            if (nowUtc.Date != this.currentFileUtcDate)
            {
                this.RotateToNewFile(nowUtc);
                return;
            }

            try
            {
                if (!File.Exists(this.currentFilePath))
                {
                    this.RotateToNewFile(nowUtc);
                    return;
                }

                if (this.stream.Length < this.fileSizeLimitBytes)
                {
                    return;
                }
            }
            catch (FileNotFoundException)
            {
                this.RotateToNewFile(nowUtc);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                this.RotateToNewFile(nowUtc);
                return;
            }
            catch (IOException)
            {
                this.RotateToNewFile(nowUtc);
                return;
            }

            this.RotateToNewFile(nowUtc);
        }

        private void RotateToNewFile(DateTime nowUtc)
        {
            try
            {
                this.stream?.Dispose();
            }
            catch
            {
            }

            Directory.CreateDirectory(this.directory);

            this.currentFilePath = this.GetNewFilePath(nowUtc);

            var mode = this.includeTimeInFileName ? FileMode.Create : FileMode.Append;
            this.stream = new FileStream(this.currentFilePath, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            this.currentFileUtcDate = nowUtc.Date;

            this.TrimOldFiles();
        }

        private void WriteLineToStream(string line)
        {
            if (this.stream == null)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            this.stream.Write(bytes, 0, bytes.Length);
            this.stream.Flush();
        }

        private void TrimOldFiles()
        {
            try
            {
                if (this.maxFiles <= 0)
                {
                    return;
                }

                var di = new DirectoryInfo(this.directory);
                if (!di.Exists)
                {
                    return;
                }

                var files = di
                    .GetFiles($"{this.filePrefix}_*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                for (var i = this.maxFiles; i < files.Count; i++)
                {
                    try
                    {
                        files[i].Delete();
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

        private string GetLatestFilePath()
        {
            if (!this.includeTimeInFileName)
            {
                return this.GetNewFilePath(DateTime.UtcNow);
            }

            var nowUtc = DateTime.UtcNow;

            try
            {
                var di = new DirectoryInfo(this.directory);
                if (!di.Exists)
                {
                    return this.GetNewFilePath(nowUtc);
                }

                var latest = di
                    .GetFiles($"{this.filePrefix}_*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                {
                    return this.GetNewFilePath(nowUtc);
                }

                var fileDate = this.TryParseLogFileUtcDate(latest.FullName);
                if (fileDate.HasValue && fileDate.Value != nowUtc.Date)
                {
                    return this.GetNewFilePath(nowUtc);
                }

                if (latest.Length < this.fileSizeLimitBytes)
                {
                    return latest.FullName;
                }

                return this.GetNewFilePath(nowUtc);
            }
            catch
            {
                return this.GetNewFilePath(nowUtc);
            }
        }

        private string GetNewFilePath(DateTime nowUtc)
        {
            if (this.includeTimeInFileName)
            {
                return Path.Combine(this.directory, $"{this.filePrefix}_{nowUtc:yyyyMMdd_HHmmss}.txt");
            }

            return Path.Combine(this.directory, $"{this.filePrefix}_{nowUtc:yyyyMMdd}.txt");
        }

        private DateTime? TryParseLogFileUtcDate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var expectedPrefix = this.filePrefix + "_";
            if (!name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var tail = name.Substring(expectedPrefix.Length);
            if (tail.Length < 8)
            {
                return null;
            }

            var datePart = tail.Substring(0, 8);
            if (!DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            return date.Date;
        }
    }
}
