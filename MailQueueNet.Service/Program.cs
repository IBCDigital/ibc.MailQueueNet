// <copyright file="Program.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Service
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Security;
    using MailQueueNet.Common.Logging;
    using MailQueueNet.Service.Core;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Server.Kestrel.Https;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Resources;
    using OpenTelemetry.Trace;

    public class Program
    {
        private const string OverridesFileName = "appsettings.overrides.json";
        private const string OverridesDirectoryEnvVar = "MAILQUEUENET_CONFIG_DIR";

        public static void Main(string[] args)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    var overridesPath = ResolveOverridesPath();
                    if (File.Exists(overridesPath))
                    {
                        config.AddJsonFile(overridesPath, optional: true, reloadOnChange: true);
                    }
                })
                .ConfigureLogging((ctx, logging) =>
                {
                    var cfg = ctx.Configuration.GetSection("FileLogging");
                    var path = cfg["Path"] ?? "Logs";
                    var limitMb = cfg.GetValue<int?>("FileSizeLimitMb") ?? 10;
                    var maxFiles = cfg.GetValue<int?>("MaxFiles") ?? 10;

                    var contentRoot = ctx.HostingEnvironment.ContentRootPath ?? AppContext.BaseDirectory;
                    var basePath = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(contentRoot, path);
                    System.IO.Directory.CreateDirectory(basePath);

                    var bytes = limitMb * 1024 * 1024;

                    // Split providers by category prefix so we get separate files per type
                    logging.AddProvider(new SimpleFileLoggerProvider(basePath, bytes, maxFiles, filePrefix: "admin", allowedCategoryPrefix: "Admin"));
                    logging.AddProvider(new SimpleFileLoggerProvider(basePath, bytes, maxFiles, filePrefix: "mailreq", allowedCategoryPrefix: "MailRequest"));
                    logging.AddProvider(new SimpleFileLoggerProvider(basePath, bytes, maxFiles, filePrefix: "sent", allowedCategoryPrefix: "MailSent"));
                    logging.AddProvider(new SimpleFileLoggerProvider(basePath, bytes, maxFiles, filePrefix: "failed", allowedCategoryPrefix: "MailFailed"));
                    logging.AddProvider(new SimpleFileLoggerProvider(basePath, bytes, maxFiles, filePrefix: "service")); // catch-all
                })
                .UseConsoleLifetime()
#if Linux || Portable
                .UseSystemd()
#endif
#if Windows || Portable
                .UseWindowsService(x => x.ServiceName = "MailQueueNet")
#endif
                /*.ConfigureServices((ctx, services) =>
                {
                    // Health checks
                    services.AddHealthChecks();

                    // OpenTelemetry: resource
                    var resource = ResourceBuilder.CreateDefault()
                        .AddService(
                            serviceName: "MailQueueNet.Service",
                            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                            serviceInstanceId: Environment.MachineName)
                        .AddAttributes(new[]
                        {
                            new KeyValuePair<string, object>("deployment.environment", ctx.HostingEnvironment.EnvironmentName ?? "unknown"),
                        });

                    services.AddOpenTelemetry()
                        .ConfigureResource(rb => rb.AddAttributes(resource.Build().Attributes))
                        .WithMetrics(mb =>
                        {
                            mb.AddAspNetCoreInstrumentation();
                            mb.AddHttpClientInstrumentation();
                            mb.AddRuntimeInstrumentation();
                            mb.AddMeter("MailQueueNet.Service");
                            mb.AddOtlpExporter();
                        })
                        .WithTracing(tb =>
                        {
                            tb.AddAspNetCoreInstrumentation(o =>
                            {
                                o.RecordException = true;
                                o.EnrichWithHttpRequest = (activity, request) =>
                                {
                                    if (request.Headers.TryGetValue("x-client-id", out var id))
                                    {
                                        activity.SetTag("client.id", id.ToString());
                                    }
                                };
                            });
                            tb.AddHttpClientInstrumentation();
                            tb.AddOtlpExporter();
                        });

                    services.AddSingleton<Coordinator>();
                    services.AddHostedService<Worker>();
                })*/
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    // Kestrel HTTPS defaults; URLs/ports come from env/config
                    webBuilder.ConfigureKestrel(options =>
                    {
                        options.ConfigureHttpsDefaults(https =>
                        {
                            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                            https.CheckCertificateRevocation = false;
                            https.ClientCertificateValidation = (cert, chain, errors) =>
                            {
                                return errors == SslPolicyErrors.None ||
                                       errors == SslPolicyErrors.RemoteCertificateChainErrors;
                            };
                        });
                    });

                    webBuilder.UseStartup<WebStartup>();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // OpenTelemetry: resource
                    var resource = ResourceBuilder.CreateDefault()
                        .AddService(
                            serviceName: "MailQueueNet.Service",
                            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                            serviceInstanceId: Environment.MachineName)
                        .AddAttributes(new[]
                        {
                            new KeyValuePair<string, object>("deployment.environment", hostContext.HostingEnvironment.EnvironmentName ?? "unknown"),
                        });

                    services.AddOpenTelemetry()
                        .ConfigureResource(rb => rb.AddAttributes(resource.Build().Attributes))
                        .WithMetrics(mb =>
                        {
                            mb.AddAspNetCoreInstrumentation();
                            mb.AddRuntimeInstrumentation();
                            mb.AddMeter("MailQueueNet.Service");
                            mb.AddOtlpExporter();
                        })
                        .WithTracing(tb =>
                        {
                            tb.AddAspNetCoreInstrumentation(o =>
                            {
                                o.RecordException = true;
                                o.EnrichWithHttpRequest = (activity, request) =>
                                {
                                    if (request.Headers.TryGetValue("x-client-id", out var id))
                                    {
                                        activity.SetTag("client.id", id.ToString());
                                    }
                                };
                            });
                            tb.AddOtlpExporter();
                        });

                    services.AddSingleton<SqliteDispatcherStateStore>();
                    services.AddSingleton<SqliteAttachmentIndexStore>();
                    services.AddSingleton<IStagingRecipientAllowListStore, SqliteStagingRecipientAllowListStore>();
                    services.AddSingleton<IAttachmentIndexNotifier, AttachmentIndexNotifier>();
                    services.Configure<StagingMailRoutingOptions>(hostContext.Configuration.GetSection("StagingMailRouting"));
                    services.AddSingleton<IMailForgeDispatcher, MailForgeDispatcher>();
                    services.AddSingleton<IStagingMailRouter, StagingMailRouter>();
                    services.AddSingleton<MailMergeQueueWriter>();

                    services.AddSingleton<Coordinator>();
                    services.AddHostedService<Worker>();
                });
        }

        private static string ResolveOverridesPath()
        {
            var configDir = Environment.GetEnvironmentVariable(OverridesDirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                return Path.Combine(configDir, OverridesFileName);
            }

            if (Directory.Exists("/data/config"))
            {
                return Path.Combine("/data/config", OverridesFileName);
            }

            return Path.Combine(AppContext.BaseDirectory, OverridesFileName);
        }
    }
}