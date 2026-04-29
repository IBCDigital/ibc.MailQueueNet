//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using System.Text.Json;
    using MailFunk.Components;
    using MailFunk.Logging;
    using MailFunk.Services;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.Cookies;
    using Microsoft.AspNetCore.Authentication.Negotiate;
    using Microsoft.AspNetCore.Authentication.OpenIdConnect;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.DataProtection;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.HttpOverrides;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Identity.Web;
    using Microsoft.IdentityModel.Protocols.OpenIdConnect;
    using MudBlazor.Services;
    using OpenTelemetry.Metrics;
    using OpenTelemetry.Resources;
    using OpenTelemetry.Trace;

    /// <summary>
    /// Application entry point for the MailFunk Blazor Server user interface.
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

            if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:Instance"]))
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                });
            }

            if (!string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]) &&
                string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:Authority"]))
            {
                var configuredInstance = builder.Configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/";
                if (!configuredInstance.EndsWith("/", StringComparison.Ordinal))
                {
                    configuredInstance += "/";
                }

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:Authority"] = $"{configuredInstance}{builder.Configuration["AzureAd:TenantId"]}/v2.0",
                });
            }

            // Add custom file logging (must be before Build()).
            builder.Logging.AddSimpleFile(builder.Configuration, builder.Environment.ContentRootPath);

            ConfigureOpenTelemetry(builder);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // MudBlazor.
            builder.Services.AddMudServices();

            // Register services.
            builder.Services.AddMailFunkServices();

            // Try to load admin client certificate (dev / admin operations).
            X509Certificate2? adminClientCert = LoadAdminClientCert(builder.Configuration);

            // gRPC client for MailQueueNet service.
            builder.Services.AddTransient<MailFunk.GrpcInterceptors.AdminSharedSecretInterceptor>();

            builder.Services.AddGrpcClient<MailQueueNet.Grpc.MailGrpcService.MailGrpcServiceClient>(o =>
            {
                var baseAddress = builder.Configuration["MailService:BaseAddress"];
                if (string.IsNullOrWhiteSpace(baseAddress))
                {
                    throw new InvalidOperationException("MailService:BaseAddress configuration is missing.");
                }

                o.Address = new Uri(baseAddress);
            })
            .AddInterceptor<MailFunk.GrpcInterceptors.AdminSharedSecretInterceptor>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateGrpcHttpHandler(builder, adminClientCert));

            // gRPC client for MailForge service (admin reporting).
            builder.Services.AddGrpcClient<MailForge.Grpc.MailForgeService.MailForgeServiceClient>(o =>
            {
                var baseAddress = builder.Configuration["MailForge:BaseAddress"];
                if (string.IsNullOrWhiteSpace(baseAddress))
                {
                    throw new InvalidOperationException("MailForge:BaseAddress configuration is missing.");
                }

                o.Address = new Uri(baseAddress);
            })
            .AddInterceptor<MailFunk.GrpcInterceptors.AdminSharedSecretInterceptor>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateGrpcHttpHandler(builder, adminClientCert));

            // Azure AD (Entra ID) + Windows SSO smart authentication configuration.
            // These values are required by Microsoft.Identity.Web for token acquisition.
            var azureAdSection = builder.Configuration.GetSection("AzureAd");
            var tenantId = azureAdSection["TenantId"]; // GUID or domain.
            var clientId = azureAdSection["ClientId"]; // App Registration ClientId.
            var clientSecret = azureAdSection["ClientSecret"]; // Confidential client secret.
            var instance = azureAdSection["Instance"];

            if (string.IsNullOrWhiteSpace(instance))
            {
                instance = "https://login.microsoftonline.com/";
                builder.Configuration["AzureAd:Instance"] = instance;
            }

            if (!instance.EndsWith("/", StringComparison.Ordinal))
            {
                instance += "/";
                builder.Configuration["AzureAd:Instance"] = instance;
            }

            if (!string.IsNullOrWhiteSpace(tenantId) && string.IsNullOrWhiteSpace(azureAdSection["Authority"]))
            {
                builder.Configuration["AzureAd:Authority"] = $"{instance}{tenantId}/v2.0";
            }

            if (Directory.Exists("/data"))
            {
                builder.Services.AddDataProtection()
                    .SetApplicationName("MailFunk")
                    .PersistKeysToFileSystem(new DirectoryInfo("/data/dataprotection-keys"));

                builder.Services.AddSingleton<IDistributedCache>(_ => new FileDistributedCache("/data/token-cache"));
            }
            else
            {
                builder.Services.AddDistributedMemoryCache();
            }

            var authenticationBuilder = builder.Services.AddAuthentication(options =>
            {
                // Use cookies for normal AuthenticateAsync() to prevent policy-scheme recursion.
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                // Use policy scheme only for challenge/forbid so we can choose Negotiate vs OIDC.
                options.DefaultChallengeScheme = "Smart";
                options.DefaultForbidScheme = "Smart";
            })
            .AddPolicyScheme("Smart", "Smart scheme", o =>
            {
                // Forward Authenticate always to cookies (important to prevent loops).
                o.ForwardAuthenticate = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ForwardSignIn = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ForwardSignOut = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ForwardDefaultSelector = ctx =>
                {
                    // If browser sent Negotiate header, attempt Windows integrated.
                    if (ctx.Request.Headers.ContainsKey("Authorization") && ctx.Request.Headers["Authorization"].ToString().StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase))
                    {
                        return NegotiateDefaults.AuthenticationScheme;
                    }

                    // Fallback to OpenIdConnect for interactive challenge.
                    return OpenIdConnectDefaults.AuthenticationScheme;
                };
            })
            .AddNegotiate()
            .AddMicrosoftIdentityWebApp(
                builder.Configuration.GetSection("AzureAd"),
                openIdConnectScheme: OpenIdConnectDefaults.AuthenticationScheme,
                cookieScheme: CookieAuthenticationDefaults.AuthenticationScheme,
                subscribeToOpenIdConnectMiddlewareDiagnosticsEvents: false)
            .EnableTokenAcquisitionToCallDownstreamApi(new[] { "User.Read" })
            .AddDistributedTokenCaches();

            authenticationBuilder.Services.Configure<MicrosoftIdentityOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Instance = instance;
                options.TenantId = tenantId;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
            });

            authenticationBuilder.Services.PostConfigureAll<MicrosoftIdentityOptions>(options =>
            {
                if (string.IsNullOrWhiteSpace(options.Instance))
                {
                    options.Instance = instance;
                }

                if (string.IsNullOrWhiteSpace(options.TenantId))
                {
                    options.TenantId = tenantId;
                }

                if (string.IsNullOrWhiteSpace(options.ClientId))
                {
                    options.ClientId = clientId;
                }

                if (string.IsNullOrWhiteSpace(options.ClientSecret))
                {
                    options.ClientSecret = clientSecret;
                }
            });

            builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            });

            builder.Services.AddAuthorization(o =>
            {
                o.FallbackPolicy = o.DefaultPolicy;
            });

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHttpClient("Graph", c =>
            {
                c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            });

            builder.Services.AddScoped<IUserProfileService, UserProfileService>();

            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedHost |
                    ForwardedHeaders.XForwardedProto;

                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            var app = builder.Build();

            app.Logger.LogInformation(
                "Entra ID configuration loaded. Instance={Instance} TenantId={TenantId} ClientId={ClientId} Authority={Authority}",
                instance ?? string.Empty,
                tenantId ?? string.Empty,
                clientId ?? string.Empty,
                builder.Configuration["AzureAd:Authority"] ?? string.Empty);

            app.Use((context, next) =>
            {
                if (!context.Request.Headers.ContainsKey("X-Forwarded-Proto") &&
                    context.Request.Headers.TryGetValue("X-Forwarded-Scheme", out var forwardedScheme))
                {
                    context.Request.Headers["X-Forwarded-Proto"] = forwardedScheme.ToString();
                }

                return next();
            });

            app.UseForwardedHeaders();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
                app.UseHsts();
            }

            if (app.Environment.IsStaging())
            {
                app.MapGet("/debug/entra", async context =>
                {
                    var schemeProvider = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
                    var schemes = await schemeProvider.GetAllSchemesAsync().ConfigureAwait(false);

                    var miOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<MicrosoftIdentityOptions>>()
                        .Get(OpenIdConnectDefaults.AuthenticationScheme);

                    var oidcOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                        .Get(OpenIdConnectDefaults.AuthenticationScheme);

                    var config = context.RequestServices.GetRequiredService<IConfiguration>();
                    var azureAd = config.GetSection("AzureAd");

                    var payload = new
                    {
                        AzureAd = new
                        {
                            Instance = azureAd["Instance"],
                            Authority = azureAd["Authority"],
                            TenantId = azureAd["TenantId"],
                            ClientId = azureAd["ClientId"],
                            HasClientSecret = !string.IsNullOrWhiteSpace(azureAd["ClientSecret"]),
                        },
                        MicrosoftIdentityOptions = new
                        {
                            miOptions.Instance,
                            miOptions.TenantId,
                            miOptions.ClientId,
                            HasClientSecret = !string.IsNullOrWhiteSpace(miOptions.ClientSecret),
                        },
                        OpenIdConnectOptions = new
                        {
                            oidcOptions.Authority,
                            oidcOptions.MetadataAddress,
                            oidcOptions.CallbackPath,
                            oidcOptions.SignedOutCallbackPath,
                            Scopes = oidcOptions.Scope,
                            oidcOptions.ResponseType,
                        },
                        AuthenticationSchemes = schemes.Select(s => new { s.Name, s.DisplayName, s.HandlerType?.FullName }).ToArray(),
                    };

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    })).ConfigureAwait(false);
                })
                .AllowAnonymous();
            }

            app.UseHttpsRedirection();
            app.UseAntiforgery();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // Sign-in endpoint (challenge).
            app.MapGet("/signin", async context =>
            {
                if (!(context.User?.Identity?.IsAuthenticated ?? false))
                {
                    await context.ChallengeAsync("Smart", new AuthenticationProperties { RedirectUri = "/" });
                }
                else
                {
                    context.Response.Redirect("/");
                }
            });

            // Sign-out (invalidate cookie + OIDC sign-out if OIDC session).
            app.MapGet("/signout", async context =>
            {
                if (context.User?.Identity?.IsAuthenticated ?? false)
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    try
                    {
                        await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" });
                    }
                    catch
                    {
                    }
                }
                else
                {
                    context.Response.Redirect("/");
                }
            });

            if (app.Environment.IsStaging())
            {
                app.MapGet("/debug/headers", async context =>
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in context.Request.Headers)
                    {
                        if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
                        {
                            headers[header.Key] = "***redacted***";
                            continue;
                        }

                        headers[header.Key] = header.Value.ToString();
                    }

                    var payload = new
                    {
                        Request = new
                        {
                            Scheme = context.Request.Scheme,
                            Host = context.Request.Host.Value,
                            PathBase = context.Request.PathBase.Value,
                            Path = context.Request.Path.Value,
                            QueryString = context.Request.QueryString.Value,
                            Protocol = context.Request.Protocol,
                            RemoteIpAddress = context.Connection.RemoteIpAddress?.ToString(),
                        },
                        Headers = headers,
                    };

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }));
                })
                .AllowAnonymous();
            }

            app.Run();
        }

        private static void ConfigureOpenTelemetry(WebApplicationBuilder builder)
        {
            var resource = ResourceBuilder.CreateDefault()
                .AddService(
                    serviceName: "MailFunk",
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName ?? "unknown"),
                });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(rb => rb.AddAttributes(resource.Build().Attributes))
                .WithTracing(tb => tb
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter())
                .WithMetrics(mb => mb
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter());
        }

        private static HttpMessageHandler CreateGrpcHttpHandler(WebApplicationBuilder builder, X509Certificate2? adminClientCert)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = 20,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };

            if (builder.Environment.IsDevelopment())
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
            }

            if (adminClientCert != null)
            {
                if (!adminClientCert.HasPrivateKey)
                {
                    Console.WriteLine("[MailFunk] WARNING: Admin client certificate loaded but has no private key; it will not be presented for mTLS.");
                }

                handler.SslOptions ??= new SslClientAuthenticationOptions();
                handler.SslOptions.ClientCertificates ??= new X509CertificateCollection();
                handler.SslOptions.ClientCertificates.Add(adminClientCert);
            }

            return handler;
        }

        private static X509Certificate2? LoadAdminClientCert(IConfiguration config)
        {
            var thumbprint = config["AdminClientCert:Thumbprint"];
            if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                try
                {
                    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                    if (found.Count > 0)
                    {
                        return found[0];
                    }
                }
                catch
                {
                    // Fallback to PFX-based loading.
                }
            }

            var pfxPath = config["AdminClientCert:Path"];
            if (!string.IsNullOrWhiteSpace(pfxPath) && File.Exists(pfxPath))
            {
                var passwordEnvVar = config["AdminClientCert:PasswordEnvironmentVariable"] ?? string.Empty;
                var password = config["AdminClientCert:Password"] ?? Environment.GetEnvironmentVariable(passwordEnvVar);

                try
                {
                    return X509CertificateLoader.LoadPkcs12FromFile(
                        pfxPath,
                        password,
                        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                }
                catch
                {
                    // Ignore and continue without an admin certificate.
                }
            }

            return null;
        }
    }
}
