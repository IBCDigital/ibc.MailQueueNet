// <copyright file="UserProfileService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System;
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Identity.Web;

    internal sealed class UserProfileService : IUserProfileService
    {
        private readonly IHttpContextAccessor accessor;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly ITokenAcquisition tokenAcquisition;

        public UserProfileService(IHttpContextAccessor accessor, IHttpClientFactory httpClientFactory, ITokenAcquisition tokenAcquisition)
        {
            this.accessor = accessor;
            this.httpClientFactory = httpClientFactory;
            this.tokenAcquisition = tokenAcquisition;
        }

        public async Task<UserProfile> GetAsync()
        {
            var ctx = this.accessor.HttpContext;
            if (ctx == null || !(ctx.User?.Identity?.IsAuthenticated ?? false))
            {
                return new UserProfile(null, null, null, null);
            }

            var displayName = ctx.User.FindFirst("name")?.Value ?? ctx.User.Identity?.Name;
            var email = ctx.User.FindFirst(ClaimTypes.Email)?.Value ?? ctx.User.FindFirst("preferred_username")?.Value;
            var initials = GetInitials(displayName ?? email);

            string? photoB64 = null;
            try
            {
                var token = await this.tokenAcquisition.GetAccessTokenForUserAsync(new[] { "User.Read" }).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    var client = this.httpClientFactory.CreateClient("Graph");
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var resp = await client.GetAsync("me/photos/48x48/$value").ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        photoB64 = "data:image/png;base64," + Convert.ToBase64String(bytes);
                    }
                }
            }
            catch
            {
                // ignore graph failures
            }

            return new UserProfile(displayName, email, initials, photoB64);
        }

        private static string? GetInitials(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            }

            return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
        }
    }
}
