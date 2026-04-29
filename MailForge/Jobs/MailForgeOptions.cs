//-----------------------------------------------------------------------
// <copyright file="MailForgeOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Jobs
{
    using System.Collections.Generic;

    /// <summary>
    /// Represents MailForge configuration options.
    /// </summary>
    public sealed class MailForgeOptions
    {
        /// <summary>
        /// Gets or sets the default job work root used when processing merge jobs.
        /// </summary>
        /// <remarks>
        /// This is a convenience configuration value for deployments that only need a single work root.
        /// When set, it is automatically added to <see cref="JobWorkRoots"/>.
        /// </remarks>
        public string JobWorkRoot { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default job work roots to probe when a caller does not supply
        /// a job work root for status lookups.
        /// </summary>
        public IList<string> JobWorkRoots { get; set; } = new List<string>();
    }
}
