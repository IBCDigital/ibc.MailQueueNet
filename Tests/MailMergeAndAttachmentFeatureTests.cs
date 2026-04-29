//-----------------------------------------------------------------------
// <copyright file="MailMergeAndAttachmentFeatureTests.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Tests
{
    using System;
    using System.IO;
    using System.Net.Mail;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Grpc;
    using Moq;
    using Xunit;

    /// <summary>
    /// Unit tests for merge + attachment client ergonomics.
    /// These tests use a mocked gRPC client so they can run in Test Explorer
    /// without a live queue service.
    /// </summary>
    public class MailMergeAndAttachmentFeatureTests
    {
        private static AsyncUnaryCall<QueueMailMergeReply> UnaryMergeReply(QueueMailMergeReply reply)
        {
            return new AsyncUnaryCall<QueueMailMergeReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private static AsyncUnaryCall<GetAttachmentInfoReply> UnaryAttachmentInfo(GetAttachmentInfoReply reply)
        {
            return new AsyncUnaryCall<GetAttachmentInfoReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private static AsyncUnaryCall<DeleteAttachmentReply> UnaryDelete(DeleteAttachmentReply reply)
        {
            return new AsyncUnaryCall<DeleteAttachmentReply>(
                Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        [Fact]
        public async Task GetAttachmentInfoAdminAsync_ShouldAddReplayProtectionHeaders()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.GetAttachmentInfoAsync(
                    It.IsAny<GetAttachmentInfoRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryAttachmentInfo(new GetAttachmentInfoReply { Exists = true, Ready = true, RefCount = 0 }));

            var reply = await mockClient.Object.GetAttachmentInfoAdminAsync(new GetAttachmentInfoRequest { Token = "tok1" }).ResponseAsync;

            Assert.True(reply.Exists);

            mockClient.Verify(client => client.GetAttachmentInfoAsync(
                It.Is<GetAttachmentInfoRequest>(r => r.Token == "tok1"),
                It.Is<Metadata>(md => md != null && md.GetValue("x-ts") != null && md.GetValue("x-nonce") != null),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteAttachmentAdminAsync_ShouldAddReplayProtectionHeaders()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.DeleteAttachmentAsync(
                    It.IsAny<DeleteAttachmentRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryDelete(new DeleteAttachmentReply { Success = true }));

            var reply = await mockClient.Object.DeleteAttachmentAdminAsync(new DeleteAttachmentRequest { Token = "tok1", Force = false }).ResponseAsync;

            Assert.True(reply.Success);

            mockClient.Verify(client => client.DeleteAttachmentAsync(
                It.Is<DeleteAttachmentRequest>(r => r.Token == "tok1" && r.Force == false),
                It.Is<Metadata>(md => md != null && md.GetValue("x-ts") != null && md.GetValue("x-nonce") != null),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task WaitForAttachmentReadyAdminAsync_ShouldPollUntilReady()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .SetupSequence(client => client.GetAttachmentInfoAsync(
                    It.IsAny<GetAttachmentInfoRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryAttachmentInfo(new GetAttachmentInfoReply { Exists = true, Ready = false, RefCount = 0 }))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryAttachmentInfo(new GetAttachmentInfoReply { Exists = true, Ready = true, RefCount = 0 }));

            var reply = await mockClient.Object.WaitForAttachmentReadyAdminAsync(
                token: "tok1",
                timeout: TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(1));

            Assert.True(reply.Exists);
            Assert.True(reply.Ready);

            mockClient.Verify(client => client.GetAttachmentInfoAsync(
                It.Is<GetAttachmentInfoRequest>(r => r.Token == "tok1"),
                It.Is<Metadata>(md => md != null && md.GetValue("x-ts") != null && md.GetValue("x-nonce") != null),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.AtLeast(2));
        }

        [Fact]
        public async Task TryDeleteAttachmentIfUnreferencedAdminAsync_ShouldNotDeleteWhenReferenced()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.GetAttachmentInfoAsync(
                    It.IsAny<GetAttachmentInfoRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryAttachmentInfo(new GetAttachmentInfoReply { Exists = true, Ready = true, RefCount = 2 }));

            var result = await mockClient.Object.TryDeleteAttachmentIfUnreferencedAdminAsync("tok1");

            Assert.False(result.Deleted);
            Assert.True(result.WasReferenced);
            Assert.NotNull(result.Info);
            Assert.Equal(2, result.Info.RefCount);
            Assert.Null(result.Delete);

            mockClient.Verify(client => client.DeleteAttachmentAsync(
                It.IsAny<DeleteAttachmentRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task TryDeleteAttachmentIfUnreferencedAdminAsync_ShouldDeleteWhenUnreferenced()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.GetAttachmentInfoAsync(
                    It.IsAny<GetAttachmentInfoRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryAttachmentInfo(new GetAttachmentInfoReply { Exists = true, Ready = true, RefCount = 0 }));

            mockClient
                .Setup(client => client.DeleteAttachmentAsync(
                    It.IsAny<DeleteAttachmentRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryDelete(new DeleteAttachmentReply { Success = true }));

            var result = await mockClient.Object.TryDeleteAttachmentIfUnreferencedAdminAsync("tok1");

            Assert.True(result.Deleted);
            Assert.False(result.WasReferenced);
            Assert.NotNull(result.Delete);
            Assert.True(result.Delete!.Success);

            mockClient.Verify(client => client.DeleteAttachmentAsync(
                It.Is<DeleteAttachmentRequest>(r => r.Token == "tok1" && r.Force == false),
                It.Is<Metadata>(md => md != null && md.GetValue("x-ts") != null && md.GetValue("x-nonce") != null),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task QueueMailMergeReplyAsync_ShouldForwardQueueCall()
        {
            var mockClient = new Mock<MailGrpcService.MailGrpcServiceClient>(MockBehavior.Loose)
            {
                CallBase = true,
            };

            mockClient
                .Setup(client => client.QueueMailMergeAsync(
                    It.IsAny<QueueMailMergeRequest>(),
                    It.IsAny<Metadata>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(MailMergeAndAttachmentFeatureTests.UnaryMergeReply(new QueueMailMergeReply
                {
                    Success = true,
                    MergeId = "merge123",
                    TemplateFileName = "t.mail",
                    BatchId = 0,
                }));

            // The extension method will call UploadAndMutateAttachmentsAsync only when remote.
            // Here we ensure local behaviour (no upload) and validate merge forwarding.
            MailClientConfiguration.Current = new MailClientConfiguration { MailQueueNetServiceChannelAddress = "http://localhost" };

            using var message = new System.Net.Mail.MailMessage("from@test.local", "to@test.local")
            {
                Subject = "Merge test",
                Body = "Body",
            };

            var reply = await mockClient.Object.QueueMailMergeReplyAsync(message, mergeId: "merge123");

            Assert.True(reply.Success);
            Assert.Equal("merge123", reply.MergeId);

            mockClient.Verify(x => x.QueueMailMergeAsync(
                It.Is<QueueMailMergeRequest>(r => r != null && r.MergeId == "merge123" && r.Message != null),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private sealed class TempFile : IDisposable
        {
            private bool disposed;

            public TempFile(string content)
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MailQueueNet.Tests");
                Directory.CreateDirectory(folder);

                this.FilePath = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(this.FilePath, content);
            }

            public string FilePath { get; }

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;

                try
                {
                    File.Delete(this.FilePath);
                }
                catch
                {
                }
            }
        }
    }
}
