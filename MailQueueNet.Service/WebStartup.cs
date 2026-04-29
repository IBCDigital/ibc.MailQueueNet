// <copyright file="WebStartup.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
//
// Derived from “MailQueueNet” by Daniel Cohen Gindi
// (https://github.com/danielgindi/MailQueueNet).
//
// Original portions:
// ©2014 Daniel Cohen Gindi (danielgindi@gmail.com)
// Licensed under the MIT Licence.
// Modifications and additions:
// ©2025 IBC Digital Pty Ltd
// Distributed under the same MIT Licence.
//
// The above notice and this permission notice shall be included in
// all copies or substantial portions of this file.
// </copyright>

namespace MailQueueNet.Service
{
    using System;
    using System.Security.Claims;
    using MailQueueNet.Service.Core;
    using MailQueueNet.Service.GrpcServices;
    using Microsoft.AspNetCore.Authentication.Certificate;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// ASP.NET Core startup class for configuring services and the HTTP pipeline.
    /// </summary>
    public class WebStartup
    {
        private IConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebStartup"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        public WebStartup(IConfiguration configuration)
        {
            Console.WriteLine("[STARTUP] WebStartup.ctor executing");
            this.configuration = configuration;
        }

        /// <summary>
        /// Registers application services with the DI container.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            // Console output to verify execution when debugger breakpoints are skipped.
            Console.WriteLine("[STARTUP] WebStartup.ConfigureServices executing");

            services.AddGrpc(o =>
            {
                o.Interceptors.Add<Security.AuditInterceptor>();
            });
            services.AddSingleton<GrpcServices.MailService>();
            services.AddSingleton<Security.AuditNonceStore>();
            services.AddSingleton<Security.AuditNonceStore>();
            services.AddSingleton<SqliteDispatcherStateStore>();
            services.AddSingleton<SqliteAttachmentIndexStore>();
            services.AddSingleton<IAttachmentIndexNotifier, AttachmentIndexNotifier>();

            // mTLS / Client certificate authentication (optional per request)
            services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
                .AddCertificate(options =>
                {
                    options.AllowedCertificateTypes = CertificateTypes.All;
                    options.Events = new CertificateAuthenticationEvents
                    {
                        OnCertificateValidated = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<WebStartup>>();
                            var allowed = this.configuration.GetSection("Security:AdminCertThumbprints").Get<string[]>() ?? System.Array.Empty<string>();
                            var cert = context.ClientCertificate;
                            var thumbprint = cert?.Thumbprint;
                            if (!string.IsNullOrEmpty(thumbprint) && System.Array.Exists(allowed, t => t.Equals(thumbprint, System.StringComparison.OrdinalIgnoreCase)))
                            {
                                var claims = new[]
                                {
                                    new Claim(ClaimTypes.Name, cert.Subject),
                                    new Claim(ClaimTypes.Thumbprint, thumbprint),
                                    new Claim(ClaimTypes.Role, "Admin"),
                                };
                                context.Principal = new System.Security.Claims.ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                                context.Success();
                                logger.LogInformation("Admin mTLS connection accepted. Subject={Subject} Thumbprint={Thumbprint} NotBefore={NotBefore:o} NotAfter={NotAfter:o} Issuer={Issuer}", cert.Subject, thumbprint, cert.NotBefore, cert.NotAfter, cert.Issuer);
                            }
                            else
                            {
                                logger.LogWarning("Client certificate presented but not in admin allowlist. Subject={Subject} Thumbprint={Thumbprint}", cert?.Subject, thumbprint);
                                context.Fail("Untrusted admin client certificate");
                            }

                            return System.Threading.Tasks.Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<WebStartup>>();
                            logger.LogError(context.Exception, "Certificate authentication failed");
                            return System.Threading.Tasks.Task.CompletedTask;
                        },
                    };
                });

            services.AddAuthorization(o =>
            {
                o.AddPolicy("Admin", p => p.RequireRole("Admin"));
            });

            // Health checks
            services.AddHealthChecks();

            /*services.AddSingleton<Coordinator>();
            services.AddHostedService<Worker>();*/
        }

        /// <summary>
        /// Configures the HTTP request pipeline.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="env">The hosting environment.</param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            Console.WriteLine("[STARTUP] WebStartup.Configure executing");

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                // gRPC service
                endpoints.MapGrpcService<MailService>();

                // Health check endpoint
                endpoints.MapHealthChecks("/healthz");

                // Friendly root
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("MailQueueNet gRPC Service");
                });
            });

            var logger = app.ApplicationServices.GetRequiredService<ILogger<WebStartup>>();
            var serverAddresses = app.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
            logger.LogInformation("Env: ASPNETCORE_URLS={Urls}; HTTP_PORTS={Hp}; HTTPS_PORTS={Hs}",
                Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? string.Empty,
                Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? string.Empty,
                Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS") ?? string.Empty);
            if (serverAddresses is { Count: > 0 })
            {
                foreach (var a in serverAddresses)
                {
                    logger.LogInformation("Kestrel listening on {Address}", a);
                }
            }
            else
            {
                logger.LogWarning("No server addresses reported.");
            }
        }
    }
}