//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge
{
    using MailForge.Jobs;
    using MailForge.Queue;
    using MailForge.Security;
    using MailForge.Services;
    using MailForge.Template;
    using MailQueueNet.Common.Logging;
    using Microsoft.AspNetCore.Authentication.Certificate;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Server.Kestrel.Https;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Resources;
    using OpenTelemetry.Trace;
    using System;
    using System.Net.Security;
    using System.Security.Claims;

    /// <summary>
    /// Application entry point for the MailForge merge worker.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Application entry point.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddGrpc();
            builder.Services.AddHealthChecks();

            builder.WebHost.ConfigureKestrel(options =>
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

            // mTLS / Client certificate authentication (optional per request)
            builder.Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
                .AddCertificate(options =>
                {
                    options.AllowedCertificateTypes = CertificateTypes.All;
                    options.Events = new CertificateAuthenticationEvents
                    {
                        OnCertificateValidated = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MailForgeAuth");
                            var allowed = builder.Configuration.GetSection("Security:AdminCertThumbprints").Get<string[]>() ?? Array.Empty<string>();
                            var cert = context.ClientCertificate;
                            var thumbprint = cert?.Thumbprint;
                            if (!string.IsNullOrEmpty(thumbprint) && Array.Exists(allowed, t => t.Equals(thumbprint, StringComparison.OrdinalIgnoreCase)))
                            {
                                var claims = new[]
                                {
                                    new Claim(ClaimTypes.Name, cert?.Subject ?? string.Empty),
                                    new Claim(ClaimTypes.Thumbprint, thumbprint),
                                    new Claim(ClaimTypes.Role, "Admin"),
                                };
                                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                                context.Success();
                                logger.LogInformation("Admin mTLS connection accepted. Subject={Subject} Thumbprint={Thumbprint}", cert?.Subject, thumbprint);
                            }
                            else
                            {
                                logger.LogWarning("Client certificate presented but not in admin allowlist. Subject={Subject} Thumbprint={Thumbprint}", cert?.Subject, thumbprint);
                                context.Fail("Untrusted admin client certificate");
                            }

                            return System.Threading.Tasks.Task.CompletedTask;
                        },
                    };
                });

            builder.Services.AddAuthorization(o =>
            {
                o.AddPolicy("Admin", p => p.RequireRole("Admin"));
            });

            builder.Services.AddSingleton<SqliteMergeJobStore>();
            builder.Services.AddSingleton<MergeJobSummaryProvider>();
            builder.Services.AddSingleton<IMergeBatchStore, FileMergeBatchStore>();

            builder.Services.AddSingleton<SqliteFenceStore>();

            builder.Services.AddSingleton<NonceStore>();

            builder.Services.AddSingleton<LiquidTemplateRenderer>();
            builder.Services.AddSingleton<HandlebarsTemplateRenderer>();
            builder.Services.AddSingleton<ITemplateEngineResolver, TemplateEngineResolver>();

            builder.Services.Configure<MailForgeOptions>(builder.Configuration.GetSection("MailForge"));
            builder.Services.Configure<MailQueueOptions>(builder.Configuration.GetSection("MailQueue"));

            builder.Services.PostConfigure<MailForgeOptions>(options =>
            {
                if (options == null)
                {
                    return;
                }

                // Ensure the singular JobWorkRoot participates in the list-based probing behaviour.
                if (string.IsNullOrWhiteSpace(options.JobWorkRoot))
                {
                    return;
                }

                if (options.JobWorkRoots == null)
                {
                    options.JobWorkRoots = new System.Collections.Generic.List<string>();
                }

                var alreadyPresent = false;
                foreach (var root in options.JobWorkRoots)
                {
                    if (string.Equals(root, options.JobWorkRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyPresent = true;
                        break;
                    }
                }

                if (!alreadyPresent)
                {
                    options.JobWorkRoots.Insert(0, options.JobWorkRoot);
                }
            });

            builder.Services.AddSingleton<IMailQueueClient, MailQueueGrpcClient>();

            builder.Services.AddSingleton<IMergeJobRunner, MergeJobRunner>();

            ConfigureLogging(builder);
            ConfigureOpenTelemetry(builder);

            var app = builder.Build();

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGrpcService<MailForgeService>();
            app.MapHealthChecks("/healthz");
            app.MapGet("/", () => "MailForge gRPC Service");

            app.Run();
        }

        private static void ConfigureLogging(WebApplicationBuilder builder)
        {
            var cfg = builder.Configuration;
            var logPath = cfg["FileLogging:Path"];
            if (string.IsNullOrWhiteSpace(logPath))
            {
                logPath = "/data/logs";
            }

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddProvider(new SimpleFileLoggerProvider(logPath, "mailforge"));

            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

        private static void ConfigureOpenTelemetry(WebApplicationBuilder builder)
        {
            var serviceName = "MailForge";
            var serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
                .WithTracing(t => t
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter())
                .WithMetrics(m => m
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter());
        }
    }
}
