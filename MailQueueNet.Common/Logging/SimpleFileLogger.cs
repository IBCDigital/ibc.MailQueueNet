//-----------------------------------------------------------------------
// <copyright file="SimpleFileLogger.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Common.Logging
{
    using System;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Implements <see cref="ILogger"/> by delegating writes to <see cref="SimpleFileLoggerProvider"/>.
    /// </summary>
    internal sealed class SimpleFileLogger : ILogger
    {
        private readonly SimpleFileLoggerProvider provider;
        private readonly string category;

        /// <summary>
        /// Initialises a new instance of the <see cref="SimpleFileLogger"/> class.
        /// </summary>
        /// <param name="provider">The owning provider.</param>
        /// <param name="category">The category name for this logger.</param>
        public SimpleFileLogger(SimpleFileLoggerProvider provider, string category)
        {
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
            this.category = category ?? string.Empty;
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
            {
                return;
            }

            this.provider.WriteLine(this.category, logLevel, message ?? string.Empty, exception);
        }
    }
}
