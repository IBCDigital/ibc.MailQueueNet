// <copyright file="InFlightMailMessage.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Grpc
{
    using System;

    /// <summary>
    /// Tracks a mail message that is currently being submitted through the resilient client.
    /// </summary>
    internal sealed class InFlightMailMessage
    {
        private bool completed;
        private bool persisted;

        /// <summary>
        /// Initialises a new instance of the <see cref="InFlightMailMessage"/> class.
        /// </summary>
        /// <param name="id">Unique tracking identifier for this in-flight message.</param>
        /// <param name="message">The mail message being submitted.</param>
        public InFlightMailMessage(Guid id, System.Net.Mail.MailMessage message)
        {
            this.Id = id;
            this.Message = message;
        }

        /// <summary>
        /// Gets the unique tracking identifier for this in-flight message.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Gets the synchronisation object used when updating this message state.
        /// </summary>
        public object SyncRoot { get; } = new object();

        /// <summary>
        /// Gets the mail message currently being submitted.
        /// </summary>
        public System.Net.Mail.MailMessage Message { get; }

        /// <summary>
        /// Gets a value indicating whether the mail was successfully submitted.
        /// </summary>
        public bool IsCompleted => this.completed;

        /// <summary>
        /// Gets a value indicating whether the mail has already been persisted to disk.
        /// </summary>
        public bool IsPersisted => this.persisted;

        /// <summary>
        /// Marks the message as successfully submitted.
        /// </summary>
        public void MarkCompleted()
        {
            this.completed = true;
        }

        /// <summary>
        /// Marks the message as persisted to the undelivered folder.
        /// </summary>
        public void MarkPersisted()
        {
            this.persisted = true;
        }
    }
}
