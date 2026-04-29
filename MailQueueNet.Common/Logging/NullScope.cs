//-----------------------------------------------------------------------
// <copyright file="NullScope.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Common.Logging
{
    using System;

    /// <summary>
    /// Represents a no-op scope used by <see cref="Microsoft.Extensions.Logging.ILogger.BeginScope{TState}(TState)"/>.
    /// </summary>
    internal sealed class NullScope : IDisposable
    {
        /// <summary>
        /// Gets a cached instance to avoid allocations.
        /// </summary>
        public static readonly NullScope Instance = new NullScope();

        private NullScope()
        {
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
