//-----------------------------------------------------------------------
// <copyright file="MailGrpcServiceClientTests.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Tests
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Grpc;
    using Microsoft.Extensions.Logging;
    using Moq;
    using Xunit;

    /// <summary>
    /// Unit tests covering the retry wrapper around <see cref="MailGrpcService.MailGrpcServiceClient"/>.
    /// </summary>
    public class MailGrpcServiceClientTests
    {
        private static AsyncUnaryCall<MailMessageReply> UnaryOk(MailMessageReply reply)
        {
            return new AsyncUnaryCall<MailMessageReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private static MailClientConfiguration CreateFastDiskResilienceConfig(string folder)
        {
            return new MailClientConfiguration
            {
                EnableDiskResilience = true,
                UndeliveredFolder = folder,
                RetryCount = 0,
                UnsentCheckIntervalMinutes = 60,
            };
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!predicate())
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("Condition was not met before the timeout elapsed.");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private static AsyncUnaryCall<ListAllowedTestRecipientsReply> ListAllowedTestRecipientsError(StatusCode statusCode, string detail)
        {
            return new AsyncUnaryCall<ListAllowedTestRecipientsReply>(
                Task.FromException<ListAllowedTestRecipientsReply>(new RpcException(new Status(statusCode, detail))),
                Task.FromResult(new Metadata()),
                () => new Status(statusCode, detail),
                () => new Metadata(),
                () => { });
        }

        private static AsyncUnaryCall<ListAllowedTestRecipientsReply> ListAllowedTestRecipientsOk(ListAllowedTestRecipientsReply reply)
        {
            return new AsyncUnaryCall<ListAllowedTestRecipientsReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private static AsyncUnaryCall<AddAllowedTestRecipientReply> AddAllowedTestRecipientOk(AddAllowedTestRecipientReply reply)
        {
            return new AsyncUnaryCall<AddAllowedTestRecipientReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private static AsyncUnaryCall<DeleteAllowedTestRecipientReply> DeleteAllowedTestRecipientOk(DeleteAllowedTestRecipientReply reply)
        {
            return new AsyncUnaryCall<DeleteAllowedTestRecipientReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        [Fact]
        public async Task QueueMailWithRetry_ShouldRetryOnTransientFailure()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Strict);

            mockClient
                .SetupSequence(client => client.QueueMailReplyAsync(
                    It.IsAny<System.Net.Mail.MailMessage>(),
                    It.IsAny<Metadata?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable")))
                .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable")))
                .ReturnsAsync(new MailMessageReply { Success = true });

            var logger = Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>();
            var retryClient = new MailGrpcServiceClientWithRetry(mockClient.Object, new MailClientConfiguration(), logger);
            var result = await retryClient.QueueMailWithRetryAsync(new System.Net.Mail.MailMessage());

            Assert.True(result.Success);
            mockClient.Verify(client => client.QueueMailReplyAsync(
                It.IsAny<System.Net.Mail.MailMessage>(),
                It.IsAny<Metadata?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task QueueMailWithRetryAndResilience_ShouldPersistUndeliveredMail()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable")));

                var logger = Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>();
                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    logger);

                using (var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body"))
                {
                    try
                    {
                        await retryClient.QueueMailWithRetryAndResilienceAsync(message);
                    }
                    catch
                    {
                        // The mock client is not configured to succeed; the wrapper should persist to disk.
                    }
                }

                var files = Directory.GetFiles(folder, "*.mail");
                Assert.Single(files);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task FlushInFlightToUndeliveredFolderAsync_ShouldPersistInFlightMessage()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var releaseCall = new TaskCompletionSource<MailMessageReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Returns(() => releaseCall.Task);

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var queueTask = retryClient.QueueMailWithRetryAndResilienceAsync(message);

                await WaitUntilAsync(() => Directory.GetFiles(folder, "*.mail").Length == 0).ConfigureAwait(false);
                await retryClient.FlushInFlightToUndeliveredFolderAsync().ConfigureAwait(false);

                var files = Directory.GetFiles(folder, "*.mail");
                Assert.Single(files);

                releaseCall.SetResult(new MailMessageReply { Success = true });
                await queueTask.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task FlushInFlightToUndeliveredFolderAsync_ShouldNotPersistSuccessfulMessage()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new MailMessageReply { Success = true });

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                await retryClient.QueueMailWithRetryAndResilienceAsync(message).ConfigureAwait(false);
                await retryClient.FlushInFlightToUndeliveredFolderAsync().ConfigureAwait(false);

                Assert.Empty(Directory.GetFiles(folder, "*.mail"));
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task FlushInFlightToUndeliveredFolderAsync_ShouldNotCreateDuplicateFile()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var releaseCall = new TaskCompletionSource<MailMessageReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Returns(() => releaseCall.Task);

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var queueTask = retryClient.QueueMailWithRetryAndResilienceAsync(message);

                await retryClient.FlushInFlightToUndeliveredFolderAsync().ConfigureAwait(false);
                await retryClient.FlushInFlightToUndeliveredFolderAsync().ConfigureAwait(false);

                Assert.Single(Directory.GetFiles(folder, "*.mail"));

                releaseCall.SetResult(new MailMessageReply { Success = true });
                await queueTask.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task StopAsync_ShouldDisposeTimerAndRejectNewQueueAttempts()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                await retryClient.StopAsync().ConfigureAwait(false);

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                await Assert.ThrowsAsync<ObjectDisposedException>(() => retryClient.QueueMailWithRetryAndResilienceAsync(message)).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task ListAllowedTestRecipientEmailAddressesAsync_ShouldReturnEmailAddresses()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>
            {
                CallBase = true,
            };

            var reply = new ListAllowedTestRecipientsReply();
            reply.EmailAddresses.Add("test@example.com");

            mockClient
                .Setup(client => client.ListAllowedTestRecipientsAsync(
                    It.Is<ListAllowedTestRecipientsRequest>(request => string.IsNullOrEmpty(request.ClientId)),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailGrpcServiceClientTests.ListAllowedTestRecipientsOk(reply));

            var result = await mockClient.Object.ListAllowedTestRecipientEmailAddressesAsync();

            Assert.Single(result, "test@example.com");
            mockClient.VerifyAll();
        }

        [Fact]
        public async Task ListAllowedTestRecipientEmailAddressesAsync_ShouldSurfaceStagingOnlyEndpointError()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.ListAllowedTestRecipientsAsync(
                    It.Is<ListAllowedTestRecipientsRequest>(request => string.IsNullOrEmpty(request.ClientId)),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailGrpcServiceClientTests.ListAllowedTestRecipientsError(StatusCode.NotFound, "Staging-only endpoint"));

            var exception = await Assert.ThrowsAsync<RpcException>(
                () => mockClient.Object.ListAllowedTestRecipientEmailAddressesAsync());

            Assert.Equal(StatusCode.NotFound, exception.StatusCode);
            Assert.Equal("Staging-only endpoint", exception.Status.Detail);
            mockClient.VerifyAll();
        }

        [Fact]
        public async Task ListAllowedTestRecipientEmailAddressesAsync_ShouldSendClientAuthenticationHeaders()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>
            {
                CallBase = true,
            };

            MailGrpcService.MailGrpcServiceClient.ConfigureClientAuth("test-client", "test-secret");

            mockClient
                .Setup(client => client.ListAllowedTestRecipientsAsync(
                    It.Is<ListAllowedTestRecipientsRequest>(request => string.IsNullOrEmpty(request.ClientId)),
                    It.Is<Metadata>(metadata =>
                        metadata.GetValue("x-client-id") == "test-client" &&
                        !string.IsNullOrWhiteSpace(metadata.GetValue("x-client-pass"))),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailGrpcServiceClientTests.ListAllowedTestRecipientsOk(new ListAllowedTestRecipientsReply()));

            var result = await mockClient.Object.ListAllowedTestRecipientEmailAddressesAsync();

            Assert.Empty(result);
            mockClient.VerifyAll();
        }

        [Fact]
        public async Task AddAllowedTestRecipientEmailAddressAsync_ShouldSendEmailAddress()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.AddAllowedTestRecipientAsync(
                    It.Is<AddAllowedTestRecipientRequest>(request => request.EmailAddress == "test@example.com" && string.IsNullOrEmpty(request.ClientId)),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailGrpcServiceClientTests.AddAllowedTestRecipientOk(new AddAllowedTestRecipientReply { Success = true }));

            var result = await mockClient.Object.AddAllowedTestRecipientEmailAddressAsync("test@example.com");

            Assert.True(result.Success);
            mockClient.VerifyAll();
        }

        [Fact]
        public async Task RemoveAllowedTestRecipientEmailAddressAsync_ShouldSendEmailAddress()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.DeleteAllowedTestRecipientAsync(
                    It.Is<DeleteAllowedTestRecipientRequest>(request => request.EmailAddress == "test@example.com" && string.IsNullOrEmpty(request.ClientId)),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailGrpcServiceClientTests.DeleteAllowedTestRecipientOk(new DeleteAllowedTestRecipientReply { Success = true }));

            var result = await mockClient.Object.RemoveAllowedTestRecipientEmailAddressAsync("test@example.com");

            Assert.True(result.Success);
            mockClient.VerifyAll();
        }
    }
}