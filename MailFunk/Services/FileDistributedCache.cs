//-----------------------------------------------------------------------
// <copyright file="FileDistributedCache.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Caching.Distributed;

    /// <summary>
    /// Provides a very small file-backed <see cref="IDistributedCache"/> implementation.
    /// </summary>
    /// <remarks>
    /// This cache is intended for low-volume staging environments where persisting the
    /// Microsoft Identity token cache to the container volume is preferred over in-memory
    /// caching.
    /// </remarks>
    internal sealed class FileDistributedCache : IDistributedCache
    {
        private static readonly DistributedCacheEntryOptions DefaultEntryOptions = new DistributedCacheEntryOptions();

        private readonly string rootPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileDistributedCache"/> class.
        /// </summary>
        /// <param name="rootPath">The directory path used to store cached items.</param>
        public FileDistributedCache(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("Cache root path must be specified.", nameof(rootPath));
            }

            this.rootPath = rootPath;
            Directory.CreateDirectory(this.rootPath);
        }

        /// <summary>
        /// Retrieves a cached value by key.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <returns>The cached value, or <see langword="null"/> if no value exists.</returns>
        public byte[]? Get(string key)
        {
            return this.GetAsync(key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Retrieves a cached value by key.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="token">A cancellation token.</param>
        /// <returns>A task producing the cached value, or <see langword="null"/> if no value exists.</returns>
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var path = this.GetFilePath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stores a cached value.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The value to store.</param>
        /// <param name="options">Cache entry options.</param>
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            this.SetAsync(key, value, options).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Stores a cached value.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The value to store.</param>
        /// <param name="options">Cache entry options.</param>
        /// <param name="token">A cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _ = options ?? DefaultEntryOptions;

            var path = this.GetFilePath(key);
            var tempPath = path + ".tmp";

            Directory.CreateDirectory(this.rootPath);

            await File.WriteAllBytesAsync(tempPath, value, token).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }

        /// <summary>
        /// Refreshes a cache entry.
        /// </summary>
        /// <param name="key">The cache key.</param>
        public void Refresh(string key)
        {
            this.RefreshAsync(key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Refreshes a cache entry.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="token">A cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Removes a cached value.
        /// </summary>
        /// <param name="key">The cache key.</param>
        public void Remove(string key)
        {
            this.RemoveAsync(key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Removes a cached value.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="token">A cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var path = this.GetFilePath(key);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }

            return Task.CompletedTask;
        }

        private string GetFilePath(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Cache key must be specified.", nameof(key));
            }

            var safeName = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            return Path.Combine(this.rootPath, safeName + ".bin");
        }
    }
}
