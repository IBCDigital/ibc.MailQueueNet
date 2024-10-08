﻿using MailQueueNet.Core.Logging;
using MailQueueNet.Service.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MailQueueNet.Service
{
    public class Program
    {
        public static void Main(string[] args)
        {
            MailQueueNetLogger mailQueueNetLogger = new MailQueueNetLogger();
            mailQueueNetLogger.ConfigureLogging();

            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .UseConsoleLifetime()
#if Linux || Portable
                .UseSystemd()
#endif
#if Windows || Portable
                .UseWindowsService(x => x.ServiceName = "MailQueueNet")
#endif
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<WebStartup>();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton<Coordinator>();
                    services.AddHostedService<Worker>();
                })
                .UseEnvironment("MAILQUEUENET");
        }
    }
}
