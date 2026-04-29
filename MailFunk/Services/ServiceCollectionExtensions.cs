// <copyright file="ServiceCollectionExtensions.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// DI registration helpers.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds services required for the dashboard.
        /// </summary>
        /// <param name="services">The DI container.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddMailFunkServices(this IServiceCollection services)
        {
            services.AddSingleton<IFolderSummaryService, FolderSummaryService>();
            services.AddSingleton<IConnectivityService, ConnectivityService>();
            services.AddSingleton<IMailMergeSummaryService, MailMergeSummaryService>();
            services.AddSingleton<IMergeDispatchStateService, MergeDispatchStateService>();
            services.AddSingleton<AllowedTestRecipientsService>();
            return services;
        }
    }
}
