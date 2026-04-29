//-----------------------------------------------------------------------
// <copyright file="ClientGenerator.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Components;
    using Microsoft.JSInterop;

    /// <summary>
    /// Generates a client password hash for MailQueueNet clients from a Client ID and shared secret.
    /// </summary>
    public sealed partial class ClientGenerator : ComponentBase
    {
        private string clientId = string.Empty;
        private string sharedSecret = string.Empty;
        private string clientPass = string.Empty;
        private bool showSecret;
        private bool showPass;

        [Inject]
        public IJSRuntime Js { get; set; } = default!;

        /// <summary>
        /// Computes the pass value using SHA-256 over clientId:sharedSecret, Base64-encoded.
        /// </summary>
        /// <param name="id">The client identifier.</param>
        /// <param name="secret">The shared secret.</param>
        /// <returns>The computed pass string.</returns>
        private static string ComputePass(string id, string secret)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(id + ":" + secret);
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        /// <summary>
        /// Generates the pass value into the component state.
        /// </summary>
        /// <returns>A completed task.</returns>
        private Task GenerateAsync()
        {
            if (!string.IsNullOrWhiteSpace(this.clientId) && !string.IsNullOrWhiteSpace(this.sharedSecret))
            {
                this.clientPass = ComputePass(this.clientId, this.sharedSecret);
            }
            else
            {
                this.clientPass = string.Empty;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Clears all fields.
        /// </summary>
        private void Clear()
        {
            this.clientId = string.Empty;
            this.sharedSecret = string.Empty;
            this.clientPass = string.Empty;
            this.showSecret = false;
            this.showPass = false;
        }

        /// <summary>
        /// Toggles showing the secret in the UI.
        /// </summary>
        private void ToggleSecretVisibility()
        {
            this.showSecret = !this.showSecret;
        }

        /// <summary>
        /// Toggles showing the generated pass in the UI.
        /// </summary>
        private void TogglePassVisibility()
        {
            this.showPass = !this.showPass;
        }

        /// <summary>
        /// Copies the generated pass to the clipboard.
        /// </summary>
        /// <returns>A task that represents the asynchronous copy operation.</returns>
        private async Task CopyPassAsync()
        {
            if (!string.IsNullOrWhiteSpace(this.clientPass))
            {
                await this.Js.InvokeVoidAsync("navigator.clipboard.writeText", this.clientPass);
            }
        }

        /// <summary>
        /// Copies sample headers to the clipboard for convenience.
        /// </summary>
        /// <returns>A task that represents the asynchronous copy operation.</returns>
        private async Task CopyHeadersAsync()
        {
            if (!string.IsNullOrWhiteSpace(this.clientId) && !string.IsNullOrWhiteSpace(this.clientPass))
            {
                var sample = $"x-client-id: {this.clientId}\nx-client-pass: {this.clientPass}";
                await this.Js.InvokeVoidAsync("navigator.clipboard.writeText", sample);
            }
        }
    }
}
