// <copyright file="Debouncer.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Internal
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class Debouncer : IDisposable
    {
        private readonly TimeSpan delay;
        private CancellationTokenSource previousCancellationToken = null;

        public Debouncer(TimeSpan? delay) => this.delay = delay ?? TimeSpan.FromSeconds(2);

        public async Task Debounce(Action action)
        {
            _ = action ?? throw new ArgumentNullException(nameof(action));
            this.Cancel();
            this.previousCancellationToken = new CancellationTokenSource();
            try
            {
                await Task.Delay(this.delay, this.previousCancellationToken.Token);
                await Task.Run(action, this.previousCancellationToken.Token);
            }

            // can swallow exception as nothing more to do if task cancelled
            catch (TaskCanceledException)
            {
            }
        }

        public void Cancel()
        {
            if (this.previousCancellationToken != null)
            {
                this.previousCancellationToken.Cancel();
                this.previousCancellationToken.Dispose();
                this.previousCancellationToken = null;
            }
        }

        public void Dispose() => this.Cancel();
    }
}
