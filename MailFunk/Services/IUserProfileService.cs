// <copyright file="IUserProfileService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System.Threading.Tasks;

    public interface IUserProfileService
    {
        Task<UserProfile> GetAsync();
    }

    public sealed record UserProfile(string? DisplayName, string? Email, string? Initials, string? PhotoBase64);
}
