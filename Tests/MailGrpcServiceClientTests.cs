//-----------------------------------------------------------------------
// <copyright file="MailGrpcServiceClientTests.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Common.FileExtensions;
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
        public async Task QueueMailWithRetryAndResilience_ShouldCreateUniqueFilesForConcurrentWriters()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                const int writerCount = 25;
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable")));

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                var queueTasks = Enumerable.Range(0, writerCount)
                    .Select(async index =>
                    {
                        using var message = new System.Net.Mail.MailMessage(
                            "from@test.com",
                            $"to{index}@test.com",
                            "Subject",
                            "Body");

                        try
                        {
                            await retryClient.QueueMailWithRetryAndResilienceAsync(message).ConfigureAwait(false);
                        }
                        catch (RpcException)
                        {
                            // The mock client is not configured to succeed; the wrapper should persist to disk.
                        }
                    })
                    .ToArray();

                await Task.WhenAll(queueTasks).ConfigureAwait(false);

                var files = Directory.GetFiles(folder, "*.mail");
                var fileNames = files.Select(Path.GetFileName).ToArray();

                Assert.Equal(writerCount, files.Length);
                Assert.Equal(writerCount, fileNames.Distinct(StringComparer.Ordinal).Count());
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
        public async Task QueueMailWithRetryAndResilience_ShouldNotExposeTemporaryFilesToMailScanner()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            using var allowPublish = new ManualResetEventSlim(false);

            try
            {
                var tempReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailReplyAsync(
                        It.IsAny<System.Net.Mail.MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service unavailable")));

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());

                RetryMailFileStore.BeforePublishForTests = (temporaryPath, _) =>
                {
                    tempReady.TrySetResult(temporaryPath);
                    allowPublish.Wait(TimeSpan.FromSeconds(5));
                };

                var queueTask = Task.Run(async () =>
                {
                    using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");

                    try
                    {
                        await retryClient.QueueMailWithRetryAndResilienceAsync(message).ConfigureAwait(false);
                    }
                    catch (RpcException)
                    {
                        // The mock client is not configured to succeed; the wrapper should persist to disk.
                    }
                });

                var temporaryPath = await tempReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                Assert.True(File.Exists(temporaryPath));
                Assert.Empty(Directory.GetFiles(folder, "*.mail"));
                Assert.Single(Directory.GetFiles(folder, "*.mail.writing-*.tmp"));

                allowPublish.Set();
                await queueTask.ConfigureAwait(false);

                Assert.Single(Directory.GetFiles(folder, "*.mail"));
                Assert.Empty(Directory.GetFiles(folder, "*.mail.writing-*.tmp"));
            }
            finally
            {
                RetryMailFileStore.BeforePublishForTests = null;
                allowPublish.Set();

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
        public async Task RetryMailFileStore_ShouldPublishFinalMailOnlyAfterTempFileIsClosedAndMoved()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            using var allowPublish = new ManualResetEventSlim(false);

            try
            {
                var pathsReady = new TaskCompletionSource<(string TemporaryPath, string FinalPath)>(TaskCreationOptions.RunContinuationsAsynchronously);

                RetryMailFileStore.BeforePublishForTests = (temporaryPath, finalPath) =>
                {
                    pathsReady.TrySetResult((temporaryPath, finalPath));
                    allowPublish.Wait(TimeSpan.FromSeconds(5));
                };

                using var mailMessage = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var persistedMessage = MailMessage.FromMessage(mailMessage);
                var writeTask = Task.Run(() => RetryMailFileStore.WriteMailToUndeliveredFolder(persistedMessage, folder));

                var paths = await pathsReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                Assert.True(File.Exists(paths.TemporaryPath));
                Assert.False(File.Exists(paths.FinalPath));
                Assert.Empty(Directory.GetFiles(folder, "*.mail"));

                using (var stream = new FileStream(paths.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.True(stream.Length > 0);
                }

                allowPublish.Set();
                var finalPath = await writeTask.ConfigureAwait(false);

                Assert.Equal(paths.FinalPath, finalPath);
                Assert.False(File.Exists(paths.TemporaryPath));
                Assert.True(File.Exists(finalPath));
                Assert.Single(Directory.GetFiles(folder, "*.mail"));

                var readBack = FileUtils.ReadMailNoSettingsFromFile(finalPath);
                Assert.Equal(persistedMessage.Subject, readBack.Subject);
                Assert.Equal(persistedMessage.Body, readBack.Body);
            }
            finally
            {
                RetryMailFileStore.BeforePublishForTests = null;
                RetryMailFileStore.PreserveTemporaryFileOnFailureForTests = false;
                allowPublish.Set();

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
        public void RetryMailFileStore_ShouldLeaveNoFinalMailFileWhenPublicationFails()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                RetryMailFileStore.BeforePublishForTests = (_, _) => throw new IOException("Simulated publication failure.");

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");

                Assert.Throws<IOException>(() => RetryMailFileStore.WriteMailToUndeliveredFolder(
                    MailMessage.FromMessage(message),
                    folder));

                Assert.Empty(Directory.GetFiles(folder, "*.mail"));
                Assert.Empty(Directory.GetFiles(folder, "*.mail.writing-*.tmp"));
            }
            finally
            {
                RetryMailFileStore.BeforePublishForTests = null;
                RetryMailFileStore.PreserveTemporaryFileOnFailureForTests = false;

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
        public void RetryMailFileStore_ShouldLeaveOnlyIgnoredTemporaryFileWhenWriterDiesAfterTempWrite()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                RetryMailFileStore.PreserveTemporaryFileOnFailureForTests = true;
                RetryMailFileStore.BeforePublishForTests = (_, _) => throw new InvalidOperationException("Simulated writer termination.");

                using var message = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");

                Assert.Throws<InvalidOperationException>(() => RetryMailFileStore.WriteMailToUndeliveredFolder(
                    MailMessage.FromMessage(message),
                    folder));

                Assert.Empty(Directory.GetFiles(folder, "*.mail"));
                Assert.Single(Directory.GetFiles(folder, "*.mail.writing-*.tmp"));
            }
            finally
            {
                RetryMailFileStore.BeforePublishForTests = null;
                RetryMailFileStore.PreserveTemporaryFileOnFailureForTests = false;

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
        public async Task ProcessUndeliveredAsync_ShouldResendFinalMailAndIgnoreTemporaryFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                using var sourceMessage = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var persistedMessage = MailMessage.FromMessage(sourceMessage);
                var finalPath = RetryMailFileStore.WriteMailToUndeliveredFolder(persistedMessage, folder);
                var temporaryPath = Path.Combine(folder, Path.GetFileName(finalPath) + ".writing-test-node-" + Guid.NewGuid().ToString("N") + ".tmp");
                FileUtils.WriteMailToFile(persistedMessage, temporaryPath);

                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailMessageReplyAsync(
                        It.Is<MailMessage>(mail => mail.Subject == persistedMessage.Subject && mail.Body == persistedMessage.Body),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new MailMessageReply { Success = true });

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());
                retryClient.PauseResendTimerForTests();

                await retryClient.ProcessUndeliveredForTestsAsync().ConfigureAwait(false);

                Assert.False(File.Exists(finalPath));
                Assert.True(File.Exists(temporaryPath));
                Assert.Empty(Directory.GetFiles(folder, "*.mail"));
                Assert.Single(Directory.GetFiles(folder, "*.mail.writing-*.tmp"));

                mockClient.Verify(client => client.QueueMailMessageReplyAsync(
                    It.IsAny<MailMessage>(),
                    It.IsAny<Metadata?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task ProcessUndeliveredAsync_ShouldAllowOnlyOneClientInSharedResendLoop()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFirstSend = new TaskCompletionSource<MailMessageReply>(TaskCreationOptions.RunContinuationsAsynchronously);

                var firstMockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                firstMockClient
                    .Setup(client => client.QueueMailMessageReplyAsync(
                        It.IsAny<MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        firstSendStarted.TrySetResult();
                        return releaseFirstSend.Task;
                    });

                var secondMockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Strict);

                var firstRetryClient = new MailGrpcServiceClientWithRetry(
                    firstMockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());
                firstRetryClient.PauseResendTimerForTests();

                var secondRetryClient = new MailGrpcServiceClientWithRetry(
                    secondMockClient.Object,
                    MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder),
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());
                secondRetryClient.PauseResendTimerForTests();

                using var sourceMessage = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var finalPath = RetryMailFileStore.WriteMailToUndeliveredFolder(MailMessage.FromMessage(sourceMessage), folder);

                var firstProcessTask = firstRetryClient.ProcessUndeliveredForTestsAsync();
                await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                await secondRetryClient.ProcessUndeliveredForTestsAsync().ConfigureAwait(false);

                Assert.True(File.Exists(finalPath));
                secondMockClient.Verify(client => client.QueueMailMessageReplyAsync(
                    It.IsAny<MailMessage>(),
                    It.IsAny<Metadata?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()), Times.Never);

                releaseFirstSend.SetResult(new MailMessageReply { Success = true });
                await firstProcessTask.ConfigureAwait(false);

                Assert.False(File.Exists(finalPath));
                firstMockClient.Verify(client => client.QueueMailMessageReplyAsync(
                    It.IsAny<MailMessage>(),
                    It.IsAny<Metadata?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task ProcessUndeliveredAsync_ShouldSkipWithoutModifyingMailWhenSharedLockIsHeld()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                using var heldLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "test-owner");

                var heldResult = heldLock.TryAcquire();
                Assert.True(heldResult.Acquired);

                using var sourceMessage = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var finalPath = RetryMailFileStore.WriteMailToUndeliveredFolder(MailMessage.FromMessage(sourceMessage), folder);
                var originalLength = new FileInfo(finalPath).Length;

                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Strict);
                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    config,
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());
                retryClient.PauseResendTimerForTests();

                await retryClient.ProcessUndeliveredForTestsAsync().ConfigureAwait(false);

                Assert.True(File.Exists(finalPath));
                Assert.Equal(originalLength, new FileInfo(finalPath).Length);
                mockClient.Verify(client => client.QueueMailMessageReplyAsync(
                    It.IsAny<MailMessage>(),
                    It.IsAny<Metadata?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()), Times.Never);
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
        public async Task ProcessUndeliveredAsync_ShouldReleaseSharedLockAfterResendException()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                using var sourceMessage = new System.Net.Mail.MailMessage("from@test.com", "to@test.com", "Subject", "Body");
                var finalPath = RetryMailFileStore.WriteMailToUndeliveredFolder(MailMessage.FromMessage(sourceMessage), folder);

                var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>();
                mockClient
                    .Setup(client => client.QueueMailMessageReplyAsync(
                        It.IsAny<MailMessage>(),
                        It.IsAny<Metadata?>(),
                        It.IsAny<DateTime?>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("Simulated resend failure."));

                var retryClient = new MailGrpcServiceClientWithRetry(
                    mockClient.Object,
                    config,
                    Mock.Of<ILogger<MailGrpcServiceClientWithRetry>>());
                retryClient.PauseResendTimerForTests();

                await retryClient.ProcessUndeliveredForTestsAsync().ConfigureAwait(false);

                Assert.True(File.Exists(finalPath));

                using var recoveredLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "recovered-owner");

                var acquireResult = recoveredLock.TryAcquire();
                Assert.True(acquireResult.Acquired);
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
        public void FileResendLock_ShouldAllowRecoveryAfterOwnerHandleIsReleased()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                var ownerLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "first-owner");

                var ownerResult = ownerLock.TryAcquire();
                Assert.True(ownerResult.Acquired);

                using (var blockedLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "blocked-owner"))
                {
                    var blockedResult = blockedLock.TryAcquire();
                    Assert.False(blockedResult.Acquired);
                }

                ownerLock.Dispose();

                using var recoveredLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "recovered-owner");

                var recoveredResult = recoveredLock.TryAcquire();
                Assert.True(recoveredResult.Acquired);
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
        public void FileResendLock_ShouldRecoverStaleMetadataOnlyWhenExclusiveAccessIsAvailable()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                config.DistributedLockTimeoutSeconds = 1;
                var lockPath = Path.Combine(folder, config.LockFileName);
                var staleMetadata = FileResendLockMetadata.Create("stale-owner", TimeSpan.FromSeconds(1));
                staleMetadata.CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
                staleMetadata.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-9);
                File.WriteAllText(lockPath, staleMetadata.ToFileText());

                using var recoveredLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "recovered-owner");

                var recoveredResult = recoveredLock.TryAcquire();
                Assert.True(recoveredResult.Acquired);
                Assert.Equal("recovered-owner", recoveredResult.Metadata?.Owner);
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
        public void FileResendLock_ShouldReportStaleButUnsafeWhenExclusiveAccessIsUnavailable()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                config.DistributedLockTimeoutSeconds = 1;
                var lockPath = Path.Combine(folder, config.LockFileName);
                var staleMetadata = FileResendLockMetadata.Create("stale-owner", TimeSpan.FromSeconds(1));
                staleMetadata.CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
                staleMetadata.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-9);
                File.WriteAllText(lockPath, staleMetadata.ToFileText());

                using var heldHandle = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                using var blockedLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "blocked-owner");

                var blockedResult = blockedLock.TryAcquire();
                Assert.False(blockedResult.Acquired);
                Assert.Contains(blockedResult.State, new[] { "stale", "unknown" });
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
        public void FileResendLock_ShouldReportRecentMetadataAsActiveWhenExclusiveAccessFails()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                using var ownerLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "active-owner");

                var ownerResult = ownerLock.TryAcquire();
                Assert.True(ownerResult.Acquired);

                using var blockedLock = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "blocked-owner");

                var blockedResult = blockedLock.TryAcquire();
                Assert.False(blockedResult.Acquired);
                Assert.NotEqual("stale", blockedResult.State);
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
        public void FileResendLock_ShouldWriteOnlySafeMetadataFields()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                var lockPath = Path.Combine(folder, config.LockFileName);

                using (var lockHandle = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "safe-client"))
                {
                    var acquireResult = lockHandle.TryAcquire();
                    Assert.True(acquireResult.Acquired);
                }

                var metadataText = File.ReadAllText(lockPath);
                var approvedKeys = new[] { "owner", "processId", "createdUtc", "expiresUtc", "package", "state", "releasedUtc" };
                var keys = metadataText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('=')[0])
                    .ToArray();

                Assert.All(keys, key => Assert.Contains(key, approvedKeys));
                Assert.Contains("owner=safe-client", metadataText);
                Assert.Contains("package=MailQueueNet", metadataText);
                Assert.DoesNotContain("recipient@test.com", metadataText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Secret subject", metadataText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Secret body", metadataText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("smtp-password", metadataText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("shared-secret", metadataText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("attachment.pdf", metadataText, StringComparison.OrdinalIgnoreCase);
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
        public void FileResendLock_ShouldHonorConfiguredLockNameAndTimeout()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MailQueueNet.Tests.Undelivered", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var defaultConfig = new MailClientConfiguration();
                Assert.Equal(".resend.lock", defaultConfig.LockFileName);

                var config = MailGrpcServiceClientTests.CreateFastDiskResilienceConfig(folder);
                config.LockFileName = ".custom-resend.lock";
                config.DistributedLockTimeoutSeconds = 7;

                using var lockHandle = new FileResendLock(
                    folder,
                    config.LockFileName,
                    TimeSpan.FromSeconds(config.DistributedLockTimeoutSeconds),
                    "timeout-owner");

                var acquireResult = lockHandle.TryAcquire();
                Assert.True(acquireResult.Acquired);

                var defaultLockPath = Path.Combine(folder, ".resend.lock");
                var customLockPath = Path.Combine(folder, config.LockFileName);
                Assert.False(File.Exists(defaultLockPath));
                Assert.True(File.Exists(customLockPath));

                var metadata = acquireResult.Metadata;
                Assert.NotNull(metadata);
                var lockDuration = metadata!.ExpiresUtc - metadata.CreatedUtc;
                Assert.InRange(lockDuration.TotalSeconds, 6.5, 7.5);
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