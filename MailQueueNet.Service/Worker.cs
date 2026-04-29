// <copyright file="Worker.cs" company="IBC Digital">
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

namespace MailQueueNet.Service
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Service.Core;
    using MailQueueNet.Service.Internal;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Primitives;

    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly IConfiguration configuration;
        private readonly Coordinator coordinator;
        private readonly IMailForgeDispatcher dispatcher;
        private readonly Debouncer debouncer = new(TimeSpan.FromSeconds(0.5));
        private IDisposable registeredOnChange = null;

        public Worker(ILogger<Worker> logger, IConfiguration configuration, ILoggerFactory loggerFactory, Coordinator coordinator, IMailForgeDispatcher dispatcher)
        {
            this.logger = logger;
            this.loggerFactory = loggerFactory;
            this.configuration = configuration;
            this.coordinator = coordinator;
            this.dispatcher = dispatcher;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                this.registeredOnChange?.Dispose();
                this.registeredOnChange = ChangeToken.OnChange<object>(this.configuration.GetReloadToken, async (_) => await this.debouncer.Debounce(() =>
                {
                    this.coordinator.RefreshSettings();
                    this.dispatcher.RefreshSettings();
                }), null);

                this.coordinator.RefreshSettings();
                this.dispatcher.RefreshSettings();

                await this.coordinator.Run(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                this.registeredOnChange?.Dispose();
            }
        }
    }
}
