// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaRegistryReaderTests
{
    private static readonly NodeId File = new("Model", 1);
    private static readonly NodeId GroupNode = new("group-team-a", 1);
    private static readonly NodeId LogicalResourceNode = new("logical-schema-a", 1);
    private static readonly NodeId VersionsNode = new("schema-a-versions", 1);
    private static readonly NodeId DefaultVersionNode = new("schema-a-default-v1", 1);
    private static readonly NodeId VersionTwoNode = new("schema-a-v2", 1);
    private static readonly NodeId ResourceType = new("ResourceType", 1);
    private static readonly NodeId ResourceVersionsType = new("ResourceVersionsType", 1);
    private static readonly NodeId AttributesType = new("AttributesType", 1);
    private const string TestModel =
        """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema"}}}}}""";

    [Test]
    [Arguments(1u)]
    [Arguments(2u)]
    [Arguments(3u)]
    [Arguments(4u)]
    [Arguments(uint.MaxValue)]
    public async Task FileTransfersApplyBufferLimitAlongsideChunkAndCaptureBudgets(uint limit)
    {
        var session = new FakeSession { FileBufferLimit = limit };
        var reader = await ValidatedReader(session, maxBytes: 5, chunkSize: 4);
        var effective = (int)Math.Min(4u, limit);
        byte[] expected = [1, 2, 3, 4, 5];
        session.Chunks.AddRange(expected.Chunk(effective));
        session.Chunks.Add([]);

        var bytes = await reader.ReadFileAsync(File);
        var writer = new OpcUaRegistryWriter(reader);
        await writer.ReplaceFileDocumentAsync(File, expected);

        await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("0102030405");
        await Assert.That(session.FileReadLengths.All(length => length > 0 && length <= effective)).IsTrue();
        await Assert.That(session.FileReadLengths[^1]).IsEqualTo(1);
        await Assert.That(Convert.ToHexString(session.WrittenChunks.SelectMany(chunk => chunk).ToArray()))
            .IsEqualTo("0102030405");
        await Assert.That(string.Join("|", session.WrittenChunks.Select(chunk => chunk.Length)))
            .IsEqualTo(string.Join("|", expected.Chunk(effective).Select(chunk => chunk.Length)));
        await Assert.That(session.BufferLimitFiles).IsEquivalentTo(new[] { File, File });
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileTransfersRetainConfiguredChunksWhenNoBufferLimitIsDeclared(bool write)
    {
        var session = new FakeSession();
        var reader = await ValidatedReader(session, maxBytes: 10, chunkSize: 2);
        byte[] expected = [1, 2, 3];
        if (write)
        {
            await new OpcUaRegistryWriter(reader).ReplaceFileDocumentAsync(File, expected);
            await Assert.That(string.Join("|", session.WrittenChunks.Select(chunk => chunk.Length))).IsEqualTo("2|1");
            await Assert.That(Convert.ToHexString(session.WrittenChunks.SelectMany(chunk => chunk).ToArray()))
                .IsEqualTo("010203");
        }
        else
        {
            session.Chunks.AddRange([new byte[] { 1, 2 }, new byte[] { 3 }, Array.Empty<byte>()]);
            var bytes = await reader.ReadFileAsync(File);
            await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("010203");
            await Assert.That(string.Join("|", session.FileReadLengths)).IsEqualTo("2|2|2");
        }
        await Assert.That(session.BufferLimitFiles.Single()).IsEqualTo(File);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, 0u)]
    [Arguments(true, 0u)]
    [Arguments(false, 1u)]
    [Arguments(true, 1u)]
    public async Task EmptyFileTransfersRequireUsableLimitAndRetainOpenCloseLifecycle(bool write, uint limit)
    {
        var session = new FakeSession { FileBufferLimit = limit };
        var reader = await ValidatedReader(session);
        session.FileSizes.Enqueue(0);
        session.FileSizes.Enqueue(0);
        session.Chunks.Add([]);
        Func<Task> transfer = async () =>
        {
            if (write) await new OpcUaRegistryWriter(reader).ReplaceFileDocumentAsync(File, ReadOnlyMemory<byte>.Empty);
            else await Assert.That((await reader.ReadFileAsync(File)).Length).IsEqualTo(0);
        };
        if (limit == 0)
        {
            var error = await Assert.ThrowsAsync<FederationException>(transfer);
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        }
        else
        {
            await transfer();
            if (!write) await Assert.That(session.FileReadLengths.Single()).IsEqualTo(1);
        }
        await Assert.That(session.Opened).IsEqualTo(limit == 0 ? 0 : 1);
        await Assert.That(session.Closed).IsEqualTo(limit == 0 ? 0 : 1);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.BufferLimitFiles.Single()).IsEqualTo(File);
    }

    [Test]
    [Arguments(false, "zero")]
    [Arguments(true, "zero")]
    [Arguments(false, "error")]
    [Arguments(true, "error")]
    [Arguments(false, "cancel")]
    [Arguments(true, "cancel")]
    public async Task FileTransfersRejectUnusableBufferLimitsBeforeAcquiringHandles(bool write, string failureKind)
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FederationException(FederationErrorCode.PolicyDenied, "Limit read denied.");
        var session = new FakeSession
        {
            FileBufferLimit = failureKind == "zero" ? 0u : 2u,
            BufferLimitFailure = failureKind == "error" ? native : null,
            AfterBufferLimitRead = failureKind == "cancel" ? cancellation.Cancel : null
        };
        var reader = await ValidatedReader(session);
        Func<Task> transfer = async () =>
        {
            if (write)
            {
                await new OpcUaRegistryWriter(reader).ReplaceFileDocumentAsync(File, new byte[] { 1 }, cancellation.Token);
            }
            else
            {
                _ = await reader.ReadFileAsync(File, cancellation.Token);
            }
        };
        if (failureKind == "cancel")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(transfer);
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(transfer);
            await Assert.That(error!.Code).IsEqualTo(failureKind == "zero"
                ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.PolicyDenied);
            if (failureKind == "error")
            {
                await Assert.That(ReferenceEquals(error, native)).IsTrue();
            }
        }
        await Assert.That(session.Opened).IsEqualTo(0);
        await Assert.That(session.FileReads).IsEqualTo(0);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    public async Task CreationHandleClosesBeforeLimitAcquisitionFailureWithoutReopening()
    {
        var session = new FakeSession();
        var reader = await ValidatedReader(session);
        var failure = new FederationException(FederationErrorCode.Unavailable, "Limit read failed.");
        session.BufferLimitFailure = failure;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await new OpcUaRegistryWriter(reader).CompleteCreatedResourceDocumentAsync(File, 42,
                new byte[] { 1, 2 }));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(session.Opened).IsEqualTo(0);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task FileReadConfirmsKnownSizeThroughEofAndCloses(int length)
    {
        var session = new FakeSession();
        session.FileSizes.Enqueue((ulong)length);
        session.FileSizes.Enqueue((ulong)length);
        var expected = Enumerable.Range(1, length).Select(i => (byte)i).ToArray();
        session.Chunks.AddRange(expected.Chunk(2));
        session.Chunks.Add([]);
        var reader = await ValidatedReader(session, maxBytes: 4, chunkSize: 2);

        var bytes = await reader.ReadFileAsync(File);

        await Assert.That(bytes).IsEquivalentTo(expected);
        await Assert.That(session.FileSizeReads).IsEqualTo(2);
        await Assert.That(session.FileSizes.Count).IsEqualTo(0);
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(5UL)]
    [Arguments(ulong.MaxValue)]
    public async Task FileReadRejectsReportedSizeAboveBudgetBeforeOpen(ulong size)
    {
        var session = new FakeSession();
        session.FileSizes.Enqueue(size);
        var reader = await ValidatedReader(session, maxBytes: 4);

        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.FileSizeReads).IsEqualTo(1);
        await Assert.That(session.Opened).IsEqualTo(0);
        await Assert.That(session.FileReads).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    [Arguments(0UL, 1)]
    [Arguments(1UL, 0)]
    [Arguments(2UL, 1)]
    [Arguments(1UL, 2)]
    public async Task FileReadRejectsEofOrBytesThatContradictKnownSize(ulong size, int length)
    {
        var closeFailure = new IOException("Synthetic Size capture cleanup failure.");
        var session = new FakeSession { CloseFailure = closeFailure };
        session.FileSizes.Enqueue(size);
        if (length > 0)
        {
            session.Chunks.Add(Enumerable.Range(1, length).Select(i => (byte)i).ToArray());
        }
        session.Chunks.Add([]);
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(session.FileSizeReads).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
    }

    [Test]
    [Arguments("unchanged")]
    [Arguments("larger")]
    [Arguments("smaller")]
    [Arguments("lost")]
    [Arguments("unknown")]
    [Arguments("learned")]
    [Arguments("learned-wrong")]
    public async Task FileReadRechecksSizeBeforeClosingAndAcceptsOnlyMatchingObservations(string change)
    {
        var session = new FakeSession { Chunks = { new byte[] { 1, 2 }, Array.Empty<byte>() } };
        session.FileSizes.Enqueue(change is "unknown" or "learned" or "learned-wrong" ? null : 2UL);
        session.FileSizes.Enqueue(change switch
        {
            "larger" or "learned-wrong" => 3UL,
            "smaller" => 1UL,
            "lost" or "unknown" => null,
            _ => 2UL
        });
        var reader = await ValidatedReader(session);
        var observedWhileOpen = false;
        session.AfterSizeRead = () => observedWhileOpen = session.Opened == 1 && session.Closed == 0;
        if (change is "unchanged" or "unknown" or "learned")
        {
            var bytes = await reader.ReadFileAsync(File);
            await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2 });
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadFileAsync(File));
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        }
        await Assert.That(observedWhileOpen).IsTrue();
        await Assert.That(session.FileSizeReads).IsEqualTo(2);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FileReadSizeFailureOrCancellationRetainsCleanupAndPreventsByteResults(
        bool afterEof, bool cancelled)
    {
        var session = new FakeSession { Chunks = { Array.Empty<byte>() } };
        var reader = await ValidatedReader(session);
        using var cancellation = new CancellationTokenSource();
        var failure = new FederationException(FederationErrorCode.PolicyDenied, "Size denied.");
        var cleanup = new IOException("Size cleanup failed.");
        session.CloseFailure = cleanup;
        session.AfterSizeRead = () =>
        {
            if (session.FileSizeReads == (afterEof ? 2 : 1))
            {
                if (cancelled)
                {
                    cancellation.Cancel();
                }
                else
                {
                    session.SizeFailure = failure;
                }
            }
        };
        Exception error;
        if (cancelled)
        {
            error = (await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await reader.ReadFileAsync(File, cancellation.Token)))!;
            await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            error = (await Assert.ThrowsAsync<FederationException>(
                async () => await reader.ReadFileAsync(File, cancellation.Token)))!;
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        }
        await Assert.That(session.Opened).IsEqualTo(afterEof ? 1 : 0);
        await Assert.That(session.Closed).IsEqualTo(afterEof ? 1 : 0);
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsEqualTo(afterEof);
        if (afterEof)
        {
            await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], cleanup)).IsTrue();
            await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        }
    }

    [Test]
    public async Task FileReadContinuesPastShortReadsUntilAnEmptyChunkThenCloses()
    {
        var session = new FakeSession { Chunks = { new byte[] { 1, 2 }, new byte[] { 3 }, Array.Empty<byte>() } };
        var reader = await ValidatedReader(session);

        var data = await reader.ReadFileAsync(File);

        await Assert.That(data).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.OpenMode).IsEqualTo((byte)1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task FileReadRequestsOnlyRemainingBudgetAndProbesExactLimitForEof(int size)
    {
        var session = new FakeSession();
        if (size > 0)
        {
            session.Chunks.Add(Enumerable.Range(1, Math.Min(size, 3)).Select(value => (byte)value).ToArray());
        }
        if (size >= 4)
        {
            session.Chunks.Add([4]);
        }
        if (size == 5)
        {
            session.Chunks.Add([5]);
        }
        session.Chunks.Add([]);
        var reader = await ValidatedReader(session, maxBytes: 4, chunkSize: 64);

        if (size <= 4)
        {
            var bytes = await reader.ReadFileAsync(File);
            await Assert.That(bytes).IsEquivalentTo(
                Enumerable.Range(1, size).Select(value => (byte)value).ToArray());
        }
        else
        {
            var failure = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadFileAsync(File));
            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(session.Chunks.Count).IsEqualTo(1);
        }
        var expectedLengths = size switch
        {
            0 => new[] { 4 },
            3 => new[] { 4, 1 },
            _ => new[] { 4, 1, 1 }
        };
        await Assert.That(session.FileReadLengths).IsEquivalentTo(expectedLengths);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileReadRejectsChunkLargerThanRequestAndRetainsCloseFailure(bool closeFails)
    {
        var closeFailure = new IOException("Synthetic Close failure.");
        var session = new FakeSession
        {
            Chunks = { new byte[] { 1, 2, 3 }, Array.Empty<byte>() },
            CloseFailure = closeFails ? closeFailure : null
        };
        var reader = await ValidatedReader(session, maxBytes: 100, chunkSize: 2);

        var failure = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadFileAsync(File));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(failure.Message).Contains("more bytes than requested");
        await Assert.That(session.FileReadLengths.Single()).IsEqualTo(2);
        await Assert.That(session.Chunks.Count).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(failure.Data.Contains("OpcUa.FileCloseFailure")).IsEqualTo(closeFails);
        if (closeFails)
        {
            await Assert.That(ReferenceEquals(failure.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FileReadRespectsChunkCapBeforeRemainingBudgetAndEofProbe(int chunkSize)
    {
        var contents = new byte[] { 1, 2, 3, 4, 5 };
        var session = new FakeSession();
        session.Chunks.AddRange(contents.Chunk(chunkSize));
        session.Chunks.Add([]);
        var reader = await ValidatedReader(session, maxBytes: contents.Length, chunkSize: chunkSize);

        var bytes = await reader.ReadFileAsync(File);

        await Assert.That(bytes).IsEquivalentTo(contents);
        var expectedLengths = chunkSize switch
        {
            1 => new[] { 1, 1, 1, 1, 1, 1 },
            2 => new[] { 2, 2, 1, 1 },
            _ => new[] { 3, 2, 1 }
        };
        await Assert.That(session.FileReadLengths).IsEquivalentTo(expectedLengths);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CancelledFileReadDoesNotOpenHandle()
    {
        var session = new FakeSession { Chunks = { Array.Empty<byte>() } };
        var reader = await ValidatedReader(session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadFileAsync(File, cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Opened).IsEqualTo(0);
        await Assert.That(session.FileReads).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    [Arguments("open", false)]
    [Arguments("open", true)]
    [Arguments("chunk", false)]
    [Arguments("chunk", true)]
    [Arguments("eof", false)]
    [Arguments("eof", true)]
    public async Task FileReadCancellationStopsIgnoredTokenAndClosesTransferredHandle(
        string phase, bool closeFails)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new IOException("Synthetic file Close failure.");
        var session = new FakeSession
        {
            Chunks = { phase == "chunk" ? new byte[] { 1 } : Array.Empty<byte>(), Array.Empty<byte>() },
            AfterOpen = phase == "open" ? cancellation.Cancel : null,
            AfterRead = phase == "open" ? null : cancellation.Cancel,
            CloseFailure = closeFails ? cleanup : null
        };
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadFileAsync(File, cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.FileReads).IsEqualTo(phase == "open" ? 0 : 1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.LastClosedHandle).IsEqualTo(7u);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsEqualTo(closeFails);
        if (closeFails)
        {
            await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], cleanup)).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileReadCancellationDuringSuccessfulClosePreventsReturningBytes(bool empty)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeSession { AfterClose = cancellation.Cancel };
        if (!empty)
        {
            session.Chunks.Add([1, 2]);
        }
        session.Chunks.Add([]);
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadFileAsync(File, cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.FileReads).IsEqualTo(empty ? 1 : 2);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task FileReadCancellationDuringCleanupDoesNotReplaceExistingFailure(
        bool readFails, bool closeFails)
    {
        using var cancellation = new CancellationTokenSource();
        var readFailure = new IOException("Synthetic read failure.");
        var closeFailure = new FederationException(FederationErrorCode.PolicyDenied, "Synthetic Close failure.");
        var session = new FakeSession
        {
            Chunks = { Array.Empty<byte>() },
            ReadFailure = readFails ? readFailure : null,
            CloseFailure = closeFails ? closeFailure : null,
            AfterClose = cancellation.Cancel,
            BeforeClose = cancellation.Cancel
        };
        var reader = await ValidatedReader(session);

        if (readFails)
        {
            var error = await Assert.ThrowsAsync<IOException>(
                async () => await reader.ReadFileAsync(File, cancellation.Token));
            await Assert.That(ReferenceEquals(error, readFailure)).IsTrue();
            await Assert.That(error!.Data.Contains("OpcUa.FileCloseFailure")).IsEqualTo(closeFails);
            if (closeFails)
            {
                await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
            }
        }
        else if (closeFails)
        {
            var error = await Assert.ThrowsAsync<FederationException>(
                async () => await reader.ReadFileAsync(File, cancellation.Token));
            await Assert.That(ReferenceEquals(error, closeFailure)).IsTrue();
        }
        else
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await reader.ReadFileAsync(File, cancellation.Token));
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(cancellation.IsCancellationRequested).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task ReadFailureIsRetainedWhenCloseAlsoFails()
    {
        var session = new FakeSession { ReadFailure = new InvalidOperationException("read"), CloseFailure = new InvalidOperationException("close") };
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Message).IsEqualTo("read");
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task CloseFailureAfterCompleteReadIsReported()
    {
        var session = new FakeSession { Chunks = { Array.Empty<byte>() }, CloseFailure = new InvalidOperationException("close") };
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
    }

    [Test]
    [Arguments(FederationErrorCode.PolicyDenied, false)]
    [Arguments(FederationErrorCode.NotFound, false)]
    [Arguments(FederationErrorCode.Unavailable, false)]
    [Arguments(FederationErrorCode.PolicyDenied, true)]
    [Arguments(FederationErrorCode.NotFound, true)]
    [Arguments(FederationErrorCode.Unavailable, true)]
    public async Task FileReadPreservesClassifiedCloseFailureAndOriginalOperationError(
        FederationErrorCode code, bool readFails)
    {
        var nativeFailure = new ServiceResultException(StatusCodes.BadSessionIdInvalid);
        var closeFailure = new FederationException(code, "Close failed.", "Retained diagnostic.", nativeFailure);
        var readFailure = readFails ? new InvalidOperationException("read") : null;
        var session = new FakeSession
        {
            Chunks = { new byte[] { 1, 2 }, Array.Empty<byte>() },
            ReadFailure = readFailure,
            CloseFailure = closeFailure
        };
        var reader = await ValidatedReader(session);

        if (readFails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await reader.ReadFileAsync(File));
            await Assert.That(ReferenceEquals(error, readFailure)).IsTrue();
            await Assert.That(ReferenceEquals(error!.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadFileAsync(File));
            await Assert.That(ReferenceEquals(error, closeFailure)).IsTrue();
            await Assert.That(error!.Code).IsEqualTo(code);
            await Assert.That(error.Diagnostic).IsEqualTo("Retained diagnostic.");
            await Assert.That(ReferenceEquals(error.InnerException, nativeFailure)).IsTrue();
        }
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.ClosedFiles.Single()).IsEqualTo(File);
        await Assert.That(session.LastClosedHandle).IsEqualTo(7u);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task BrowseFailureRetainsContinuationReleaseFailureDiagnostic()
    {
        var session = new FakeSession
        {
            BrowseNextFailure = new FederationException(FederationErrorCode.Unavailable, "browse"),
            ReleaseFailure = new InvalidOperationException("release")
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [9]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(error.Data.Contains("OpcUa.ContinuationReleaseFailure")).IsTrue();
    }

    [Test]
    public async Task DoesNotReleaseContinuationAlreadyConsumedBySuccessfulBrowseNext()
    {
        var failure = new FederationException(FederationErrorCode.InvalidPackage, "mapping failed");
        failure.Data["OpcUa.ContinuationConsumed"] = true;
        var session = new FakeSession { BrowseNextFailure = failure };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("component")], [3]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Released.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OversizedFileFailsWithLimitAndStillCloses()
    {
        var session = new FakeSession { Chunks = { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } } };
        var reader = await ValidatedReader(session, maxBytes: 4);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task BrowseFollowsEveryContinuationPoint()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [1]));
        session.Pages.Enqueue(new([Ref("b")], [2]));
        session.Pages.Enqueue(new([Ref("c")]));

        var all = await reader.BrowseAllAsync(File);

        await Assert.That(string.Join(",", all.Select(r => r.BrowseName.Name))).IsEqualTo("a,b,c");
    }

    [Test]
    public async Task ExpiredContinuationPointNeverYieldsACompleteCollection()
    {
        var session = new FakeSession { BrowseNextFailure = new FederationException(FederationErrorCode.Unavailable, "expired") };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [1]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
    }

    [Test]
    public async Task BudgetOverrunReleasesTheOutstandingContinuationPoint()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session, maxReferences: 1);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a"), Ref("b")], [9]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Released.Single()).IsEquivalentTo(new byte[] { 9 });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BrowsePageBudgetCountsInitialAndEmptyPages(int limit)
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session, maxBrowsePages: limit);
        await reader.ReadSpecVersionAsync(File, _ => true);
        for (var index = 0; index < limit; index++)
        {
            session.Pages.Enqueue(new([], [(byte)(index + 1)]));
        }
        session.Pages.Enqueue(new([]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(error.Message).IsEqualTo("The Browse result exceeds the page budget.");
        await Assert.That(session.Released.Single()).IsEquivalentTo(new byte[] { (byte)limit });
        await Assert.That(session.Pages.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BrowsePageBudgetPermitsCompletionAtExactLimit(int limit)
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session, maxBrowsePages: limit);
        await reader.ReadSpecVersionAsync(File, _ => true);
        for (var index = 0; index < limit - 1; index++)
        {
            session.Pages.Enqueue(new([], [1]));
        }
        session.Pages.Enqueue(new([Ref("final")]));

        var references = await reader.BrowseAllAsync(File);

        await Assert.That(references.Single().BrowseName.Name).IsEqualTo("final");
        await Assert.That(session.Released.Count).IsEqualTo(0);
        await Assert.That(session.Pages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BrowsePageBudgetRetainsReleaseFailureOnLimitError()
    {
        var failure = new IOException("Synthetic continuation release failure.");
        var session = new FakeSession { ReleaseFailure = failure };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session, maxBrowsePages: 1);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([], [7]));
        session.Pages.Enqueue(new([]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(ReferenceEquals(error.Data["OpcUa.ContinuationReleaseFailure"], failure)).IsTrue();
        await Assert.That(session.Released.Single()).IsEquivalentTo(new byte[] { 7 });
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task BrowsePageBudgetMustBePositive(int limit)
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = new OpcUaRegistryReader(new FakeSession(), maxBrowsePages: limit);
            await Task.CompletedTask;
        });

        await Assert.That(error!.ParamName).IsEqualTo("maxBrowsePages");
    }

    [Test]
    public async Task RootThatIsNotARegistryIsNotFoundNeverSubstituted()
    {
        var target = OpcUaFederationTargetTests.Target("""{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var namespaces = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:xregistry"]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession { IsRegistry = false }).VerifyRootAsync(target, namespaces));
        var root = await new OpcUaRegistryReader(new FakeSession()).VerifyRootAsync(target, namespaces);

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task SpecVersionIsReadAndCheckedBeforeUse()
    {
        static FakeSession Components() { var s = new FakeSession(); s.Pages.Enqueue(new([SpecVersionRef()])); return s; }

        var version = await new OpcUaRegistryReader(Components()).ReadSpecVersionAsync(File, v => v.StartsWith("1.0", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(Components()).ReadSpecVersionAsync(File, _ => false));

        await Assert.That(version).IsEqualTo("1.0-rc4");
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task RejectedSpecVersionRecheckClearsThePreviouslyAcceptedSessionVersion()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([SpecVersionRef()]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadSpecVersionAsync(File, _ => false));
        var browseError = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(browseError!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task ModelAndCapabilitiesAreParsedFromTheirNamedFileObjects()
    {
        var session = new FakeSession { Chunks = { System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"), Array.Empty<byte>(), System.Text.Encoding.UTF8.GetBytes("nope"), Array.Empty<byte>() } };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([FileRef("Model")]));
        session.Pages.Enqueue(new([FileRef("Capabilities")]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);

        using var model = await reader.ReadModelAsync(File);
        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadCapabilitiesAsync(File));

        await Assert.That(model.RootElement.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task FileDocumentWriteReplacesSuppliedFileTypeNode()
    {
        var (session, writer) = await WriterForResource();
        var file = new NodeId("Capabilities", 1);

        await writer.ReplaceFileDocumentAsync(file, new byte[] { 8, 9 });

        await Assert.That(session.LastOpenedFile).IsEqualTo(file);
        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 8, 9 });
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteAcceptsExactByteBudget()
    {
        var (session, writer) = await WriterForResource(maxBytes: 256, chunkSize: 128);

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, new byte[256]);

        await Assert.That(session.WrittenChunks.Count).IsEqualTo(2);
        await Assert.That(session.WrittenChunks.Sum(chunk => chunk.Length)).IsEqualTo(256);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task MissingOrDuplicateComponentIsNotGuessed()
    {
        var none = new FakeSession();
        var twice = new FakeSession();
        var missingReader = new OpcUaRegistryReader(none);
        var duplicateReader = new OpcUaRegistryReader(twice);
        none.Pages.Enqueue(new([SpecVersionRef()]));
        twice.Pages.Enqueue(new([SpecVersionRef()]));
        await missingReader.ReadSpecVersionAsync(File, _ => true);
        await duplicateReader.ReadSpecVersionAsync(File, _ => true);
        none.Pages.Enqueue(new([]));
        twice.Pages.Enqueue(new([FileRef("Model"), FileRef("Model")]));

        var absent = await Assert.ThrowsAsync<FederationException>(async () => await missingReader.ReadModelAsync(File));
        var duplicate = await Assert.ThrowsAsync<FederationException>(async () => await duplicateReader.ReadModelAsync(File));

        await Assert.That(absent!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(duplicate!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task SpecVersionMustBeAStringVariableComponent()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("SpecVersion", 1),
            new QualifiedName("SpecVersion", 1), NodeClass.Object)]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(session).ReadSpecVersionAsync(File, _ => true));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ModelComponentMustBeAFileTypeObject()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Model", 1),
            new QualifiedName("Model", 1), NodeClass.Object, ObjectTypeIds.BaseObjectType)]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);

        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadModelAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    public async Task JsonValueComponentRequiresVariableAndReturnsItsTypedValue()
    {
        var session = new FakeSession
        {
            JsonValue = Value("42", BuiltInType.UInt32)
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Epoch", 1),
            new QualifiedName("Epoch", 1), NodeClass.Variable)]));

        var value = await reader.ReadJsonValueComponentAsync(File, "Epoch");

        await Assert.That(value.GetUInt32()).IsEqualTo(42u);
    }

    [Test]
    public async Task RegistryPropertyMappingRequiresCoreFieldsAndPreservesTypedValues()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("7", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["Name"] = Value("\"Production\"", BuiltInType.String)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt"),
            VariableRef("Name")
        ]));

        var metadata = await reader.ReadRegistryPropertiesAsync(File);

        await Assert.That(metadata.GetProperty("registryid").GetString()).IsEqualTo("registry-1");
        await Assert.That(metadata.GetProperty("specversion").GetString()).IsEqualTo("1.0-rc4");
        await Assert.That(metadata.GetProperty("xid").GetString()).IsEqualTo("/");
        await Assert.That(metadata.GetProperty("epoch").GetUInt32()).IsEqualTo(7u);
        await Assert.That(metadata.GetProperty("name").GetString()).IsEqualTo("Production");
    }

    [Test]
    public async Task RegistryPropertyMappingRejectsAnInvalidEpochValue()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("-1", BuiltInType.Int32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryPropertyMappingRejectsTimestampsWithoutAnRfc3339Zone()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("7", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task GroupPropertiesRequireTheExpectedTypeAndMapCoreMetadata()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["Name"] = Value("\"Team A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([GroupRef("team-a", groupType)]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"), VariableRef("Name"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var group = await reader.ReadGroupPropertiesAsync(File, "teams", "team-a", groupType);

        await Assert.That(group.GetProperty("groupid").GetString()).IsEqualTo("team-a");
        await Assert.That(group.GetProperty("xid").GetString()).IsEqualTo("/teams/team-a");
        await Assert.That(group.GetProperty("epoch").GetUInt32()).IsEqualTo(3u);
        await Assert.That(group.GetProperty("name").GetString()).IsEqualTo("Team A");
    }

    [Test]
    public async Task GroupPropertyMappingRejectsXidThatDoesNotMatchTheSelectedGroup()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-b\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["Name"] = Value("\"Team A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([GroupRef("team-a", groupType)]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("Name"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadGroupPropertiesAsync(File, "teams", "team-a", groupType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task GroupCollectionIncludesOnlyInstancesOfTheDeclaredGroupType()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["Name"] = Value("\"Team A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            GroupRef("team-a", groupType), GroupRef("not-a-group", new NodeId("ResourceType", 1))
        ]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("Name"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var groups = await reader.ReadGroupCollectionAsync(File, "teams", groupType);

        await Assert.That(groups.GetArrayLength()).IsEqualTo(1);
        await Assert.That(groups[0].GetProperty("groupid").GetString()).IsEqualTo("team-a");
    }

    [Test]
    public async Task GroupCollectionMustBeDeclaredByTheRegistryModel()
    {
        var reader = await ModelReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadGroupCollectionAsync(File, "unknown", new NodeId("GroupType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ReaderResolvesLogicalResourceAndProjectsDefaultVersion()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("41", BuiltInType.UInt32),
                ["logical:Name"] = Value("\"Schema A default\"", BuiltInType.String),
                ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:Format"] = Value("\"application/json\"", BuiltInType.String),
                ["logical:ContentType"] = Value("\"application/schema+json\"", BuiltInType.String),
                ["logical:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["default:VersionId"] = Value("\"decoy-version\"", BuiltInType.String),
                ["default:Epoch"] = Value("99", BuiltInType.UInt32),
                ["default:Name"] = Value("\"Must not be browsed\"", BuiltInType.String)
            },
            StringValues =
            {
                ["logical-label:region"] = "logical-default-label",
                ["default-label:region"] = "exact-version-label"
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical"),
            VariableRef("VersionId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("Name", "logical"),
            VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
            VariableRef("Format", "logical"), VariableRef("ContentType", "logical")
        ]));

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-labels", 1), new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var labels = await reader.ReadAttributesAsync(LogicalResourceNode, AttributesType);

        await Assert.That(resource.GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(resource.GetProperty("epoch").GetUInt32()).IsEqualTo(41u);
        await Assert.That(resource.GetProperty("name").GetString()).IsEqualTo("Schema A default");
        await Assert.That(resource.GetProperty("format").GetString()).IsEqualTo("application/json");
        await Assert.That(resource.GetProperty("contenttype").GetString()).IsEqualTo("application/schema+json");
        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("logical-default-label");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|logical-schema-a|logical-labels");
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaPropertiesMapMetaBrowseNamesAndDefaultVersionSeparately()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["meta:MetaCreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["meta:MetaModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["meta:MetaName"] = Value("\"Schema metadata\"", BuiltInType.String),
                ["meta:MetaDescription"] = Value("\"Meta description\"", BuiltInType.String),
                ["meta:MetaDocumentation"] = Value("\"https://example.test/schema\"", BuiltInType.String),
                ["meta:MetaDefaultVersionSticky"] = Value("true", BuiltInType.Boolean),
                ["meta:MetaReadonly"] = Value("false", BuiltInType.Boolean),
                ["meta:MetaCompatibility"] = Value("\"BACKWARD\"", BuiltInType.String),
                ["meta:VersionId"] = Value("\"v2\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"),
            VariableRef("MetaEpoch", "meta"), VariableRef("MetaCreatedAt", "meta"),
            VariableRef("MetaModifiedAt", "meta"), VariableRef("MetaName", "meta"),
            VariableRef("MetaDescription", "meta"), VariableRef("MetaDocumentation", "meta"),
            VariableRef("MetaDefaultVersionSticky", "meta"), VariableRef("MetaReadonly", "meta"),
            VariableRef("MetaCompatibility", "meta"), VariableRef("VersionId", "meta")
        ]));

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo("/teams/team-a/schemas/schema-a/meta");
        await Assert.That(meta.GetProperty("epoch").GetUInt32()).IsEqualTo(17u);
        await Assert.That(meta.GetProperty("createdat").GetString()).IsEqualTo("2026-01-02T03:04:05Z");
        await Assert.That(meta.GetProperty("modifiedat").GetString()).IsEqualTo("2026-01-03T03:04:05Z");
        await Assert.That(meta.GetProperty("name").GetString()).IsEqualTo("Schema metadata");
        await Assert.That(meta.GetProperty("description").GetString()).IsEqualTo("Meta description");
        await Assert.That(meta.GetProperty("documentation").GetString()).IsEqualTo("https://example.test/schema");
        await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        await Assert.That(meta.GetProperty("readonly").GetBoolean()).IsFalse();
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo("BACKWARD");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v2");
        await Assert.That(meta.TryGetProperty("versionid", out _)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaXrefDoesNotBecomeDefaultVersionId()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaXref"] = Value("\"/teams/team-a/schemas/schema-target\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaXref", "meta")
        ]));

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(meta.GetProperty("xref").GetString()).IsEqualTo("/teams/team-a/schemas/schema-target");
        await Assert.That(meta.TryGetProperty("defaultversionid", out _)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaDeprecatedPreservesTypedOptionalFields()
    {
        var meta = await ReadMetaWithDeprecated(Value(
            """{"Effective":"2026-01-02T03:04:05Z","Removal":"2027-01-02T03:04:05+01:00","Alternative":"../replacement","Documentation":"https://example.test/deprecation"}""",
            BuiltInType.ExtensionObject, "DeprecationDataType"));

        var deprecated = meta.GetProperty("deprecated");
        await Assert.That(deprecated.GetProperty("effective").GetString()).IsEqualTo("2026-01-02T03:04:05Z");
        await Assert.That(deprecated.GetProperty("removal").GetString()).IsEqualTo("2027-01-02T03:04:05+01:00");
        await Assert.That(deprecated.GetProperty("alternative").GetString()).IsEqualTo("../replacement");
        await Assert.That(deprecated.GetProperty("documentation").GetString())
            .IsEqualTo("https://example.test/deprecation");
    }

    [Test]
    public async Task ResourceMetaDeprecatedRejectsTheWrongStructuredDataType()
    {
        var exception = await Assert.ThrowsAsync<FederationException>(async () =>
            await ReadMetaWithDeprecated(Value(
                """{"Effective":"2026-01-02T03:04:05Z"}""",
                BuiltInType.ExtensionObject, "VendorDeprecationDataType")))
            ?? throw new InvalidOperationException("The wrong MetaDeprecated DataType was accepted.");

        await Assert.That(exception.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("""{"Effective":"2026-01-02T03:04:05"}""")]
    [Arguments("""{"Documentation":"not a uri with spaces"}""")]
    public async Task ResourceMetaDeprecatedRejectsInvalidTypedFields(string deprecated)
    {
        var exception = await Assert.ThrowsAsync<FederationException>(async () =>
            await ReadMetaWithDeprecated(Value(deprecated, BuiltInType.ExtensionObject, "DeprecationDataType")))
            ?? throw new InvalidOperationException("The invalid MetaDeprecated field was accepted.");

        await Assert.That(exception.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"attributes":{"vendorconfig":{"type":"object","attributes":{"origins":{"type":"array","item":{"type":"string"}},"region":"string","tags":{"type":"map","item":{"type":"string"}}}}},"groups":{"teams":{"singular":"team"}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["root:VendorConfig"] = Value(
                    """{"Region":"registry","Origins":["primary","backup"],"Tags":{"eu":"west"}}""",
                    BuiltInType.ExtensionObject, "VendorConfigDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"), VariableRef("VendorConfig", "root")
        ])]);

        var registry = await reader.ReadRegistryPropertiesAsync(File);

        var config = registry.GetProperty("vendorconfig");
        await Assert.That(config.GetProperty("region").GetString()).IsEqualTo("registry");
        await Assert.That(config.GetProperty("origins")[1].GetString()).IsEqualTo("backup");
        await Assert.That(config.GetProperty("tags").GetProperty("eu").GetString()).IsEqualTo("west");
    }

    [Test]
    public async Task RegistryPropertiesRejectCaseCollidingModelledProperties()
    {
        const string model = """{"attributes":{"vendorcode":"string"},"groups":{"teams":{"singular":"team"}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["root:VendorCode"] = Value("\"first\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"),
            VariableRef("VendorCode", "root"), VariableRef("VENDORCODE", "root")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task RegistryPropertiesDoNotUnicodeFoldModelledBrowseNames()
    {
        const string model = """{"attributes":{"key":"string"},"groups":{"teams":{"singular":"team"}}}""";
        const string kelvinKey = "\u212Aey";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                [$"root:{kelvinKey}"] = Value("\"not-key\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"), VariableRef(kelvinKey, "root")
        ])]);

        var registry = await reader.ReadRegistryPropertiesAsync(File);

        await Assert.That(registry.TryGetProperty("key", out _)).IsFalse();
    }

    [Test]
    public async Task GroupPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","attributes":{"policy":{"type":"object","attributes":{"enabled":"boolean"}},"region":"string"}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["group:GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["group:Epoch"] = Value("2", BuiltInType.UInt32),
                ["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["group:Name"] = Value("\"Team A\"", BuiltInType.String),
                ["group:Region"] = Value("\"west\"", BuiltInType.String),
                ["group:Policy"] = Value("""{"Enabled":true}""",
                    BuiltInType.ExtensionObject, "TeamPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))
        ])]);
        session.BrowsePages[GroupNode] = new([new([
            VariableRef("GroupId", "group"), VariableRef("Xid", "group"), VariableRef("Epoch", "group"),
            VariableRef("CreatedAt", "group"), VariableRef("ModifiedAt", "group"), VariableRef("Name", "group"),
            VariableRef("Region", "group"), VariableRef("Policy", "group")
        ])]);

        var group = await reader.ReadGroupPropertiesAsync(File, "teams", "team-a",
            new NodeId("GroupType", 1));

        await Assert.That(group.GetProperty("region").GetString()).IsEqualTo("west");
        await Assert.That(group.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ResourcePropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":"string","policy":{"type":"object","attributes":{"enabled":"boolean"}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["logical:Origin"] = Value("\"catalog\"", BuiltInType.String),
                ["logical:Policy"] = Value("""{"Enabled":true}""",
                    BuiltInType.ExtensionObject, "SchemaPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical"),
            VariableRef("Origin", "logical"), VariableRef("Policy", "logical")
        ])]);

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(resource.GetProperty("origin").GetString()).IsEqualTo("catalog");
        await Assert.That(resource.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ResourcePropertiesRejectMissingRequiredModelledExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":{"type":"string","required":true}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourcePropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("does not conform to the xRegistry Model");
    }

    [Test]
    public async Task ResourcePropertiesApplyDefaultModelledExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":{"type":"string","required":true,"default":"catalog"}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical")
        ])]);

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(resource.GetProperty("origin").GetString()).IsEqualTo("catalog");
    }

    [Test]
    public async Task ResourceMetaPropertiesPreserveModelledExtensionObjects()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"Mode":"strict"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        var policy = meta.GetProperty("policy");
        await Assert.That(policy.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(policy.GetProperty("mode").GetString()).IsEqualTo("strict");
    }

    [Test]
    public async Task ResourceMetaPropertiesRejectModelledExtensionObjectsThatViolateTheirModel()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"Mode":"unsupported"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("does not conform to the xRegistry Model");
    }

    [Test]
    public async Task ResourceMetaPropertiesRejectCaseCollidingExtensionObjectFields()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean"}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"enabled":false}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("duplicate model field 'enabled'");
    }

    [Test]
    public async Task ResourceMetaRejectsEpochWithWrongBuiltInType()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:MetaEpoch"] = Value("17", BuiltInType.Int32)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "meta"), VariableRef("MetaEpoch", "meta")]));

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ResourceMetaAttributesReadMetaLabelsNotDelegatedVersionLabels()
    {
        var metaLabelsNode = new NodeId("meta-labels", 1);
        var session = new FakeSession { StringValues = { ["meta-label:region"] = "metadata-only" } };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            new OpcUaReference(metaLabelsNode, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([VariableRef("region", "meta-label")]));

        var labels = await reader.ReadResourceMetaAttributesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, AttributesType);

        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("metadata-only");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|meta-labels");
    }

    [Test]
    public async Task FederationSourceCompilesModelAndReturnsCapturedModelAndCapabilities()
    {
        var session = SourceSession();
        var source = await OpenSource(session);

        var model = await source.ReadAsync(new(FederationOperation.Model, "/"));
        var capabilities = await source.ReadAsync(new(FederationOperation.Capabilities, "/"));

        await Assert.That(source.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(source.Model.Groups.ContainsKey("teams")).IsTrue();
        await Assert.That(model.Metadata.GetProperty("model").GetProperty("groups")
            .GetProperty("teams").GetProperty("resources").GetProperty("schemas")
            .GetProperty("singular").GetString()).IsEqualTo("schema");
        await Assert.That(capabilities.Metadata.GetProperty("flags")[0].GetString()).IsEqualTo("doc");
        await Assert.That(model.Context).IsEqualTo(source.Context);
        await Assert.That(capabilities.Context).IsEqualTo(source.Context);
    }

    [Test]
    public async Task FederationResourceEntityIncludesMetaVersionsAndDocumentLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a"));
        var entity = result.Metadata.GetProperty("entity");
        var meta = entity.GetProperty("meta");
        var version = entity.GetProperty("versions").GetProperty("v1");

        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("resource");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(entity.TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(entity.GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo(
            "/teams/team-a/schemas/schema-a/meta");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v1");
        await Assert.That(meta.GetProperty("self").GetString()).IsEqualTo("#/entity/meta");
        await Assert.That(meta.GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entity/versions/v1");
        await Assert.That(version.GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/entity/versions/v1");
    }

    [Test]
    public async Task FederationCollectionIsCompleteAndUsesCollectionLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        ConfigureResourceCollectionWalk(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Collection,
            "/teams/team-a/schemas"));
        var entity = result.Metadata.GetProperty("entities").GetProperty("schema-a");

        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("collection");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entities/schema-a");
        await Assert.That(entity.GetProperty("labels").GetProperty("region").GetString())
            .IsEqualTo("resource-region");
        await Assert.That(entity.GetProperty("meta").GetProperty("self").GetString())
            .IsEqualTo("#/entities/schema-a/meta");
        await Assert.That(entity.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("#/entities/schema-a/versions/v1");
    }

    [Test]
    public async Task FederationCollectionSelectorMatchesLiteralLabelsAndRebasesPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        ConfigureResourceCollectionWalk(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Collection, "/teams/team-a/schemas",
            new FederationLabelSelector("region", "RESOURCE-REGION")));
        var entity = result.Metadata.GetProperty("entity");

        await Assert.That(result.SelectedXid).IsEqualTo("/teams/team-a/schemas/schema-a");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(entity.GetProperty("metaurl").GetString()).IsEqualTo("#/entity/meta");
        await Assert.That(entity.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("#/entity/versions/v1");
    }

    [Test]
    public async Task FederationMetaEntityAndExactVersionUseEntityLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        var source = await OpenSource(session);

        var meta = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a/meta"));
        var versionSession = SourceSession();
        ConfigureResourceAddressSpace(versionSession);
        var versionSource = await OpenSource(versionSession);
        var version = await versionSource.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a/versions/v1"));

        await Assert.That(meta.Metadata.GetProperty("kind").GetString()).IsEqualTo("meta");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("self").GetString())
            .IsEqualTo("#/entity");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("defaultversionid").GetString())
            .IsEqualTo("v1");
        var versionEntity = version.Metadata.GetProperty("entity");
        await Assert.That(version.Metadata.GetProperty("kind").GetString()).IsEqualTo("version");
        await Assert.That(versionEntity.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(versionEntity.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(versionEntity.GetProperty("self").GetString()).IsEqualTo("#/entity");
    }

    [Test]
    public async Task FederationSourceRejectsApiViewWithoutRetrievableCoreApiUrls()
    {
        var source = await OpenSource(SourceSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity, "/",
                representation: FederationRepresentation.ApiView)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task FederationSourceRejectsMissingObjectTypeMappingInsteadOfGuessing()
    {
        var session = SourceSession();

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session, new(
                new Dictionary<string, NodeId>(StringComparer.Ordinal),
                new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>(),
                AttributesType)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.BrowsedNodes.Contains(GroupNode)).IsFalse();
    }

    [Test]
    [Arguments("missing")]
    [Arguments("wrong-type")]
    [Arguments("malformed-json")]
    [Arguments("invalid-model")]
    public async Task FederationBootstrapReportsMissingOrUnusableModelAsUnsupported(string state)
    {
        var session = SourceSession(state switch
        {
            "malformed-json" => "{",
            "invalid-model" => """{"groups":{"teams":{"singular":42}}}""",
            _ => TestModel
        });
        if (state is "missing" or "wrong-type")
        {
            session.BrowsePages[File] = new([
                new([SpecVersionRef()]),
                new([VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
                    VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")]),
                state == "missing" ? new([]) : new([TypedObjectRef("Model", new NodeId("WrongModel", 1),
                    new NodeId("NotFileType", 1))])
            ]);
        }

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(error.InnerException is FederationException).IsTrue();
        await Assert.That(session.OpenedFiles.Any(node => node.IdentifierAsString == "Capabilities")).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(GroupNode)).IsFalse();
        if (state is "malformed-json" or "invalid-model")
        {
            await Assert.That(session.Closed).IsEqualTo(1);
        }
    }

    [Test]
    public async Task FederationBootstrapRetainsMissingModelReadAndCleanupDiagnostics()
    {
        var failure = new FederationException(FederationErrorCode.NotFound,
            "The selected Model disappeared.", "Original native diagnostic.");
        var cleanup = new IOException("Synthetic close failure.");
        var session = SourceSession(readFailure: failure);
        session.CloseFailure = cleanup;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(error.Diagnostic).IsEqualTo("Original native diagnostic.");
        await Assert.That(ReferenceEquals(error.InnerException, failure)).IsTrue();
        await Assert.That(ReferenceEquals(failure.Data["OpcUa.FileCloseFailure"], cleanup)).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    [Arguments(FederationErrorCode.Unavailable)]
    [Arguments(FederationErrorCode.PolicyDenied)]
    [Arguments(FederationErrorCode.IntegrityError)]
    [Arguments(FederationErrorCode.LimitExceeded)]
    [Arguments(FederationErrorCode.InconsistentSnapshot)]
    [Arguments(FederationErrorCode.InvalidPackage)]
    [Arguments(FederationErrorCode.Ambiguous)]
    public async Task FederationBootstrapDoesNotReclassifyModelReadFailure(FederationErrorCode code)
    {
        var failure = new FederationException(code, "Synthetic native failure.", "Retained diagnostic.");
        var session = SourceSession(readFailure: failure);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(error!.Diagnostic).IsEqualTo("Retained diagnostic.");
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    public async Task FederationBootstrapDoesNotReclassifyModelReadCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        var session = SourceSession(readFailure: failure);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await OpenSource(session));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task FederationBootstrapRejectsNonRegistryRootBeforeOpeningDocuments()
    {
        var session = SourceSession(isRegistry: false);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.OpenedFiles.Count).IsEqualTo(0);
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("group")]
    [Arguments("resource")]
    [Arguments("group-case")]
    [Arguments("resource-case")]
    [Arguments("document-contract")]
    public async Task FederationBootstrapValidatesEveryModelCollectionBeforeReturningSource(string mismatch)
    {
        const string model = """
            {"groups":{
                "teams":{"singular":"team","resources":{"schemas":{"singular":"schema"}}},
                "departments":{"singular":"department","resources":{"pages":{"singular":"page"}}}
            }}
            """;
        var session = SourceSession(model);
        var groups = new Dictionary<string, NodeId>(StringComparer.Ordinal)
        {
            ["teams"] = new("GroupType", 1),
            ["departments"] = new("DepartmentType", 1)
        };
        var resources = new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
        {
            [("teams", "schemas")] = new(ResourceType, ResourceVersionsType),
            [("departments", "pages")] = new(new NodeId("PageType", 1), new NodeId("PageVersionsType", 1))
        };
        switch (mismatch)
        {
            case "group":
                groups.Remove("departments");
                break;
            case "resource":
                resources.Remove(("departments", "pages"));
                break;
            case "group-case":
                groups["Departments"] = groups["departments"];
                groups.Remove("departments");
                break;
            case "resource-case":
                resources[("departments", "Pages")] = resources[("departments", "pages")];
                resources.Remove(("departments", "pages"));
                break;
            case "document-contract":
                resources[("departments", "pages")] =
                    OpcUaResourceTypeBinding.ForMetadataOnly(new NodeId("PageType", 1));
                break;
        }

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenSource(session, new(groups, resources, AttributesType)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.BrowsedNodes.Contains(GroupNode)).IsFalse();
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FederationBootstrapAcceptsCompleteAndEmptyModelMappings(bool emptyModel)
    {
        var groups = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var resources = new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>();
        if (!emptyModel)
        {
            groups["teams"] = new("GroupType", 1);
            resources[("teams", "schemas")] = new(ResourceType, ResourceVersionsType);
        }
        var session = SourceSession(emptyModel ? """{"groups":{}}""" : TestModel);

        var source = await OpenSource(session, new(groups, resources, AttributesType));

        await Assert.That(source.Model.Groups.Count).IsEqualTo(emptyModel ? 0 : 1);
        await Assert.That(session.BrowsedNodes.Contains(GroupNode)).IsFalse();
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationDocumentReadUsesDefaultVersionsExternalReference()
    {
        var (session, source) = await OpenExternalReferenceSource();

        var result = await source.ReadAsync(new(FederationOperation.Document,
            "/teams/team-a/schemas/schema-a"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.GetProperty("binding").GetString()).IsEqualTo("opcua");
        var externalNodeId = result.ExternalDocument.GetProperty("expandednodeid");
        await Assert.That(externalNodeId.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(externalNodeId.GetProperty("serverapplicationuri").GetString())
            .IsEqualTo("urn:remote:application");
        await Assert.That(externalNodeId.GetProperty("namespaceuri").GetString())
            .IsEqualTo("urn:remote:namespace");
        await Assert.That(externalNodeId.GetProperty("identifiertype").GetString()).IsEqualTo("Numeric");
        await Assert.That(externalNodeId.GetProperty("identifier").GetUInt32()).IsEqualTo(42u);
        await Assert.That(session.Opened).IsEqualTo(2);
    }

    [Test]
    public async Task FederationDocumentReadUsesExactVersionsExternalReference()
    {
        var (_, source) = await OpenExternalReferenceSource();

        var result = await source.ReadAsync(new(FederationOperation.Document,
            "/teams/team-a/schemas/schema-a/versions/v1"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.ExternalDocument.GetProperty("expandednodeid")
            .GetProperty("serverapplicationuri").GetString()).IsEqualTo("urn:remote:application");
        await Assert.That(result.ExternalDocument.GetProperty("expandednodeid")
            .GetProperty("identifier").GetUInt32()).IsEqualTo(42u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FederationDocumentReadReturnsExactLocalVersionBytes(bool exact)
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":true}}}}}""";
        byte[] expected = [0x00, 0x7f, 0x80, 0xff];
        var session = SourceSession(model);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([expected, Array.Empty<byte>()]);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Document,
            exact ? "/teams/team-a/schemas/schema-a/versions/v1" : "/teams/team-a/schemas/schema-a"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.Document!.Length).IsEqualTo(expected.Length);
        await Assert.That(result.Document.ContentType).IsEqualTo("application/json");
        using var stream = result.Document.OpenRead();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual);
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
        await Assert.That(session.JsonValueReads.Count(node => node.IdentifierAsString == "version:Epoch"))
            .IsEqualTo(4);
    }

    [Test]
    [Arguments(false, "Epoch")]
    [Arguments(true, "Epoch")]
    [Arguments(false, "Name")]
    [Arguments(true, "Name")]
    [Arguments(false, "ContentType")]
    [Arguments(true, "ContentType")]
    [Arguments(false, "Format")]
    [Arguments(true, "Format")]
    [Arguments(false, "ModifiedAt")]
    [Arguments(true, "ModifiedAt")]
    public async Task FederationDocumentReadRejectsSelectedVersionChangesBeforeReturningBytes(
        bool exact, string property)
    {
        foreach (var phase in new[] { "chunk", "eof", "close" })
        {
            var session = SourceSession();
            ConfigureResourceAddressSpace(session);
            session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
            session.Chunks.AddRange([new byte[] { 1, 2 }, Array.Empty<byte>()]);
            var source = await OpenSource(session);
            var changed = property switch
            {
                "Epoch" => Value("5", BuiltInType.UInt32),
                "Name" => Value("\"updated name\"", BuiltInType.String),
                "ContentType" => Value("\"application/octet-stream\"", BuiltInType.String),
                "Format" => Value("\"binary\"", BuiltInType.String),
                _ => Value("\"2026-01-02T00:00:00Z\"", BuiltInType.DateTime)
            };
            var reads = 0;
            session.AfterRead = () =>
            {
                reads++;
                if (phase == "chunk" && reads == 1 || phase == "eof" && reads == 2)
                {
                    session.JsonValues["version:" + property] = changed;
                }
            };
            session.AfterClose = () =>
            {
                if (phase == "close")
                {
                    session.JsonValues["version:" + property] = changed;
                }
            };

            var failure = await Assert.ThrowsAsync<FederationException>(async () =>
                await source.ReadAsync(new(FederationOperation.Document, exact
                    ? "/teams/team-a/schemas/schema-a/versions/v1" : "/teams/team-a/schemas/schema-a")));

            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
            await Assert.That(failure.Message).IsEqualTo("The selected Version changed while reading its document.");
            await Assert.That(reads).IsEqualTo(2);
            await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
            await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        }
    }

    [Test]
    [Arguments(false, "MetaEpoch")]
    [Arguments(true, "MetaEpoch")]
    [Arguments(false, "VersionId")]
    [Arguments(true, "VersionId")]
    public async Task FederationDocumentReadRechecksDefaultMetaOnlyForDefaultSelection(bool exact, string property)
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([new byte[] { 1, 2 }, Array.Empty<byte>()]);
        var source = await OpenSource(session);
        session.AfterClose = () => session.JsonValues["logical:" + property] = property == "MetaEpoch"
            ? Value("4", BuiltInType.UInt32) : Value("\"v2\"", BuiltInType.String);

        if (exact)
        {
            var result = await source.ReadAsync(new(FederationOperation.Document,
                "/teams/team-a/schemas/schema-a/versions/v1"));
            await Assert.That(result.SelectedXid).IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
            await Assert.That(result.Document!.Length).IsEqualTo(2);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<FederationException>(async () =>
                await source.ReadAsync(new(FederationOperation.Document, "/teams/team-a/schemas/schema-a")));
            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
            await Assert.That(failure.Message).IsEqualTo(
                "Resource Meta changed while selecting and reading the default Version document.");
        }
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task FederationDocumentReadPreservesVersionRecheckFailureOrCancellation(bool exact, bool cancelled)
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([new byte[] { 1, 2 }, Array.Empty<byte>()]);
        var source = await OpenSource(session);
        using var cancellation = new CancellationTokenSource();
        var native = new ServiceResultException(StatusCodes.BadNoCommunication);
        var failure = new FederationException(FederationErrorCode.Unavailable, "Version recheck failed.",
            innerException: native);
        var attempted = false;
        session.AfterClose = () => session.BeforeJsonRead = node =>
        {
            if (node.IdentifierAsString == "version:Epoch")
            {
                attempted = true;
                if (cancelled)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw failure;
            }
        };
        var request = new FederationReadRequest(FederationOperation.Document, exact
            ? "/teams/team-a/schemas/schema-a/versions/v1" : "/teams/team-a/schemas/schema-a");
        if (cancelled)
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await source.ReadAsync(request, cancellation.Token));
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await source.ReadAsync(request, cancellation.Token));
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
            await Assert.That(ReferenceEquals(error!.InnerException, native)).IsTrue();
        }
        await Assert.That(attempted).IsTrue();
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false, "close")]
    [Arguments(true, "close")]
    [Arguments(false, "version")]
    [Arguments(true, "version")]
    [Arguments(false, "meta")]
    public async Task FederationDocumentReadDoesNotReturnBytesAfterCancellationDuringCaptureRechecks(
        bool exact, string phase)
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([new byte[] { 1, 2 }, Array.Empty<byte>()]);
        var source = await OpenSource(session);
        using var cancellation = new CancellationTokenSource();
        session.AfterClose = () =>
        {
            if (phase == "close")
            {
                cancellation.Cancel();
            }
            else
            {
                session.BeforeJsonRead = node =>
                {
                    if (node.IdentifierAsString == (phase == "version" ? "version:ContentType" : "logical:MetaEpoch"))
                    {
                        cancellation.Cancel();
                    }
                };
            }
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.ReadAsync(new(FederationOperation.Document, exact
                ? "/teams/team-a/schemas/schema-a/versions/v1" : "/teams/team-a/schemas/schema-a"),
                cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        if (phase == "close")
        {
            await Assert.That(session.JsonValueReads.Count(node => node.IdentifierAsString == "version:Epoch"))
                .IsEqualTo(3);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task FederationDocumentReadDoesNotMaskFileReadOrCloseFailureWithVersionRecheck(
        bool exact, bool closeFailure)
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([new byte[] { 1, 2 }, Array.Empty<byte>()]);
        var source = await OpenSource(session);
        var original = new FederationException(FederationErrorCode.Unavailable, "File operation failed.");
        if (closeFailure)
        {
            session.CloseFailure = original;
        }
        else
        {
            session.ReadFailure = original;
        }

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await source.ReadAsync(new(FederationOperation.Document, exact
                ? "/teams/team-a/schemas/schema-a/versions/v1" : "/teams/team-a/schemas/schema-a")));

        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(session.JsonValueReads.Count(node => node.IdentifierAsString == "version:Epoch"))
            .IsEqualTo(3);
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|Capabilities|schema-a-default-v1");
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceDirectedPinnedFederationRejectsRetargetingBeforeProviderSelection(bool changed)
    {
        var (session, source) = await OpenExternalReferenceSource();
        AddSourceOrigin(session, new("urn:approved-origin", "", ExpandedNodeId.Null));
        var identity = session.ExternalNodeIdMappings[session.ExpandedNodeIds["logical:ExternalReference"]];
        var origin = OpcUaRegistryOrigin.Stable("urn:approved-origin");
        var pin = new OpcUaFederationSourcePin(origin, "/remote/g1/artifacts/r1",
            OpcUaExternalTargetRole.LogicalResource, changed
                ? new(identity.ServerApplicationUri, identity.NamespaceUri, IdType.Numeric, 99u) : identity);
        var calls = 0;
        var unavailable = new FederationException(FederationErrorCode.Unavailable, "Provider is unavailable.");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await source.ResolvePinnedExternalResourceAsync("/teams/team-a/schemas/schema-a",
                new NodeId("RegistryOriginDataType", 1), OpcUaFederationSourcePin.Parse(pin.Serialize()), origin,
                (_, _, _) =>
                {
                    calls++;
                    return ValueTask.FromException<IOpcUaTrustedProviderLease>(unavailable);
                }, _ => true));

        await Assert.That(error!.Code).IsEqualTo(changed ? FederationErrorCode.PolicyDenied :
            FederationErrorCode.Unavailable);
        await Assert.That(calls).IsEqualTo(changed ? 0 : 1);
        await Assert.That(session.ExpandedNodeIdReads.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SourceDirectedFederationReadsLogicalOriginAndSelectedReferenceBeforeProvider(
        bool exactVersion, bool stableOrigin)
    {
        var (session, source) = await OpenExternalReferenceSource();
        var expected = session.ExternalNodeIdMappings[session.ExpandedNodeIds[
            exactVersion ? "version:ExternalReference" : "logical:ExternalReference"]];
        var root = new ExpandedNodeId(new NodeId("RemoteRegistry", 2), null, 2u);
        var trustedOrigin = stableOrigin ? OpcUaRegistryOrigin.Stable("urn:approved-origin") :
            OpcUaRegistryOrigin.Application(new(expected.ServerApplicationUri, "urn:origin:root",
                IdType.String, "RemoteRegistry"));
        AddSourceOrigin(session, stableOrigin
            ? new("urn:approved-origin", "", ExpandedNodeId.Null)
            : new("", expected.ServerApplicationUri, root));
        session.ExternalNodeIdMappings[root] = trustedOrigin.RegistryRoot ??
            new(expected.ServerApplicationUri, "urn:origin:root", IdType.String, "RemoteRegistry");
        var failure = new FederationException(FederationErrorCode.Unavailable, "Provider intentionally unavailable.");
        OpcUaRegistryOrigin? receivedOrigin = null;
        OpcUaExternalNodeId? receivedTarget = null;
        var calls = 0;
        var xid = "/teams/team-a/schemas/schema-a" + (exactVersion ? "/versions/v1" : "");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await source.ResolveExternalResourceAsync(xid, new NodeId("RegistryOriginDataType", 1),
                trustedOrigin, (origin, target, _) =>
                {
                    calls++;
                    receivedOrigin = origin;
                    receivedTarget = target;
                    return ValueTask.FromException<IOpcUaTrustedProviderLease>(failure);
                }, _ => true, exactVersion ? OpcUaExternalTargetRole.ExactVersion :
                    OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(ReferenceEquals(receivedOrigin, trustedOrigin)).IsTrue();
        await Assert.That(ReferenceEquals(receivedTarget, expected)).IsTrue();
        await Assert.That(session.OriginReads.Single().Variable).IsEqualTo(new NodeId("logical:OriginRegistry", 1));
        await Assert.That(session.ExpandedNodeIdReads.Single()).IsEqualTo(
            new NodeId(exactVersion ? "version:ExternalReference" : "logical:ExternalReference", 1));
        if (!exactVersion)
        {
            await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsFalse();
            await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        }
    }

    [Test]
    [Arguments("absent")]
    [Arguments("mixed")]
    [Arguments("unapproved")]
    public async Task SourceDirectedFederationRejectsOriginBeforeReferenceReadOrProviderSelection(string state)
    {
        var (session, source) = await OpenExternalReferenceSource();
        if (state != "absent")
        {
            AddSourceOrigin(session, new("urn:source-origin", state == "mixed" ? "urn:server" : "",
                ExpandedNodeId.Null));
        }
        var calls = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await source.ResolveExternalResourceAsync("/teams/team-a/schemas/schema-a",
                new NodeId("RegistryOriginDataType", 1), OpcUaRegistryOrigin.Stable("urn:approved-origin"),
                (_, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("The rejected source origin acquired a provider.");
                }, _ => true, OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(error!.Code).IsEqualTo(state == "unapproved" ? FederationErrorCode.PolicyDenied :
            FederationErrorCode.InvalidPackage);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(session.ExpandedNodeIdReads.Count).IsEqualTo(0);
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceDirectedFederationDoesNotSubstituteAnotherRoleWhenSelectedReferenceIsAbsent(
        bool exactVersion)
    {
        var (session, source) = await OpenExternalReferenceSource();
        AddSourceOrigin(session, new("urn:approved-origin", "", ExpandedNodeId.Null));
        var selected = exactVersion ? DefaultVersionNode : LogicalResourceNode;
        session.BrowsePages[selected] = new([new(session.BrowsePages[selected].Peek().References
            .Where(reference => reference.BrowseName.Name != "ExternalReference").ToArray())]);
        var calls = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await source.ResolveExternalResourceAsync(
                "/teams/team-a/schemas/schema-a" + (exactVersion ? "/versions/v1" : ""),
                new NodeId("RegistryOriginDataType", 1), OpcUaRegistryOrigin.Stable("urn:approved-origin"),
                (_, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("The absent reference selected a provider.");
                }, _ => true, exactVersion ? OpcUaExternalTargetRole.ExactVersion :
                    OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.ExpandedNodeIdReads.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
        if (!exactVersion)
        {
            await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        }
    }

    [Test]
    [Arguments("cancelled")]
    [Arguments("role")]
    [Arguments("source-role")]
    public async Task SourceDirectedFederationRejectsCancelledOrInvalidRequestsBeforeSourceReads(string state)
    {
        var (session, source) = await OpenExternalReferenceSource();
        var before = session.BrowsedNodes.Count;
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        if (state == "cancelled")
        {
            cancellation.Cancel();
        }
        ValueTask<OpcUaResolvedExternalResource> Resolve() => source.ResolveExternalResourceAsync(
            "/teams/team-a/schemas/schema-a", new NodeId("RegistryOriginDataType", 1),
            OpcUaRegistryOrigin.Stable("urn:approved"), (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("The rejected source request acquired a provider.");
            }, _ => true, state == "role" ? (OpcUaExternalTargetRole)99 :
                state == "source-role" ? OpcUaExternalTargetRole.ExactVersion : OpcUaExternalTargetRole.LogicalResource,
            cancellation.Token);

        if (state == "cancelled")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await Resolve());
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else if (state == "role")
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Resolve());
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () => await Resolve());
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        }
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(before);
        await Assert.That(session.OriginReads.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    private static void AddSourceOrigin(FakeSession session, OpcUaRegistryOriginValue value)
    {
        session.OriginValue = value;
        session.BrowsePages[LogicalResourceNode] = new([new([
            .. session.BrowsePages[LogicalResourceNode].Peek().References,
            VariableRef("OriginRegistry", "logical")
        ])]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginRegistryIsReadFromLogicalResourceWithoutSelectingItsDefaultVersion(bool stable)
    {
        var (session, reader) = await OriginReader();
        var root = stable ? ExpandedNodeId.Null : new ExpandedNodeId(new NodeId("RemoteRegistry", 2), null, 2u);
        session.OriginValue = new(stable ? "urn:Origin:Exact%2f" : "", stable ? "" : "urn:remote", root);
        var originType = new NodeId("RegistryOriginDataType", 1);

        var origin = await reader.ReadOriginRegistryAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, originType);

        await Assert.That(ReferenceEquals(origin, session.OriginValue)).IsTrue();
        await Assert.That(session.OriginReads.Count).IsEqualTo(1);
        await Assert.That(session.OriginReads[0]).IsEqualTo((new NodeId("logical:OriginRegistry", 1), originType));
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        await Assert.That(session.OpenedFiles.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("object")]
    [Arguments("duplicate")]
    public async Task OriginRegistryRequiresOneLogicalResourceVariable(string state)
    {
        var (session, reader) = await OriginReader(state);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadOriginRegistryAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new NodeId("RegistryOriginDataType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.OriginReads.Count).IsEqualTo(0);
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task OriginRegistryRejectsMixedAndIncompleteAuthoritativeForms(
        bool hasOrigin, bool hasServer, bool hasRoot)
    {
        var (session, reader) = await OriginReader();
        session.OriginValue = new(hasOrigin ? "urn:origin" : "", hasServer ? "urn:server" : "",
            hasRoot ? new ExpandedNodeId("Registry", "urn:registry") : ExpandedNodeId.Null);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadOriginRegistryAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new NodeId("RegistryOriginDataType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.OriginReads.Count).IsEqualTo(1);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReader Reader)> OriginReader(string state = "valid")
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([ResourceReferencePage()]);
        var properties = session.BrowsePages[LogicalResourceNode].Peek().References.ToList();
        if (state != "absent")
        {
            properties.Add(state == "object"
                ? new(new NodeId("logical:OriginRegistry", 1), new QualifiedName("OriginRegistry", 1), NodeClass.Object)
                : VariableRef("OriginRegistry", "logical"));
        }
        if (state == "duplicate")
        {
            properties.Add(VariableRef("OriginRegistry", "logical"));
        }
        session.BrowsePages[LogicalResourceNode] = new([new(properties)]);
        return (session, reader);
    }

    [Test]
    public async Task ExternalReferenceIsMappedToUriIdentitySeparateFromResourceUrl()
    {
        var resourceType = new NodeId("ResourceType", 1);
        var reference = new ExpandedNodeId("resource", 0, "urn:xregistry", 1u);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            },
            ExpandedNodeIds = { ["ExternalReference"] = reference },
            ExternalNodeIdMappings =
            {
                [reference] = new("urn:remote:application", "urn:xregistry", IdType.String, "resource")
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, resourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId"), VariableRef("Xid"), VariableRef("Name"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));
        session.Pages.Enqueue(new([VariableRef("ExternalReference")]));

        var actual = await reader.ReadExternalNodeIdAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", resourceType);

        var identity = actual!;
        await Assert.That(identity.ServerApplicationUri).IsEqualTo("urn:remote:application");
        await Assert.That(identity.NamespaceUri).IsEqualTo("urn:xregistry");
        await Assert.That(identity.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(identity.Identifier).IsEqualTo("resource");
    }

    [Test]
    public async Task ReaderExplicitVersionRequiresExactTypedMatchingChild()
    {
        var exactSession = new FakeSession
        {
            JsonValues =
            {
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("52", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String)
            }
        };
        var exactReader = await ModelReader(exactSession);
        exactSession.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        exactSession.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        exactSession.Pages.Enqueue(new([
            TypedObjectRef("wrong-type", new NodeId("wrong-version", 1), new NodeId("OtherType", 1)),
            TypedObjectRef("v2", VersionTwoNode, ResourceType)
        ]));
        exactSession.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
            VariableRef("ModifiedAt", "v2")
        ]));

        var resource = await exactReader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2", ResourceVersionsType);

        await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(resource.GetProperty("epoch").GetUInt32()).IsEqualTo(52u);
        await Assert.That(NodeIdTrace(exactSession.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|schema-a-versions|schema-a-v2");
    }

    [Test]
    public async Task ExplicitVersionPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("52", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String),
                ["v2:Policy"] = Value(
                    """{"Enabled":true,"Mode":"strict"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        session.Pages.Enqueue(new([TypedObjectRef("v2", VersionTwoNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("CreatedAt", "v2"), VariableRef("ModifiedAt", "v2"),
            VariableRef("Name", "v2"),
            VariableRef("Policy", "v2")
        ]));
        session.Pages.Enqueue(new([VariableRef("Policy", "v2")]));

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
            ResourceVersionsType);

        await Assert.That(resource.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(resource.GetProperty("policy").GetProperty("mode").GetString()).IsEqualTo("strict");
    }

    [Test]
    [Arguments("mismatched-id")]
    [Arguments("wrong-type")]
    [Arguments("missing")]
    public async Task ReaderExplicitVersionRejectsInvalidChildWithoutUsingGroupSibling(string scenario)
    {
        var hasMatchingTypeChild = scenario == "mismatched-id";
        var expectedCode = hasMatchingTypeChild
            ? FederationErrorCode.InvalidPackage : FederationErrorCode.NotFound;
        var page = scenario switch
        {
            "mismatched-id" => new OpcUaBrowsePage([TypedObjectRef("v2", VersionTwoNode, ResourceType)]),
            "wrong-type" => new OpcUaBrowsePage([
                TypedObjectRef("v2", new NodeId("wrong-version", 1), new NodeId("OtherType", 1))]),
            _ => new OpcUaBrowsePage([])
        };
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v2:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                    ["v2:Epoch"] = Value("51", BuiltInType.UInt32),
                    ["v2:Name"] = Value("\"Schema A mismatched version\"", BuiltInType.String),
                    ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new([
                TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
                TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
            ]));
            session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
            session.Pages.Enqueue(page);
            if (hasMatchingTypeChild)
            {
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
                    VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
                    VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
                    VariableRef("ModifiedAt", "v2")
                ]));
            }

            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadResourcePropertiesAsync(
                    GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
                    ResourceVersionsType));

            await Assert.That(error!.Code).IsEqualTo(expectedCode);
            await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
                hasMatchingTypeChild
                    ? "Model|Model|group-team-a|logical-schema-a|schema-a-versions|schema-a-v2"
                    : "Model|Model|group-team-a|logical-schema-a|schema-a-versions");
            await Assert.That(session.Opened).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ReaderResourceCollectionEnumeratesLogicalResourcesOnly()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical-a:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical-a:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical-a:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical-a:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical-a:Name"] = Value("\"Schema A default\"", BuiltInType.String),
                ["logical-a:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-a:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-b:ResourceId"] = Value("\"schema-b\"", BuiltInType.String),
                ["logical-b:VersionId"] = Value("\"v3\"", BuiltInType.String),
                ["logical-b:Xid"] = Value("\"/teams/team-a/schemas/schema-b\"", BuiltInType.String),
                ["logical-b:Epoch"] = Value("8", BuiltInType.UInt32),
                ["logical-b:Name"] = Value("\"Schema B default\"", BuiltInType.String),
                ["logical-b:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-b:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "logical-a")]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical-a"), VariableRef("VersionId", "logical-a"),
            VariableRef("Xid", "logical-a"), VariableRef("Epoch", "logical-a"),
            VariableRef("Name", "logical-a"), VariableRef("CreatedAt", "logical-a"),
            VariableRef("ModifiedAt", "logical-a")
        ]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "logical-b")]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical-b"), VariableRef("VersionId", "logical-b"),
            VariableRef("Xid", "logical-b"), VariableRef("Epoch", "logical-b"),
            VariableRef("Name", "logical-b"), VariableRef("CreatedAt", "logical-b"),
            VariableRef("ModifiedAt", "logical-b")
        ]));

        var resources = await reader.ReadResourceCollectionAsync(
            GroupNode, "/teams/team-a", "schemas", ResourceType);
        var resourcesById = resources.EnumerateArray().ToDictionary(
            item => item.GetProperty("resourceid").GetString()!, StringComparer.Ordinal);

        await Assert.That(resources.GetArrayLength()).IsEqualTo(2);
        await Assert.That(resourcesById["schema-a"].GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(resourcesById["schema-a"].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
        await Assert.That(resourcesById["schema-b"].GetProperty("versionid").GetString()).IsEqualTo("v3");
        await Assert.That(resourcesById["schema-b"].GetProperty("epoch").GetUInt32()).IsEqualTo(8u);
        await Assert.That(session.BrowsedNodes.Count(node => node == LogicalResourceNode)).IsEqualTo(2);
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsFalse();
    }

    [Test]
    public async Task ResourceCollectionMustBeDeclaredByItsGroupModel()
    {
        var reader = await ModelReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadResourceCollectionAsync(
                File, "/teams/team-a", "unknown", new NodeId("ResourceType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ReaderVersionCollectionEnumeratesOnlySelectedResourceVersions()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["default:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["default:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["default:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String),
                ["default:Epoch"] = Value("4", BuiltInType.UInt32),
                ["default:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["default:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["default:Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("5", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        session.Pages.Enqueue(new([
            TypedObjectRef("v1", DefaultVersionNode, ResourceType),
            TypedObjectRef("v2", VersionTwoNode, ResourceType),
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "default"), VariableRef("VersionId", "default"),
            VariableRef("Xid", "default"), VariableRef("Epoch", "default"),
            VariableRef("Name", "default"), VariableRef("CreatedAt", "default"),
            VariableRef("ModifiedAt", "default")
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
            VariableRef("ModifiedAt", "v2")
        ]));

        var versions = await reader.ReadVersionCollectionAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, ResourceVersionsType);
        var versionsById = versions.EnumerateArray().ToDictionary(
            item => item.GetProperty("versionid").GetString()!, StringComparer.Ordinal);

        await Assert.That(versions.GetArrayLength()).IsEqualTo(2);
        await Assert.That(versionsById["v1"].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(versionsById["v1"].GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(versionsById["v2"].GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v2");
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(new NodeId("logical-schema-b", 1))).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(new NodeId("schema-b-v9", 1))).IsFalse();
    }

    [Test]
    public async Task AttributeContainerReadsTypedPropertiesByTheirBrowseNames()
    {
        var attributesType = new NodeId("AttributesType", 1);
        var session = new FakeSession
        {
            StringValues =
            {
                ["team"] = "alpha",
                ["x-vendor-flag"] = "enabled"
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Labels", 1),
            new QualifiedName("Labels", 1), NodeClass.Object, attributesType)]));
        session.Pages.Enqueue(new([VariableRef("team"), VariableRef("x-vendor-flag")]));

        var attributes = await reader.ReadAttributesAsync(File, attributesType);

        await Assert.That(attributes.GetProperty("team").GetString()).IsEqualTo("alpha");
        await Assert.That(attributes.GetProperty("x-vendor-flag").GetString()).IsEqualTo("enabled");
    }

    [Test]
    public async Task ReaderDefaultAndExplicitDocumentOperationsTargetSelectedVersion()
    {
        var session = new FakeSession
        {
            Chunks =
            {
                new byte[] { 1, 2 }, Array.Empty<byte>(),
                new byte[] { 8, 9 }, Array.Empty<byte>()
            },
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String),
                ["default:VersionId"] = Value("\"wrong-default\"", BuiltInType.String),
                ["default:Epoch"] = Value("99", BuiltInType.UInt32),
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("9", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
            VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
            VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"),
            TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
        ])]);
        session.BrowsePages[VersionsNode] = new([new([
            TypedObjectRef("v1", DefaultVersionNode, ResourceType),
            TypedObjectRef("v2", VersionTwoNode, ResourceType)
        ])]);
        session.BrowsePages[VersionTwoNode] = new([new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
            VariableRef("ModifiedAt", "v2")
        ])]);

        var defaultBytes = await reader.ReadResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
        await Assert.That(defaultBytes).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a");
        var explicitBytes = await reader.ReadResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2", ResourceVersionsType);

        await Assert.That(defaultBytes).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(explicitBytes).IsEquivalentTo(new byte[] { 8, 9 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsTrue();
    }

    [Test]
    [Arguments(false, "Epoch")]
    [Arguments(true, "Epoch")]
    [Arguments(false, "Name")]
    [Arguments(true, "Name")]
    [Arguments(false, "ContentType")]
    [Arguments(true, "ContentType")]
    [Arguments(false, "Format")]
    [Arguments(true, "Format")]
    [Arguments(false, "ModifiedAt")]
    [Arguments(true, "ModifiedAt")]
    public async Task DirectDocumentReadRejectsChangedSelectedPropertiesAfterCleanup(bool exact, string property)
    {
        foreach (var phase in new[] { "chunk", "eof", "close" })
        {
            var (session, reader) = await DirectDocumentReader();
            session.Chunks.AddRange([new byte[] { 0x80, 0xff }, Array.Empty<byte>()]);
            var changed = property switch
            {
                "Epoch" => Value("5", BuiltInType.UInt32),
                "ModifiedAt" => Value("\"2026-02-01T00:00:00Z\"", BuiltInType.DateTime),
                _ => Value("\"changed\"", BuiltInType.String)
            };
            var reads = 0;
            Action change = () => session.JsonValues[(exact ? "version:" : "logical:") + property] = changed;
            session.AfterRead = () =>
            {
                reads++;
                if (phase == "chunk" && reads == 1 || phase == "eof" && reads == 2) change();
            };
            session.AfterClose = () => { if (phase == "close") change(); };

            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, exact ? "v1" : null, ResourceVersionsType));

            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
            await Assert.That(reads).IsEqualTo(2);
            await Assert.That(session.ClosedFiles[^1]).IsEqualTo(exact ? DefaultVersionNode : LogicalResourceNode);
            await Assert.That(session.Closed).IsEqualTo(2);
            await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        }
    }

    [Test]
    [Arguments(false, "MetaEpoch")]
    [Arguments(true, "MetaEpoch")]
    [Arguments(false, "VersionId")]
    [Arguments(true, "VersionId")]
    [Arguments(false, "MetaName")]
    [Arguments(true, "MetaName")]
    public async Task DirectDocumentReadRechecksDefaultMetaWithoutCouplingExactVersions(bool exact, string property)
    {
        var (session, reader) = await DirectDocumentReader();
        session.Chunks.AddRange([new byte[] { 0x80, 0xff }, Array.Empty<byte>()]);
        session.AfterClose = () => session.JsonValues["logical:" + property] = property == "MetaEpoch"
            ? Value("4", BuiltInType.UInt32) : Value("\"v2\"", BuiltInType.String);
        if (exact)
        {
            var bytes = await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, "v1", ResourceVersionsType);
            await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("80FF");
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, resourceVersionsType: ResourceVersionsType));
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        }
        await Assert.That(session.JsonValueReads.Count(node => node.IdentifierAsString == "logical:MetaEpoch"))
            .IsEqualTo(exact ? 0 : property == "VersionId" ? 1 : 2);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    public async Task DirectDocumentReadPreservesRecheckErrorAndCancellation(bool meta, bool cancelled, bool exact)
    {
        var (session, reader) = await DirectDocumentReader();
        session.Chunks.AddRange([new byte[] { 1 }, Array.Empty<byte>()]);
        using var cancellation = new CancellationTokenSource();
        var original = new FederationException(FederationErrorCode.PolicyDenied, "Recheck denied.");
        session.AfterClose = () => session.BeforeJsonRead = node =>
        {
            if (node.IdentifierAsString != (meta ? "logical:MetaEpoch" : exact ? "version:Epoch" : "logical:Epoch")) return;
            if (cancelled) cancellation.Cancel();
            else throw original;
        };
        if (cancelled)
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, exact ? "v1" : null, ResourceVersionsType, cancellation.Token));
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, exact ? "v1" : null, ResourceVersionsType, cancellation.Token));
            await Assert.That(ReferenceEquals(error, original)).IsTrue();
        }
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DirectDocumentReadDoesNotRecheckAfterFileOrCloseFailure(bool closing, bool exact)
    {
        var (session, reader) = await DirectDocumentReader();
        session.Chunks.AddRange([new byte[] { 1 }, Array.Empty<byte>()]);
        var original = new FederationException(FederationErrorCode.Unavailable, "File access failed.");
        if (closing) session.CloseFailure = original;
        else session.ReadFailure = original;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, exact ? "v1" : null, ResourceVersionsType));

        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(session.JsonValueReads.Count(node =>
                node.IdentifierAsString == (exact ? "version:Epoch" : "logical:Epoch")))
            .IsEqualTo(1);
        await Assert.That(session.JsonValueReads.Count(node => node.IdentifierAsString == "logical:MetaEpoch"))
            .IsEqualTo(exact ? 0 : 1);
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectDocumentReadObservesCancellationDuringInitialMetadataBeforeOpen(bool meta)
    {
        var (session, reader) = await DirectDocumentReader();
        using var cancellation = new CancellationTokenSource();
        session.BeforeJsonRead = node =>
        {
            if (node.IdentifierAsString == (meta ? "logical:MetaEpoch" : "logical:Epoch")) cancellation.Cancel();
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, cancellationToken: cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.LastOpenedFile).IsEqualTo(File);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReader Reader)> DirectDocumentReader()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([ResourceReferencePage()]);
        return (session, reader);
    }

    [Test]
    [Arguments(true, null, 0)]
    [Arguments(true, null, 99)]
    [Arguments(true, null, 100)]
    [Arguments(true, "", 3)]
    [Arguments(true, "v2", 3)]
    [Arguments(false, null, 0)]
    [Arguments(false, null, 99)]
    [Arguments(false, null, 100)]
    [Arguments(false, "", 3)]
    [Arguments(false, "v2", 3)]
    public async Task CreateOrReplaceDocumentValidatesExactVersionAndWritesCompleteReplacement(
        bool created, string? versionId, int length)
    {
        var (session, writer) = await WriterForResource(maxBytes: 100, chunkSize: 64);
        session.Pages.Clear();
        session.MethodOutputs["GetOrCreateResource"] =
            [new Variant(VersionTwoNode), new Variant("v2"), new Variant(0u), new Variant(created)];
        session.AfterMethod = () => QueueDocumentSelection(session, explicitVersion: true);
        var browseStart = session.BrowsedNodes.Count;
        var operationStart = session.FileOperations.Count;
        var contents = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();

        var result = await writer.CreateOrReplaceResourceDocumentAsync(GroupNode, "/teams/team-a",
            "schemas", "schema-a", ResourceType, ResourceVersionsType, contents, versionId);

        await Assert.That(result.ResourceNode).IsEqualTo(VersionTwoNode);
        await Assert.That(result.VersionId).IsEqualTo("v2");
        await Assert.That(result.Created).IsEqualTo(created);
        await Assert.That(result.FileHandle).IsEqualTo(0u);
        var call = session.MethodCalls.Single();
        await Assert.That(call.Target).IsEqualTo(GroupNode);
        await Assert.That(call.Method).IsEqualTo("GetOrCreateResource");
        await Assert.That(call.Arguments[0].TryGetValue(out string? resource) && resource == "schema-a").IsTrue();
        await Assert.That(call.Arguments[1].TryGetValue(out string? version) &&
            version == (versionId ?? string.Empty)).IsTrue();
        await Assert.That(call.Arguments[2].TryGetValue(out bool requestOpen) && !requestOpen).IsTrue();
        await Assert.That(NodeIdTrace(session.BrowsedNodes.Skip(browseStart)))
            .IsEqualTo("group-team-a|logical-schema-a|schema-a-versions|schema-a-v2");
        await Assert.That(session.Opened).IsEqualTo(2);
        await Assert.That(session.LastOpenedFile).IsEqualTo(VersionTwoNode);
        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.WrittenChunks.SelectMany(chunk => chunk).ToArray()).IsEquivalentTo(contents);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo((length + 63) / 64);
        await Assert.That(session.WrittenFiles.All(file => file == VersionTwoNode)).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(VersionTwoNode);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(string.Join("|", session.FileOperations.Skip(operationStart)))
            .IsEqualTo("Open:6|Position:7|" + string.Concat(Enumerable.Repeat("Write:7|", (length + 63) / 64)) +
                "Close:7");
    }

    [Test]
    [Arguments("node", FederationErrorCode.InvalidPackage)]
    [Arguments("resource-id", FederationErrorCode.InvalidPackage)]
    [Arguments("version-id", FederationErrorCode.InvalidPackage)]
    [Arguments("xid", FederationErrorCode.InvalidPackage)]
    [Arguments("missing-resource", FederationErrorCode.NotFound)]
    [Arguments("missing-versions", FederationErrorCode.NotFound)]
    [Arguments("missing-version", FederationErrorCode.NotFound)]
    [Arguments("wrong-type", FederationErrorCode.NotFound)]
    [Arguments("duplicate-version", FederationErrorCode.Ambiguous)]
    public async Task CreateOrReplaceDocumentRejectsUnverifiedTargetsBeforeOpen(
        string malformed, FederationErrorCode code)
    {
        var (session, writer) = await WriterForResource();
        session.Pages.Clear();
        session.MethodOutputs["GetOrCreateResource"] =
        [
            new Variant(malformed == "node" ? DefaultVersionNode : VersionTwoNode),
            new Variant("v2"), new Variant(0u), new Variant(false)
        ];
        session.AfterMethod = () =>
        {
            QueueDocumentSelection(session, explicitVersion: true);
            if (malformed == "missing-resource")
            {
                session.BrowsePages[GroupNode] = new([new([])]);
            }
            if (malformed == "missing-versions")
            {
                session.BrowsePages[LogicalResourceNode] = new([new([])]);
            }
            if (malformed is "missing-version" or "wrong-type" or "duplicate-version")
            {
                var candidates = new List<OpcUaReference>();
                if (malformed != "missing-version")
                {
                    candidates.Add(TypedObjectRef("v2", VersionTwoNode,
                        malformed == "wrong-type" ? ObjectTypeIds.BaseObjectType : ResourceType));
                }
                if (malformed == "duplicate-version")
                {
                    candidates.Add(TypedObjectRef("v2", DefaultVersionNode, ResourceType));
                }
                session.BrowsePages[VersionsNode] = new([new(candidates)]);
            }
        };
        if (malformed is "resource-id" or "version-id" or "xid")
        {
            var property = malformed == "resource-id" ? "ResourceId" :
                malformed == "version-id" ? "VersionId" : "Xid";
            session.JsonValues["v2:" + property] = Value("\"different\"", BuiltInType.String);
        }

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CreateOrReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, ResourceVersionsType, new byte[] { 1 }, "v2"));

        await Assert.That(error!.Code).IsEqualTo(code);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo("GetOrCreateResource");
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("limit")]
    [Arguments("metadata")]
    [Arguments("collection")]
    [Arguments("group")]
    [Arguments("resource-type")]
    [Arguments("versions-type")]
    [Arguments("resource-id")]
    public async Task CreateOrReplaceDocumentRejectsBeforeCreation(string invalid)
    {
        var (session, writer) = await WriterForResource(
            invalid == "metadata"
                ? """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}"""
                : TestModel,
            maxBytes: 256);
        var browsed = session.BrowsedNodes.Count;
        using var cancellation = new CancellationTokenSource();
        if (invalid == "cancel")
        {
            cancellation.Cancel();
        }

        async Task Invoke() => _ = await writer.CreateOrReplaceResourceDocumentAsync(
            invalid == "group" ? NodeId.Null : GroupNode, "/teams/team-a",
            invalid == "collection" ? "unknown" : "schemas", invalid == "resource-id" ? "" : "schema-a",
            invalid == "resource-type" ? NodeId.Null : ResourceType,
            invalid == "versions-type" ? NodeId.Null : ResourceVersionsType,
            new byte[invalid == "limit" ? 257 : 1], cancellationToken: cancellation.Token);

        if (invalid == "cancel")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(Invoke);
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else if (invalid is "group" or "resource-type" or "versions-type" or "resource-id")
        {
            _ = await Assert.ThrowsAsync<ArgumentException>(Invoke);
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(Invoke);
            await Assert.That(error!.Code).IsEqualTo(invalid == "metadata" ? FederationErrorCode.UnsupportedOperation :
                invalid == "limit" ? FederationErrorCode.LimitExceeded : FederationErrorCode.NotFound);
        }
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(browsed);
        await Assert.That(session.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CreateOrReplaceDocumentRequiresAcceptedSpecVersionAndCompiledModel(bool specAccepted)
    {
        var session = new FakeSession();
        var reader = specAccepted ? await ValidatedReader(session) : new OpcUaRegistryReader(session);
        var writer = new OpcUaRegistryWriter(reader);
        var browsed = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CreateOrReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, ResourceVersionsType, new byte[] { 1 }));

        await Assert.That(error!.Code).IsEqualTo(specAccepted
            ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.UnsupportedVersion);
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(browsed);
        await Assert.That(session.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(session.Opened).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    [Arguments("method")]
    [Arguments("position")]
    [Arguments("write")]
    [Arguments("close")]
    public async Task CreateOrReplaceDocumentRetainsNativeFailureWithoutRetry(string stage)
    {
        var failure = new FederationException(FederationErrorCode.PolicyDenied, "Denied.",
            "Retained diagnostic.", new ServiceResultException(StatusCodes.BadUserAccessDenied));
        var cleanup = new InvalidOperationException("close");
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            MethodFailure = stage == "method" ? failure : null,
            PositionFailure = stage == "position" ? failure : null,
            WriteFailure = stage == "write" ? failure : null,
            CloseFailure = stage == "close" ? failure : stage is "position" or "write" ? cleanup : null
        });
        session.Pages.Clear();
        session.MethodOutputs["GetOrCreateResource"] =
            [new Variant(VersionTwoNode), new Variant("v2"), new Variant(0u), new Variant(true)];
        session.AfterMethod = () => QueueDocumentSelection(session, explicitVersion: true);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CreateOrReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, ResourceVersionsType, new byte[] { 1 }));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(error.Diagnostic).IsEqualTo("Retained diagnostic.");
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo("GetOrCreateResource");
        await Assert.That(session.Opened).IsEqualTo(stage == "method" ? 1 : 2);
        await Assert.That(session.Closed).IsEqualTo(stage == "method" ? 1 : 2);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(stage == "close" ? 1 : 0);
        if (stage is "position" or "write")
        {
            await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], cleanup)).IsTrue();
        }
    }

    [Test]
    public async Task CreateOrReplaceDocumentObservesCancellationAfterCreationBeforeBrowse()
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        session.MethodOutputs["GetOrCreateResource"] =
            [new Variant(VersionTwoNode), new Variant("v2"), new Variant(0u), new Variant(true)];
        session.AfterMethod = cancellation.Cancel;
        var browsed = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await writer.CreateOrReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, ResourceVersionsType, new byte[] { 1 }, cancellationToken: cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.MethodCalls.Count).IsEqualTo(1);
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(browsed);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DocumentWritePreCancellationAvoidsResolutionAndOpen(bool resource)
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var browsed = session.BrowsedNodes.Count;
        var opened = session.Opened;
        var closed = session.Closed;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (resource)
            {
                await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, new byte[] { 1 }, cancellationToken: cancellation.Token);
            }
            else
            {
                await writer.ReplaceFileDocumentAsync(File, new byte[] { 1 }, cancellation.Token);
            }
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.BrowsedNodes.Count).IsEqualTo(browsed);
        await Assert.That(session.Opened).IsEqualTo(opened);
        await Assert.That(session.Closed).IsEqualTo(closed);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("file", "open", false)]
    [Arguments("file", "position", false)]
    [Arguments("file", "first", false)]
    [Arguments("file", "final", false)]
    [Arguments("file", "empty", false)]
    [Arguments("resource", "open", false)]
    [Arguments("resource", "position", false)]
    [Arguments("resource", "first", false)]
    [Arguments("resource", "final", false)]
    [Arguments("resource", "empty", false)]
    [Arguments("created", "entry", false)]
    [Arguments("created", "closed", false)]
    [Arguments("created", "open", false)]
    [Arguments("created", "position", false)]
    [Arguments("created", "first", false)]
    [Arguments("created", "final", false)]
    [Arguments("created", "empty", false)]
    [Arguments("file", "open", true)]
    [Arguments("file", "first", true)]
    [Arguments("file", "final", true)]
    [Arguments("created", "entry", true)]
    public async Task DocumentWriteCancellationStopsChunksAndClosesTransferredHandle(
        string operation, string phase, bool closeFails)
    {
        var (session, writer) = await WriterForResource(chunkSize: 2);
        using var cancellation = new CancellationTokenSource();
        var closeFailure = closeFails ? new InvalidOperationException("close") : null;
        session.CloseFailure = closeFailure;
        session.AfterOpen = phase == "open" ? cancellation.Cancel : null;
        session.AfterPosition = phase is "position" or "empty" ? cancellation.Cancel : null;
        session.AfterClose = phase == "closed" ? cancellation.Cancel : null;
        session.AfterWrite = () =>
        {
            if (phase == "first" || phase == "final" && session.WrittenChunks.Count == 2)
            {
                cancellation.Cancel();
            }
        };
        if (phase == "entry")
        {
            cancellation.Cancel();
        }
        ReadOnlyMemory<byte> bytes = phase == "empty" ? ReadOnlyMemory<byte>.Empty : new byte[] { 1, 2, 3 };
        var closed = session.Closed;
        var positioned = session.PositionedFiles.Count;
        var opened = session.Opened;
        var file = operation == "resource" ? LogicalResourceNode : File;
        var expectedHandle = operation == "created" && phase is "entry" or "closed" ? 42u : 7u;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (operation == "resource")
            {
                await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ResourceType, bytes, cancellationToken: cancellation.Token);
            }
            else if (operation == "created")
            {
                await writer.CompleteCreatedResourceDocumentAsync(file, 42, bytes, cancellation.Token);
            }
            else
            {
                await writer.ReplaceFileDocumentAsync(file, bytes, cancellation.Token);
            }
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.Opened).IsEqualTo(opened + (phase is "entry" or "closed" ? 0 : 1));
        await Assert.That(session.PositionedFiles.Count)
            .IsEqualTo(positioned + (phase is "entry" or "closed" or "open" ? 0 : 1));
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(phase == "first" ? 1 : phase == "final" ? 2 : 0);
        if (session.WrittenChunks.Count > 0)
        {
            await Assert.That(session.WrittenChunks[0]).IsEquivalentTo(new byte[] { 1, 2 });
        }
        if (phase == "final")
        {
            await Assert.That(session.WrittenChunks[1]).IsEquivalentTo(new byte[] { 3 });
        }
        await Assert.That(session.Closed)
            .IsEqualTo(closed + (operation == "created" && phase is not ("entry" or "closed") ? 2 : 1));
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(file);
        await Assert.That(session.LastClosedHandle).IsEqualTo(expectedHandle);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CreatedDocumentOriginalCloseFailurePreventsReplacementOpen(bool denied)
    {
        var (session, writer) = await WriterForResource();
        Exception closeFailure = denied
            ? new FederationException(FederationErrorCode.PolicyDenied, "denied")
            : new InvalidOperationException("close");
        session.CloseFailure = closeFailure;
        var opened = session.Opened;
        var closed = session.Closed;
        var positioned = session.PositionedFiles.Count;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CompleteCreatedResourceDocumentAsync(File, 42, new byte[] { 1 }));

        await Assert.That(error!.Code)
            .IsEqualTo(denied ? FederationErrorCode.PolicyDenied : FederationErrorCode.Unavailable);
        await Assert.That(ReferenceEquals(denied ? error : error.InnerException, closeFailure)).IsTrue();
        await Assert.That(session.Opened).IsEqualTo(opened);
        await Assert.That(session.PositionedFiles.Count).IsEqualTo(positioned);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(closed + 1);
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
    }

    [Test]
    [Arguments(0)]
    [Arguments(99)]
    [Arguments(100)]
    public async Task CreatedDocumentReplacementReopensWithTruncationAtAllowedByteBudgets(int length)
    {
        var (session, writer) = await WriterForResource(maxBytes: 100, chunkSize: 64);
        var file = new NodeId("Created", 1);
        var contents = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();

        await writer.CompleteCreatedResourceDocumentAsync(file, 42, contents);

        await Assert.That(session.LastOpenedFile).IsEqualTo(file);
        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.WrittenChunks.SelectMany(chunk => chunk).ToArray()).IsEquivalentTo(contents);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.FileClosedHandles[^2]).IsEqualTo((file, 42u));
        await Assert.That(session.FileClosedHandles[^1]).IsEqualTo((file, 7u));
        await Assert.That(session.Closed).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CreatedDocumentLimitRejectionStillClosesTransferredHandle(bool closeFails)
    {
        var (session, writer) = await WriterForResource(maxBytes: 100);
        var closeFailure = closeFails ? new InvalidOperationException("close") : null;
        session.CloseFailure = closeFailure;
        var opened = session.Opened;
        var closed = session.Closed;
        var positioned = session.PositionedFiles.Count;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CompleteCreatedResourceDocumentAsync(File, 42, new byte[101]));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Opened).IsEqualTo(opened);
        await Assert.That(session.PositionedFiles.Count).IsEqualTo(positioned);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(closed + 1);
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(File);
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
    }

    [Test]
    public async Task ResourceDocumentWriteCancellationStillClosesFile()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            WriteFailure = new OperationCanceledException()
        });

        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments("v2")]
    public async Task ReaderDocumentOperationsRequireHasDocumentSupport(string? versionId)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session, """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, versionId));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.LastOpenedFile).IsEqualTo(File);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");

        var (writerSession, writer) = await WriterForResource(
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");
        var writeError = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, new byte[] { 1 }, versionId));

        await Assert.That(writeError!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(writerSession.Opened).IsEqualTo(1);
        await Assert.That(writerSession.LastOpenedFile).IsEqualTo(File);
        await Assert.That(NodeIdTrace(writerSession.BrowsedNodes)).IsEqualTo("Model|Model");
    }

    [Test]
    public async Task ResourceDocumentWriteErasesThenWritesBoundedChunksAndCloses()
    {
        var (session, writer) = await WriterForResource(chunkSize: 2);

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, new byte[] { 1, 2, 3 });

        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(2);
        await Assert.That(session.WrittenChunks[0]).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(session.WrittenChunks[1]).IsEquivalentTo(new byte[] { 3 });
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteClosesEmptyReplacementWithoutWritingChunks()
    {
        var (session, writer) = await WriterForResource();

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, ReadOnlyMemory<byte>.Empty);

        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteRejectsResourceWithoutDocumentBeforeBrowsing()
    {
        var (session, writer) = await WriterForResource(
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Pages.Count).IsEqualTo(2);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task ResourceDocumentWriteRejectsOversizeBeforeResolvingOrOpeningFile()
    {
        var (session, writer) = await WriterForResource(maxBytes: 100);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[101]));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Pages.Count).IsEqualTo(2);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task ResourceDocumentWriteFailureIsRetainedWhenCloseAlsoFails()
    {
        var writeFailure = new InvalidOperationException("write");
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            WriteFailure = writeFailure,
            CloseFailure = new InvalidOperationException("close")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Message).IsEqualTo("write");
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWritePositionFailureStillClosesFile()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            PositionFailure = new InvalidOperationException("position")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Message).IsEqualTo("position");
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteCloseFailureAfterSuccessIsReported()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            CloseFailure = new InvalidOperationException("close")
        });

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task WriterCompletedCreatedDocumentClosesOriginalHandleAndReopensWithTruncation()
    {
        var (session, writer) = await WriterForResource();
        var start = session.FileOperations.Count;

        await writer.CompleteCreatedResourceDocumentAsync(new NodeId("schema-a", 1), 42,
            new byte[] { 4, 5 });

        await Assert.That(session.Opened).IsEqualTo(2);
        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 4, 5 });
        await Assert.That(session.LastPositionFile).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.LastPositionHandle).IsEqualTo(7u);
        await Assert.That(session.LastWrittenFile).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.LastWrittenHandle).IsEqualTo(7u);
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.FileClosedHandles[^2]).IsEqualTo((new NodeId("schema-a", 1), 42u));
        await Assert.That(session.LastClosedHandle).IsEqualTo(7u);
        await Assert.That(session.Closed).IsEqualTo(3);
        await Assert.That(string.Join("|", session.FileOperations.Skip(start)))
            .IsEqualTo("Close:42|Open:6|Position:7|Write:7|Close:7");
    }

    [Test]
    public async Task PropertyWriteUsesTheDeclaredBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        await writer.WritePropertyAsync(new NodeId("Epoch", 1), Value("17", BuiltInType.UInt32));

        await Assert.That(session.WrittenValues.Count).IsEqualTo(1);
        await Assert.That(session.WrittenValues[0].Variable).IsEqualTo(new NodeId("Epoch", 1));
        await Assert.That(session.WrittenValues[0].Value.TryGetValue(out uint value) && value == 17).IsTrue();
    }

    [Test]
    public async Task PropertyWriteMapsEverySupportedScalarBuiltInType()
    {
        var (session, writer) = await WriterForResource();
        var timestamp = new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc);
        var cases = new (BuiltInType Type, string Json, object Expected)[]
        {
            (BuiltInType.String, "\"text\"", "text"),
            (BuiltInType.Boolean, "true", true),
            (BuiltInType.SByte, "-3", (sbyte)-3),
            (BuiltInType.Byte, "3", (byte)3),
            (BuiltInType.Int16, "-4", (short)-4),
            (BuiltInType.UInt16, "4", (ushort)4),
            (BuiltInType.Int32, "-5", -5),
            (BuiltInType.UInt32, "5", 5u),
            (BuiltInType.Int64, "-6", -6L),
            (BuiltInType.UInt64, "6", 6UL),
            (BuiltInType.Float, "1.25", 1.25f),
            (BuiltInType.Double, "1.25", 1.25d),
            (BuiltInType.DateTime, "\"2026-01-03T03:04:05Z\"", timestamp)
        };

        foreach (var (type, json, expected) in cases)
        {
            await writer.WritePropertyAsync(new NodeId("Value", 1), Value(json, type));
            await Assert.That(session.WrittenValues[^1].Value.AsBoxedObject()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task PropertyWriteRejectsScalarThatDoesNotMatchItsBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("Epoch", 1), Value("\"17\"", BuiltInType.UInt32)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PropertyWriteRejectsScalarOutsideItsBuiltInRange()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("ByteValue", 1), Value("256", BuiltInType.Byte)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PropertyWriteRejectsUnsupportedBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("ByteString", 1), Value("\"data\"", BuiltInType.ByteString)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WriterCreateAndGetOrCreatePreserveOutputsAndValidateTypes()
    {
        var (session, writer) = await WriterForResource();
        session.MethodOutputs["CreateGroup"] = [new Variant(new NodeId("team-a", 1))];
        session.MethodOutputs["GetOrCreateGroup"] =
            [new Variant(new NodeId("team-b", 1)), new Variant(true)];
        session.MethodOutputs["CreateResource"] =
            [new Variant(new NodeId("schema-a", 1)), new Variant("v1"), new Variant(42u)];
        session.MethodOutputs["GetOrCreateResource"] =
            [new Variant(new NodeId("schema-b", 1)), new Variant("v2"), new Variant(43u), new Variant(false)];

        var createdGroup = await writer.CreateGroupAsync(File, "team-a");
        var resolvedGroup = await writer.GetOrCreateGroupAsync(File, "team-b");
        var createdResource = await writer.CreateResourceAsync(new NodeId("team-a", 1), "schema-a", "v1", true);
        var resolvedResource = await writer.GetOrCreateResourceAsync(
            new NodeId("team-a", 1), "schema-b", "v2", true);

        await Assert.That(createdGroup).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(resolvedGroup.GroupNode).IsEqualTo(new NodeId("team-b", 1));
        await Assert.That(resolvedGroup.Created).IsTrue();
        await Assert.That(createdResource.ResourceNode).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(createdResource.VersionId).IsEqualTo("v1");
        await Assert.That(createdResource.FileHandle).IsEqualTo(42u);
        await Assert.That(resolvedResource.ResourceNode).IsEqualTo(new NodeId("schema-b", 1));
        await Assert.That(resolvedResource.VersionId).IsEqualTo("v2");
        await Assert.That(resolvedResource.FileHandle).IsEqualTo(43u);
        await Assert.That(resolvedResource.Created).IsFalse();
        await Assert.That(session.MethodCalls.Count).IsEqualTo(4);
        await Assert.That(session.MethodCalls[0].Target).IsEqualTo(File);
        await Assert.That(session.MethodCalls[1].Target).IsEqualTo(File);
        await Assert.That(session.MethodCalls[2].Target).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(session.MethodCalls[3].Target).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(session.MethodCalls[0].Method).IsEqualTo("CreateGroup");
        await Assert.That(session.MethodCalls[1].Method).IsEqualTo("GetOrCreateGroup");
        await Assert.That(session.MethodCalls[2].Method).IsEqualTo("CreateResource");
        await Assert.That(session.MethodCalls[3].Method).IsEqualTo("GetOrCreateResource");
        await Assert.That(session.MethodCalls[2].Arguments.Count).IsEqualTo(3);
        await Assert.That(session.MethodCalls[2].Arguments[0].TryGetValue(out string? createResourceId) &&
            createResourceId == "schema-a").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[1].TryGetValue(out string? createVersionId) &&
            createVersionId == "v1").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[2].TryGetValue(out bool requestOpen) && requestOpen).IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[0].TryGetValue(out string? resolveResourceId) &&
            resolveResourceId == "schema-b").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[1].TryGetValue(out string? resolveVersionId) &&
            resolveVersionId == "v2").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[2].TryGetValue(out bool resolveOpen) &&
            resolveOpen).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ResourceCreationObservesCancellationBeforeCallingSession(bool getOrCreate, bool requestOpen)
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (getOrCreate)
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1", requestOpen, cancellation.Token);
            else
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1", requestOpen, cancellation.Token);
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, 0u, false)]
    [Arguments(true, 0u, false)]
    [Arguments(false, 42u, false)]
    [Arguments(true, 42u, false)]
    [Arguments(false, uint.MaxValue, false)]
    [Arguments(true, uint.MaxValue, false)]
    [Arguments(false, 42u, true)]
    [Arguments(true, 42u, true)]
    public async Task ResourceCreationClosesReturnedHandleAfterCancellationWithoutLosingCleanupFailure(
        bool getOrCreate, uint handle, bool closeFails)
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        session.MethodOutputs[method] = getOrCreate
            ? [new Variant(VersionTwoNode), new Variant("v1"), new Variant(handle), new Variant(false)]
            : [new Variant(VersionTwoNode), new Variant("v1"), new Variant(handle)];
        session.AfterMethod = cancellation.Cancel;
        var cleanup = new FederationException(FederationErrorCode.Unavailable, "Close failed.");
        if (closeFails) session.CloseFailure = cleanup;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (getOrCreate)
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1", true, cancellation.Token);
            else
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1", true, cancellation.Token);
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.ClosedFiles[^1]).IsEqualTo(VersionTwoNode);
        await Assert.That(session.LastClosedHandle).IsEqualTo(handle);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFails ? cleanup : null)).IsTrue();
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.PositionedFiles.Count).IsEqualTo(0);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResourceCreationCancellationWithoutRequestedHandleDoesNotClosePlaceholder(bool getOrCreate)
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        session.MethodOutputs[method] = getOrCreate
            ? [new Variant(VersionTwoNode), new Variant("v1"), new Variant(0u), new Variant(true)]
            : [new Variant(VersionTwoNode), new Variant("v1"), new Variant(0u)];
        session.AfterMethod = cancellation.Cancel;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (getOrCreate)
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1", false, cancellation.Token);
            else
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1", false, cancellation.Token);
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.ClosedFiles.Single()).IsEqualTo(File);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResourceCreationRetainsMalformedOutputErrorOverLaterCancellation(bool getOrCreate)
    {
        var (session, writer) = await WriterForResource();
        using var cancellation = new CancellationTokenSource();
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        session.MethodOutputs[method] = getOrCreate
            ? [new Variant(VersionTwoNode), new Variant("wrong"), new Variant(42u), new Variant(true)]
            : [new Variant(VersionTwoNode), new Variant("wrong"), new Variant(42u)];
        session.AfterMethod = cancellation.Cancel;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            if (getOrCreate)
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1", true, cancellation.Token);
            else
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1", true, cancellation.Token);
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, null, "")]
    [Arguments(true, null, "")]
    [Arguments(false, "", "")]
    [Arguments(true, "", "")]
    [Arguments(false, "v1", "v2")]
    [Arguments(true, "v1", "v2")]
    [Arguments(false, "v1", "V1")]
    [Arguments(true, "v1", "V1")]
    public async Task ResourceCreationRejectsEmptyOrContradictoryAssignedVersion(
        bool getOrCreate, string? requestedVersion, string assignedVersion)
    {
        var (session, writer) = await WriterForResource();
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        session.MethodOutputs[method] = getOrCreate
            ? [new Variant(VersionTwoNode), new Variant(assignedVersion), new Variant(0u), new Variant(false)]
            : [new Variant(VersionTwoNode), new Variant(assignedVersion), new Variant(0u)];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            if (getOrCreate)
            {
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", requestedVersion);
            }
            else
            {
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", requestedVersion);
            }
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
        await Assert.That(session.MethodCalls.Single().Target).IsEqualTo(GroupNode);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, "version", false)]
    [Arguments(true, "version", false)]
    [Arguments(false, "version", true)]
    [Arguments(true, "version", true)]
    [Arguments(false, "count", false)]
    [Arguments(true, "count", false)]
    [Arguments(false, "count", true)]
    [Arguments(true, "count", true)]
    [Arguments(false, "type", false)]
    [Arguments(true, "type", false)]
    [Arguments(true, "created", false)]
    [Arguments(true, "created", true)]
    [Arguments(true, "missing", false)]
    [Arguments(true, "missing", true)]
    public async Task ResourceCreationClosesRequestedHandleWhenOutputsAreRejected(
        bool getOrCreate, string malformed, bool closeFails)
    {
        var (session, writer) = await WriterForResource();
        var closeFailure = closeFails ? new InvalidOperationException("close") : null;
        session.CloseFailure = closeFailure;
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        var outputs = new List<Variant>
        {
            new(VersionTwoNode),
            malformed == "type" ? new Variant(42u) : new Variant(malformed == "version" ? "v2" : "v1"),
            new(42u)
        };
        if (getOrCreate && malformed != "missing")
        {
            outputs.Add(malformed == "created" ? new Variant("not-a-bool") : new Variant(true));
        }
        if (malformed == "count")
        {
            outputs.Add(new Variant("unexpected"));
        }
        session.MethodOutputs[method] = outputs;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            if (getOrCreate)
            {
                _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1", true);
            }
            else
            {
                _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1", true);
            }
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Closed).IsEqualTo(2);
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(VersionTwoNode);
        await Assert.That(session.CloseToken.CanBeCanceled).IsFalse();
        await Assert.That(ReferenceEquals(error.Data["OpcUa.FileCloseFailure"], closeFailure)).IsTrue();
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.PositionedFiles.Count).IsEqualTo(0);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
    }

    [Test]
    [Arguments(false, null, "server-assigned")]
    [Arguments(true, null, "server-assigned")]
    [Arguments(false, "", "server-assigned")]
    [Arguments(true, "", "server-assigned")]
    [Arguments(false, "v1", "v1")]
    [Arguments(true, "v1", "v1")]
    public async Task ResourceCreationPreservesAssignedOrExplicitVersionIdentity(
        bool getOrCreate, string? requestedVersion, string assignedVersion)
    {
        var (session, writer) = await WriterForResource();
        var method = getOrCreate ? "GetOrCreateResource" : "CreateResource";
        session.MethodOutputs[method] = getOrCreate
            ? [new Variant(VersionTwoNode), new Variant(assignedVersion), new Variant(0u), new Variant(true)]
            : [new Variant(VersionTwoNode), new Variant(assignedVersion), new Variant(0u)];

        if (getOrCreate)
        {
            var result = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", requestedVersion);
            await Assert.That(result.ResourceNode).IsEqualTo(VersionTwoNode);
            await Assert.That(result.VersionId).IsEqualTo(assignedVersion);
            await Assert.That(result.FileHandle).IsEqualTo(0u);
            await Assert.That(result.Created).IsTrue();
        }
        else
        {
            var result = await writer.CreateResourceAsync(GroupNode, "schema-a", requestedVersion);
            await Assert.That(result.ResourceNode).IsEqualTo(VersionTwoNode);
            await Assert.That(result.VersionId).IsEqualTo(assignedVersion);
            await Assert.That(result.FileHandle).IsEqualTo(0u);
        }
        var call = session.MethodCalls.Single();
        await Assert.That(call.Method).IsEqualTo(method);
        await Assert.That(call.Target).IsEqualTo(GroupNode);
        await Assert.That(call.Arguments[1].TryGetValue(out string? inputVersion) &&
            inputVersion == (requestedVersion ?? string.Empty)).IsTrue();
        await Assert.That(call.Arguments[2].TryGetValue(out bool requestOpen) && !requestOpen).IsTrue();
    }

    [Test]
    public async Task GroupCreationRejectsMalformedMethodOutput()
    {
        var (session, writer) = await WriterForResource();
        session.MethodOutputs["CreateGroup"] = [new Variant(42u)];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CreateGroupAsync(File, "team-a"));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo("CreateGroup");
    }

    [Test]
    public async Task WriterMetaAttributesTargetLogicalResource()
    {
        var (session, writer) = await WriterForResource();
        var entity = LogicalResourceNode;
        var logicalMetaLabels = new NodeId("logical-meta-labels", 1);
        var logicalLabels = new NodeId("logical-labels", 1);
        var exactVersionLabels = new NodeId("default-version-labels", 1);

        await writer.DeleteAsync(entity);
        await writer.DeleteAsync(entity, 5);
        await writer.AddAttributeAsync(logicalLabels, "team", "alpha", 6);
        await writer.RemoveAttributeAsync(exactVersionLabels, "team", 7);
        await writer.AddMetaAttributeAsync(logicalMetaLabels, "region", "west", 8);
        await writer.RemoveMetaAttributeAsync(logicalMetaLabels, "region", 9);

        await Assert.That(session.MethodCalls.Count).IsEqualTo(6);
        await Assert.That(session.MethodCalls[0].Method).IsEqualTo("Delete");
        await Assert.That(session.MethodCalls[0].Target).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.MethodCalls[0].Arguments.Count).IsEqualTo(1);
        await Assert.That(session.MethodCalls[0].Arguments[0].TryGetValue(out uint omittedEpoch) &&
            omittedEpoch == 0u).IsTrue();
        await Assert.That(session.MethodCalls[1].Method).IsEqualTo("Delete");
        await Assert.That(session.MethodCalls[1].Target).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.MethodCalls[1].Arguments[0].TryGetValue(out uint deleteEpoch) &&
            deleteEpoch == 5).IsTrue();
        await Assert.That(session.MethodCalls[2].Method).IsEqualTo("AddAttribute");
        await Assert.That(session.MethodCalls[2].Target).IsEqualTo(logicalLabels);
        await Assert.That(session.MethodCalls[2].Arguments[0].TryGetValue(out string? addKey) &&
            addKey == "team").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[1].TryGetValue(out string? addValue) &&
            addValue == "alpha").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[2].TryGetValue(out uint addEpoch) &&
            addEpoch == 6).IsTrue();
        await Assert.That(session.MethodCalls[3].Method).IsEqualTo("RemoveAttribute");
        await Assert.That(session.MethodCalls[3].Target).IsEqualTo(exactVersionLabels);
        await Assert.That(session.MethodCalls[3].Arguments[0].TryGetValue(out string? removeKey) &&
            removeKey == "team").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[1].TryGetValue(out uint removeEpoch) &&
            removeEpoch == 7).IsTrue();
        await Assert.That(session.MethodCalls[4].Method).IsEqualTo("AddAttribute");
        await Assert.That(session.MethodCalls[4].Arguments[0].TryGetValue(out string? addMetaKey) &&
            addMetaKey == "region").IsTrue();
        await Assert.That(session.MethodCalls[4].Arguments[1].TryGetValue(out string? addMetaValue) &&
            addMetaValue == "west").IsTrue();
        await Assert.That(session.MethodCalls[4].Arguments[2].TryGetValue(out uint addMetaEpoch) &&
            addMetaEpoch == 8).IsTrue();
        await Assert.That(session.MethodCalls[5].Method).IsEqualTo("RemoveAttribute");
        await Assert.That(session.MethodCalls[5].Arguments[0].TryGetValue(out string? removeMetaKey) &&
            removeMetaKey == "region").IsTrue();
        await Assert.That(session.MethodCalls[5].Arguments[1].TryGetValue(out uint removeMetaEpoch) &&
            removeMetaEpoch == 9).IsTrue();
        await Assert.That(session.MethodCalls[4].Target).IsEqualTo(logicalMetaLabels);
        await Assert.That(session.MethodCalls[5].Target).IsEqualTo(logicalMetaLabels);
    }

    [Test]
    [Arguments(null)]
    [Arguments(0u)]
    [Arguments(1u)]
    [Arguments(52u)]
    [Arguments(uint.MaxValue)]
    public async Task WriterAlwaysSuppliesRequiredUInt32EpochOnDeleteAndLabelCalls(uint? expectedEpoch)
    {
        var (session, writer) = await WriterForResource();
        var epoch = expectedEpoch ?? 0u;
        var labels = new NodeId("Labels", 1);
        var metaLabels = new NodeId("MetaLabels", 1);

        await writer.DeleteAsync(LogicalResourceNode, expectedEpoch);
        await writer.AddAttributeAsync(labels, "key", "value", epoch);
        await writer.RemoveAttributeAsync(labels, "key", epoch);
        await writer.AddMetaAttributeAsync(metaLabels, "key", "value", epoch);
        await writer.RemoveMetaAttributeAsync(metaLabels, "key", epoch);

        var expected = new[]
        {
            (LogicalResourceNode, "Delete", 1),
            (labels, "AddAttribute", 3),
            (labels, "RemoveAttribute", 2),
            (metaLabels, "AddAttribute", 3),
            (metaLabels, "RemoveAttribute", 2)
        };
        await Assert.That(session.MethodCalls.Count).IsEqualTo(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var call = session.MethodCalls[index];
            await Assert.That(call.Target).IsEqualTo(expected[index].Item1);
            await Assert.That(call.Method).IsEqualTo(expected[index].Item2);
            await Assert.That(call.Arguments.Count).IsEqualTo(expected[index].Item3);
            await Assert.That(call.Arguments[^1].TryGetValue(out uint actual) && actual == epoch).IsTrue();
        }
    }

    [Test]
    [Arguments(0u)]
    [Arguments(11u)]
    public async Task WriterDeleteRetainsNativeEpochFailureWithoutRetry(uint expectedEpoch)
    {
        var native = new ServiceResultException(StatusCodes.BadInvalidState);
        var failure = new FederationException(FederationErrorCode.Unavailable,
            "Native Delete rejected.", "BadInvalidState", native);
        var session = new FakeSession { MethodFailure = failure };
        var reader = await ValidatedReader(session);
        var writer = new OpcUaRegistryWriter(reader);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.DeleteAsync(LogicalResourceNode, expectedEpoch));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(ReferenceEquals(error!.InnerException, native)).IsTrue();
        var call = session.MethodCalls.Single();
        await Assert.That(call.Target).IsEqualTo(LogicalResourceNode);
        await Assert.That(call.Method).IsEqualTo("Delete");
        await Assert.That(call.Arguments.Count).IsEqualTo(1);
        await Assert.That(call.Arguments[0].TryGetValue(out uint epoch) && epoch == expectedEpoch).IsTrue();
    }

    private static string NodeIdTrace(IEnumerable<NodeId> nodes) =>
        string.Join("|", nodes.Select(node => node.IdentifierAsString));

    private static OpcUaReference Ref(string name) => new(new NodeId(name, 1), new QualifiedName(name, 1));
    private static OpcUaReference SpecVersionRef() =>
        new(new NodeId("SpecVersion", 1), new QualifiedName("SpecVersion", 1), NodeClass.Variable);
    private static OpcUaReference GroupRef(string name, NodeId groupType) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, groupType);
    private static OpcUaReference TypedObjectRef(string name, NodeId objectType) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, objectType);
    private static OpcUaReference TypedObjectRef(string name, NodeId node, NodeId objectType) =>
        new(node, new QualifiedName(name, 1), NodeClass.Object, objectType);
    private static OpcUaReference VariableRef(string name) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Variable);
    private static OpcUaReference VariableRef(string name, string nodePrefix) =>
        new(new NodeId(nodePrefix + ":" + name, 1), new QualifiedName(name, 1), NodeClass.Variable);
    private static void QueueDocumentSelection(FakeSession session, bool explicitVersion)
    {
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        if (explicitVersion)
        {
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "logical"),
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
            ]));
            session.Pages.Enqueue(new([
                TypedObjectRef("v1", DefaultVersionNode, ResourceType),
                TypedObjectRef("v2", VersionTwoNode, ResourceType)
            ]));
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
                VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
                VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
                VariableRef("ModifiedAt", "v2")
            ]));
        }
        else
        {
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
                VariableRef("ModifiedAt", "logical")
            ]));
        }
    }
    private static OpcUaReference FileRef(string name) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, ObjectTypeIds.FileType);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static OpcUaJsonValue Value(string json, BuiltInType builtInType, string? dataTypeName = null) =>
        new(Json(json), builtInType, dataTypeName);

    private static async ValueTask<JsonElement> ReadMetaWithDeprecated(OpcUaJsonValue value)
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaDeprecated"] = value
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaDeprecated", "meta")
        ]));
        return await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
    }

    private static async ValueTask<OpcUaRegistryReader> ValidatedReader(FakeSession session,
        int maxBytes = 64 * 1024 * 1024, int chunkSize = 64 * 1024)
    {
        var reader = new OpcUaRegistryReader(session, maxBytes, chunkSize);
        session.Pages.Enqueue(new([SpecVersionRef()]));
        await reader.ReadSpecVersionAsync(File, _ => true);
        return reader;
    }

    private static async ValueTask<OpcUaRegistryReader> ModelReader(FakeSession session, string model = TestModel,
        int maxBytes = 64 * 1024 * 1024, int chunkSize = 64 * 1024)
    {
        var reader = new OpcUaRegistryReader(session, maxBytes, chunkSize);
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([FileRef("Model")]));
        var modelChunks = System.Text.Encoding.UTF8.GetBytes(model).Chunk(Math.Min(maxBytes, chunkSize)).ToList();
        modelChunks.Add([]);
        session.Chunks.InsertRange(0, modelChunks);
        await reader.ReadSpecVersionAsync(File, _ => true);
        _ = await reader.ReadModelDefinitionAsync(File);
        return reader;
    }

    private static FakeSession SourceSession(string model = TestModel, bool isRegistry = true,
        Exception? readFailure = null)
    {
        var session = new FakeSession { IsRegistry = isRegistry, ReadFailure = readFailure };
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes(model), Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("""{"flags":["doc"]}"""), Array.Empty<byte>()
        ]);
        session.JsonValues["RegistryId"] = Value("\"registry-a\"", BuiltInType.String);
        session.JsonValues["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String);
        session.JsonValues["Xid"] = Value("\"/\"", BuiltInType.String);
        session.JsonValues["Epoch"] = Value("1", BuiltInType.UInt32);
        session.JsonValues["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.BrowsePages[File] = new([
            new([SpecVersionRef()]),
            new([VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
                VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")]),
            new([FileRef("Model")]),
            new([FileRef("Capabilities")]),
            new([TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))])
        ]);
        return session;
    }

    private static void ConfigureResourceAddressSpace(FakeSession session, bool hasExternalReference = false)
    {
        session.BrowsePages[GroupNode] = new([
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage(),
            ResourceReferencePage(), GroupPropertiesPage(), ResourceReferencePage(), GroupPropertiesPage(),
            ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage()
        ]);
        session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
        session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
        session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
        session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
        session.BrowsePages[LogicalResourceNode] = new([
            new([
                VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
                VariableRef("Name", "logical"), VariableRef("Format", "logical"),
                VariableRef("ContentType", "logical"), VariableRef("MetaEpoch", "logical"),
                VariableRef("MetaCreatedAt", "logical"), VariableRef("MetaModifiedAt", "logical"),
                VariableRef("MetaName", "logical"), VariableRef("MetaDefaultVersionSticky", "logical"),
                VariableRef("MetaReadonly", "logical"), VariableRef("MetaCompatibility", "logical"),
                TypedObjectRef("Labels", new NodeId("logical-labels", 1), AttributesType),
                TypedObjectRef("MetaLabels", new NodeId("logical-meta-labels", 1), AttributesType),
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
            ])
        ]);
        if (hasExternalReference)
        {
            session.BrowsePages[LogicalResourceNode] = new([
                new([
                    VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                    VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                    VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
                    VariableRef("Name", "logical"), VariableRef("Format", "logical"),
                    VariableRef("ContentType", "logical"), VariableRef("MetaEpoch", "logical"),
                    VariableRef("ExternalReference", "logical"),
                    VariableRef("MetaCreatedAt", "logical"), VariableRef("MetaModifiedAt", "logical"),
                    VariableRef("MetaName", "logical"), VariableRef("MetaDefaultVersionSticky", "logical"),
                    VariableRef("MetaReadonly", "logical"), VariableRef("MetaCompatibility", "logical"),
                    TypedObjectRef("Labels", new NodeId("logical-labels", 1), AttributesType),
                    TypedObjectRef("MetaLabels", new NodeId("logical-meta-labels", 1), AttributesType),
                    TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
                ])
            ]);
        }
        session.JsonValues["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["logical:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String);
        session.JsonValues["logical:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:Name"] = Value("\"Schema A\"", BuiltInType.String);
        session.JsonValues["logical:Format"] = Value("\"json\"", BuiltInType.String);
        session.JsonValues["logical:ContentType"] = Value("\"application/json\"", BuiltInType.String);
        session.JsonValues["logical:MetaEpoch"] = Value("3", BuiltInType.UInt32);
        session.JsonValues["logical:MetaCreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:MetaModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:MetaName"] = Value("\"Schema A metadata\"", BuiltInType.String);
        session.JsonValues["logical:MetaDefaultVersionSticky"] = Value("false", BuiltInType.Boolean);
        session.JsonValues["logical:MetaReadonly"] = Value("false", BuiltInType.Boolean);
        session.JsonValues["logical:MetaCompatibility"] = Value("\"backward\"", BuiltInType.String);
        session.BrowsePages[new NodeId("logical-labels", 1)] =
            new([new([VariableRef("region", "logical-label")])]);
        session.BrowsePages[new NodeId("logical-meta-labels", 1)] =
            new([new([VariableRef("region", "logical-meta-label")])]);
        session.StringValues["logical-label:region"] = "resource-region";
        session.StringValues["logical-meta-label:region"] = "meta-region";
        session.BrowsePages[VersionsNode] =
            new([new([TypedObjectRef("v1", DefaultVersionNode, ResourceType)])]);
        session.BrowsePages[DefaultVersionNode] = new([
            new([
                VariableRef("ResourceId", "version"), VariableRef("VersionId", "version"),
                VariableRef("Xid", "version"), VariableRef("Epoch", "version"),
                VariableRef("CreatedAt", "version"), VariableRef("ModifiedAt", "version"),
                VariableRef("Name", "version"), VariableRef("Format", "version"),
                VariableRef("ContentType", "version"),
                TypedObjectRef("Labels", new NodeId("version-labels", 1), AttributesType)
            ])
        ]);
        session.JsonValues["version:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["version:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["version:Xid"] = Value(
            "\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String);
        session.JsonValues["version:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["version:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["version:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["version:Name"] = Value("\"Schema A v1\"", BuiltInType.String);
        session.JsonValues["version:Format"] = Value("\"json\"", BuiltInType.String);
        session.JsonValues["version:ContentType"] = Value("\"application/json\"", BuiltInType.String);
        session.BrowsePages[new NodeId("version-labels", 1)] = new([new([])]);
    }

    private static void ConfigureResourceCollectionWalk(FakeSession session) =>
        session.BrowsePages[GroupNode] = new([
            GroupPropertiesPage(), ResourceReferencePage(),
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage(),
            ResourceReferencePage(), GroupPropertiesPage(), ResourceReferencePage(),
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage()
        ]);

    private static OpcUaBrowsePage GroupPropertiesPage() => new([
        VariableRef("GroupId", "group"), VariableRef("Xid", "group"),
        VariableRef("Epoch", "group"), VariableRef("CreatedAt", "group"),
        VariableRef("ModifiedAt", "group"), VariableRef("Name", "group")
    ]);

    private static OpcUaBrowsePage ResourceReferencePage() => new([
        TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
    ]);

    private static async ValueTask<OpcUaRegistryReadSource> OpenSource(FakeSession session,
        OpcUaRegistryTypeMap? types = null)
    {
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        var context = new NativeRegistryContext("opcua", "opc.tcp://localhost:4840");
        types ??= new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = new(ResourceType, ResourceVersionsType)
            },
            AttributesType);
        return await OpcUaRegistryReadSource.OpenAsync(reader, File, context, types);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReadSource Source)> OpenExternalReferenceSource()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session, hasExternalReference: true);
        var logicalReference = new ExpandedNodeId(new NodeId(7u, 2), null, 2u);
        var versionReference = new ExpandedNodeId(new NodeId(42u, 2), null, 2u);
        session.ExpandedNodeIds["logical:ExternalReference"] = logicalReference;
        session.ExpandedNodeIds["version:ExternalReference"] = versionReference;
        session.ExternalNodeIdMappings[logicalReference] = new(
            "urn:logical:application", "urn:logical:namespace", IdType.Numeric, 7u);
        session.ExternalNodeIdMappings[versionReference] = new(
            "urn:remote:application", "urn:remote:namespace", IdType.Numeric, 42u);
        var versionProperties = new List<OpcUaReference>
        {
            VariableRef("ResourceId", "version"), VariableRef("VersionId", "version"),
            VariableRef("Xid", "version"), VariableRef("Epoch", "version"),
            VariableRef("CreatedAt", "version"), VariableRef("ModifiedAt", "version"),
            VariableRef("Name", "version"), VariableRef("Format", "version"),
            VariableRef("ContentType", "version"), VariableRef("ExternalReference", "version"),
            TypedObjectRef("Labels", new NodeId("version-labels", 1), AttributesType)
        };
        session.BrowsePages[DefaultVersionNode] = new([new(versionProperties)]);
        return (session, await OpenSource(session));
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryWriter Writer)> WriterForResource(
        string model = TestModel, int maxBytes = 64 * 1024 * 1024, int chunkSize = 64 * 1024,
        FakeSession? session = null)
    {
        session ??= new FakeSession();
        session.JsonValues["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["logical:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String);
        session.JsonValues["logical:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String);
        session.JsonValues["default:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["default:VersionId"] = Value("\"decoy-default\"", BuiltInType.String);
        session.JsonValues["default:Xid"] = Value("\"/decoy/default-version\"", BuiltInType.String);
        session.JsonValues["default:Epoch"] = Value("99", BuiltInType.UInt32);
        session.JsonValues["default:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["default:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["default:Name"] = Value("\"Do not use exact default node\"", BuiltInType.String);
        session.JsonValues["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["v2:VersionId"] = Value("\"v2\"", BuiltInType.String);
        session.JsonValues["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String);
        session.JsonValues["v2:Epoch"] = Value("5", BuiltInType.UInt32);
        session.JsonValues["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String);
        var closeFailure = session.CloseFailure;
        session.CloseFailure = null;
        var reader = await ModelReader(session, model, maxBytes, chunkSize);
        session.CloseFailure = closeFailure;
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
            VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
            VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical")
        ]));
        return (session, new OpcUaRegistryWriter(reader));
    }

    private sealed class FakeSession : IOpcUaRegistrySession
    {
        public List<byte[]> Chunks { get; } = [];
        public Queue<OpcUaBrowsePage> Pages { get; } = new();
        public Dictionary<NodeId, Queue<OpcUaBrowsePage>> BrowsePages { get; } = [];
        public List<NodeId> BrowsedNodes { get; } = [];
        public List<byte[]> Released { get; } = [];
        public Dictionary<string, OpcUaJsonValue> JsonValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> StringValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ExpandedNodeId> ExpandedNodeIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<ExpandedNodeId, OpcUaExternalNodeId> ExternalNodeIdMappings { get; } = [];
        public IReadOnlyList<string> ServerArray { get; init; } = ["urn:xregistry"];
        public IReadOnlyList<string> NamespaceArray { get; init; } =
            ["http://opcfoundation.org/UA/", "urn:xregistry"];
        public Dictionary<string, IReadOnlyList<Variant>> MethodOutputs { get; } = new(StringComparer.Ordinal);
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; init; }
        public Exception? WriteValueFailure { get; init; }
        public Exception? MethodFailure { get; init; }
        public Exception? PositionFailure { get; init; }
        public Exception? CloseFailure { get; set; }
        public Exception? BrowseNextFailure { get; init; }
        public Exception? ReleaseFailure { get; init; }
        public bool IsRegistry { get; init; } = true;
        public bool AdvanceMetadataRevisionOnOpen { get; set; }
        public Action? AfterOpen { get; set; }
        public Action? AfterRead { get; set; }
        public Action<NodeId>? BeforeJsonRead { get; set; }
        public List<NodeId> JsonValueReads { get; } = [];
        public Action? AfterPosition { get; set; }
        public Action? AfterWrite { get; set; }
        public Action? AfterClose { get; set; }
        public Action? BeforeClose { get; set; }
        public Action? AfterMethod { get; set; }
        public CancellationToken CloseToken { get; private set; }
        public int Closed { get; private set; }
        public int Opened { get; private set; }
        public int FileReads { get; private set; }
        public byte OpenMode { get; private set; }
        public NodeId LastOpenedFile { get; private set; }
        public List<NodeId> OpenedFiles { get; } = [];
        public List<NodeId> ClosedFiles { get; } = [];
        public List<(string Property, NodeId File)> PropertyReadsWhileFileOpen { get; } = [];
        public List<(NodeId File, uint Handle)> FileReadHandles { get; } = [];
        public List<int> FileReadLengths { get; } = [];
        public Queue<ulong?> FileSizes { get; } = [];
        public uint? FileBufferLimit { get; set; }
        public Exception? BufferLimitFailure { get; set; }
        public Action? AfterBufferLimitRead { get; set; }
        public List<NodeId> BufferLimitFiles { get; } = [];
        public ValueTask<uint?> ReadFileMaxByteStringLengthAsync(NodeId file, CancellationToken cancellationToken)
        {
            BufferLimitFiles.Add(file);
            AfterBufferLimitRead?.Invoke();
            return BufferLimitFailure is null ? new(FileBufferLimit) : throw BufferLimitFailure;
        }
        public int FileSizeReads { get; private set; }
        public Action? AfterSizeRead { get; set; }
        public Exception? SizeFailure { get; set; }
        public ValueTask<ulong?> ReadFileSizeAsync(NodeId file, CancellationToken cancellationToken)
        {
            FileSizeReads++;
            AfterSizeRead?.Invoke();
            if (SizeFailure is not null)
            {
                throw SizeFailure;
            }
            return new(FileSizes.Count == 0 ? null : FileSizes.Dequeue());
        }
        public List<(NodeId File, uint Handle)> FileClosedHandles { get; } = [];
        public uint LastClosedHandle { get; private set; }
        private Dictionary<string, OpcUaJsonValue>? metadataPropertySnapshot;
        private Queue<byte[]>? metadataChunkSnapshot;
        private bool metadataRevisionAdvanced;
        public NodeId LastPositionFile { get; private set; }
        public uint LastPositionHandle { get; private set; }
        public NodeId LastWrittenFile { get; private set; }
        public uint LastWrittenHandle { get; private set; }
        public ulong Position { get; private set; }
        public List<NodeId> PositionedFiles { get; } = [];
        public List<string> FileOperations { get; } = [];
        public List<byte[]> WrittenChunks { get; } = [];
        public List<NodeId> WrittenFiles { get; } = [];
        public List<(NodeId Variable, Variant Value)> WrittenValues { get; } = [];
        public List<(NodeId Target, string Method, IReadOnlyList<Variant> Arguments)> MethodCalls { get; } = [];

        public ValueTask<IReadOnlyList<string>> ReadServerArrayAsync(CancellationToken cancellationToken) =>
            new(ServerArray);

        public ValueTask<NamespaceTable> ReadNamespaceArrayAsync(CancellationToken cancellationToken) =>
            new(new NamespaceTable(NamespaceArray));

        public ValueTask<bool> IsRegistryNodeAsync(NodeId node, CancellationToken cancellationToken) => new(IsRegistry);

        public ValueTask<bool> IsTypeOrSubtypeAsync(NodeId actualType, NodeId declaredType,
            CancellationToken cancellationToken) => new(actualType == declaredType);

        public ValueTask<OpcUaBrowsePage> BrowseAsync(NodeId node, CancellationToken cancellationToken)
        {
            BrowsedNodes.Add(node);
            if (BrowsePages.TryGetValue(node, out var pages) && pages.Count > 0)
            {
                return new(pages.Count == 1 ? pages.Peek() : pages.Dequeue());
            }
            return new(Pages.Dequeue());
        }

        public ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release, CancellationToken cancellationToken)
        {
            if (release)
            {
                Released.Add(continuationPoint);
                return ReleaseFailure is null ? new(new OpcUaBrowsePage([])) : throw ReleaseFailure;
            }
            return BrowseNextFailure is null ? new(Pages.Dequeue()) : throw BrowseNextFailure;
        }

        public ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken)
        {
            FileOperations.Add($"Open:{mode}");
            if (AdvanceMetadataRevisionOnOpen && !metadataRevisionAdvanced &&
                file.IdentifierAsString == "Metadata")
            {
                metadataPropertySnapshot = JsonValues.Where(pair => pair.Key.StartsWith("metadata:", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                metadataChunkSnapshot = new(Chunks.Select(chunk => chunk.ToArray()));
                JsonValues["metadata:Name"] = Value("\"Advanced metadata revision\"", BuiltInType.String);
                Chunks.Clear();
                Chunks.Add(System.Text.Encoding.UTF8.GetBytes("""{"name":"Advanced metadata revision"}"""));
                Chunks.Add([]);
                metadataRevisionAdvanced = true;
            }
            Opened++;
            LastOpenedFile = file;
            OpenedFiles.Add(file);
            OpenMode = mode;
            AfterOpen?.Invoke();
            return new(7u);
        }

        public ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken)
        {
            FileReads++;
            FileReadHandles.Add((file, handle));
            FileReadLengths.Add(length);
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }
            var chunk = file.IdentifierAsString == "Metadata" && metadataChunkSnapshot is not null
                ? metadataChunkSnapshot.Dequeue()
                : Chunks[0];
            if (file.IdentifierAsString != "Metadata" || metadataChunkSnapshot is null)
            {
                Chunks.RemoveAt(0);
            }
            AfterRead?.Invoke();
            return new(chunk);
        }

        public ValueTask SetPositionAsync(NodeId file, uint handle, ulong position,
            CancellationToken cancellationToken)
        {
            FileOperations.Add($"Position:{handle}");
            if (PositionFailure is not null)
            {
                throw PositionFailure;
            }
            LastPositionFile = file;
            LastPositionHandle = handle;
            Position = position;
            PositionedFiles.Add(file);
            AfterPosition?.Invoke();
            return default;
        }

        public ValueTask WriteAsync(NodeId file, uint handle, byte[] data, CancellationToken cancellationToken)
        {
            FileOperations.Add($"Write:{handle}");
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
            LastWrittenFile = file;
            LastWrittenHandle = handle;
            WrittenFiles.Add(file);
            WrittenChunks.Add(data);
            AfterWrite?.Invoke();
            return default;
        }

        public ValueTask WriteValueAsync(NodeId variable, Variant value, CancellationToken cancellationToken)
        {
            if (WriteValueFailure is not null)
            {
                throw WriteValueFailure;
            }
            WrittenValues.Add((variable, value));
            return default;
        }

        public ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
            IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken)
        {
            MethodCalls.Add((target, methodBrowseName, inputArguments.ToArray()));
            if (MethodFailure is not null)
            {
                throw MethodFailure;
            }
            AfterMethod?.Invoke();
            return new(MethodOutputs.TryGetValue(methodBrowseName, out var outputs) ? outputs : []);
        }

        public string SpecVersion { get; init; } = "1.0-rc4";
        public OpcUaJsonValue JsonValue { get; init; } = Value("42", BuiltInType.UInt32);

        public ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken) =>
            new(StringValues.TryGetValue(variable.IdentifierAsString, out var value) ? value : SpecVersion);
        public ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken)
        {
            JsonValueReads.Add(variable);
            BeforeJsonRead?.Invoke(variable);
            if (Opened > Closed)
            {
                PropertyReadsWhileFileOpen.Add((variable.IdentifierAsString, LastOpenedFile));
            }
            if (Opened > Closed && LastOpenedFile.IdentifierAsString == "Metadata" &&
                metadataPropertySnapshot is not null &&
                metadataPropertySnapshot.TryGetValue(variable.IdentifierAsString, out var pinnedValue))
            {
                return new(pinnedValue);
            }
            return new(JsonValues.TryGetValue(variable.IdentifierAsString, out var value) ? value : JsonValue);
        }
        public OpcUaRegistryOriginValue? OriginValue { get; set; }
        public List<(NodeId Variable, NodeId DataType)> OriginReads { get; } = [];
        public ValueTask<OpcUaRegistryOriginValue> ReadRegistryOriginAsync(NodeId variable, NodeId expectedDataType,
            CancellationToken cancellationToken)
        {
            OriginReads.Add((variable, expectedDataType));
            return new(OriginValue ?? throw new InvalidOperationException("No origin fixture supplied."));
        }

        public List<NodeId> ExpandedNodeIdReads { get; } = [];
        public ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable, CancellationToken cancellationToken)
        {
            ExpandedNodeIdReads.Add(variable);
            return new(ExpandedNodeIds[variable.IdentifierAsString]);
        }

        public ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
            CancellationToken cancellationToken) => new(ExternalNodeIdMappings[nodeId]);

        public ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken)
        {
            FileOperations.Add($"Close:{handle}");
            CloseToken = cancellationToken;
            Closed++;
            ClosedFiles.Add(file);
            FileClosedHandles.Add((file, handle));
            LastClosedHandle = handle;
            BeforeClose?.Invoke();
            if (file.IdentifierAsString == "Metadata")
            {
                metadataPropertySnapshot = null;
                metadataChunkSnapshot = null;
            }
            if (CloseFailure is not null)
            {
                throw CloseFailure;
            }
            AfterClose?.Invoke();
            return default;
        }

    }

    [Test]
    public async Task RegistryContentsCannotBeBrowsedBeforeSpecVersionIsAccepted()
    {
        var reader = new OpcUaRegistryReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task FileContentsCannotBeReadBeforeSpecVersionIsAccepted()
    {
        var session = new FakeSession();
        var reader = new OpcUaRegistryReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(session.OpenMode).IsEqualTo((byte)0);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("wrong-type")]
    [Arguments("duplicate")]
    public async Task ReaderExplicitVersionRejectsInvalidVersionsContainer(string scenario)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(scenario switch
        {
            "wrong-type" => new OpcUaBrowsePage([
                TypedObjectRef("Versions", VersionsNode, new NodeId("OtherType", 1))]),
            "duplicate" => new OpcUaBrowsePage([
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType),
                TypedObjectRef("Versions", new NodeId("duplicate-versions", 1), ResourceVersionsType)]),
            _ => new OpcUaBrowsePage([])
        });

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourcePropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
                ResourceVersionsType));

        await Assert.That(error!.Code).IsEqualTo(
            scenario == "duplicate" ? FederationErrorCode.Ambiguous : FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a");
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    public async Task WriterDocumentReplacementTargetsDefaultOrExactVersion()
    {
        var (session, writer) = await WriterForResource();

        await writer.ReplaceResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, new byte[] { 3, 4 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a");
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 3, 4 });
        await Assert.That(NodeIdTrace(session.WrittenFiles)).IsEqualTo("logical-schema-a");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a");
        QueueDocumentSelection(session, explicitVersion: true);
        await writer.ReplaceResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType,
            new byte[] { 7, 8 }, "v2", ResourceVersionsType);

        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(NodeIdTrace(session.WrittenFiles)).IsEqualTo("logical-schema-a|schema-a-v2");
        await Assert.That(session.WrittenChunks[0]).IsEquivalentTo(new byte[] { 3, 4 });
        await Assert.That(session.WrittenChunks[1]).IsEquivalentTo(new byte[] { 7, 8 });
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
    }

    [Test]
    public async Task ReaderSeparatesLogicalMetaAttributesFromVersionAttributes()
    {
        var logicalMetaLabels = new NodeId("logical-meta-labels", 1);
        var logicalLabels = new NodeId("logical-labels", 1);
        var exactVersionLabels = new NodeId("default-version-labels", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta-epoch:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["logical-epoch:Epoch"] = Value("41", BuiltInType.UInt32),
                ["exact-epoch:Epoch"] = Value("99", BuiltInType.UInt32)
            },
            StringValues =
            {
                ["meta-label:region"] = "logical-meta-only",
                ["logical-label:region"] = "logical-delegated-label",
                ["exact-version-label:region"] = "exact-version-label"
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([VariableRef("MetaEpoch", "meta-epoch")]));
        var metaEpoch = await reader.ReadJsonValueComponentAsync(LogicalResourceNode, "MetaEpoch");
        session.Pages.Enqueue(new([VariableRef("Epoch", "logical-epoch")]));
        var logicalEpoch = await reader.ReadJsonValueComponentAsync(LogicalResourceNode, "Epoch");
        session.Pages.Enqueue(new([VariableRef("Epoch", "exact-epoch")]));
        var exactEpoch = await reader.ReadJsonValueComponentAsync(DefaultVersionNode, "Epoch");
        session.Pages.Enqueue(new([
            new OpcUaReference(logicalMetaLabels, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType),
            new OpcUaReference(logicalLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var labels = await reader.ReadAttributesAsync(LogicalResourceNode, AttributesType);
        session.Pages.Enqueue(new([
            new OpcUaReference(exactVersionLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("exact-version-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var exactLabels = await reader.ReadAttributesAsync(DefaultVersionNode, AttributesType);
        // The current convenience API selects Labels only. Resolve MetaLabels through
        // the public browse/read APIs rather than pretending it is a separate Meta entity.
        session.Pages.Enqueue(new([
            new OpcUaReference(logicalLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType),
            new OpcUaReference(logicalMetaLabels, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType)
        ]));
        var logicalChildren = await reader.BrowseAllAsync(LogicalResourceNode);
        var metaLabels = logicalChildren.Single(child => child.BrowseName.Name == "MetaLabels");
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("meta-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var metaRegion = await reader.ReadStringComponentAsync(metaLabels.Target, "region");

        await Assert.That(metaEpoch.GetUInt32()).IsEqualTo(17u);
        await Assert.That(logicalEpoch.GetUInt32()).IsEqualTo(41u);
        await Assert.That(exactEpoch.GetUInt32()).IsEqualTo(99u);
        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("logical-delegated-label");
        await Assert.That(exactLabels.GetProperty("region").GetString()).IsEqualTo("exact-version-label");
        await Assert.That(metaLabels.Target).IsEqualTo(logicalMetaLabels);
        await Assert.That(metaRegion).IsEqualTo("logical-meta-only");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|logical-schema-a|logical-schema-a|schema-a-default-v1|" +
            "logical-schema-a|logical-labels|schema-a-default-v1|default-version-labels|" +
            "logical-schema-a|logical-meta-labels");
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    public async Task ReaderResourceCollectionHandlesEmptyAndSingletonLogicalResources()
    {
        foreach (var count in new[] { 0, 1 })
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                    ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                    ["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String),
                    ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                    ["default:Epoch"] = Value("99", BuiltInType.UInt32)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new(count == 0
                ? Array.Empty<OpcUaReference>()
                : [TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
            if (count == 1)
            {
                session.Pages.Enqueue(new([VariableRef("ResourceId", "logical")]));
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                    VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                    VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
                    VariableRef("ModifiedAt", "logical")
                ]));
            }

            var resources = await reader.ReadResourceCollectionAsync(
                GroupNode, "/teams/team-a", "schemas", ResourceType);

            await Assert.That(resources.GetArrayLength()).IsEqualTo(count);
            if (count == 1)
            {
                await Assert.That(resources[0].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
                await Assert.That(resources[0].GetProperty("versionid").GetString()).IsEqualTo("v1");
                await Assert.That(resources[0].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
                await Assert.That(session.BrowsedNodes.Count(node => node == LogicalResourceNode)).IsEqualTo(2);
                await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
            }
        }
    }

    [Test]
    public async Task ReaderVersionCollectionPreservesEmptySingletonAndMembersRegardlessOfBrowseOrder()
    {
        foreach (var count in new[] { 0, 1, 2 })
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["v1:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v1:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["v1:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String),
                    ["v1:Epoch"] = Value("4", BuiltInType.UInt32),
                    ["v1:Name"] = Value("\"Schema A v1\"", BuiltInType.String),
                    ["v1:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v1:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                    ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                    ["v2:Epoch"] = Value("5", BuiltInType.UInt32),
                    ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String),
                    ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new([
                TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
                TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType)
            ]));
            session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
            var versions = new List<OpcUaReference>();
            string[] versionIds = count == 2 ? ["v2", "v1"] : count == 1 ? ["v1"] : [];
            foreach (var versionId in versionIds)
            {
                versions.Add(TypedObjectRef(versionId,
                    versionId == "v1" ? DefaultVersionNode : VersionTwoNode, ResourceType));
            }
            session.Pages.Enqueue(new(versions));
            foreach (var versionId in versionIds)
            {
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", versionId), VariableRef("VersionId", versionId),
                    VariableRef("Xid", versionId), VariableRef("Epoch", versionId),
                    VariableRef("Name", versionId), VariableRef("CreatedAt", versionId),
                    VariableRef("ModifiedAt", versionId)
                ]));
            }

            var actual = await reader.ReadVersionCollectionAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, ResourceVersionsType);
            var byVersionId = actual.EnumerateArray().ToDictionary(
                item => item.GetProperty("versionid").GetString()!, StringComparer.Ordinal);

            await Assert.That(actual.GetArrayLength()).IsEqualTo(count);
            if (count >= 1)
            {
                await Assert.That(byVersionId["v1"].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
                await Assert.That(byVersionId["v1"].GetProperty("xid").GetString())
                    .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
                await Assert.That(byVersionId["v1"].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
            }
            if (count == 2)
            {
                await Assert.That(byVersionId["v2"].GetProperty("xid").GetString())
                    .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v2");
                await Assert.That(byVersionId["v2"].GetProperty("epoch").GetUInt32()).IsEqualTo(5u);
            }
            await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
            await Assert.That(session.BrowsedNodes.Contains(new NodeId("logical-schema-b", 1))).IsFalse();
        }
    }

    [Test]
    public async Task WriterCreateAndGetOrCreateRejectMalformedOutputs()
    {
        var cases = new[]
        {
            (Method: "CreateResource", Outputs: (IReadOnlyList<Variant>)[new Variant(new NodeId("wrong", 1))]),
            (Method: "GetOrCreateResource", Outputs: (IReadOnlyList<Variant>)[
                new Variant(new NodeId("schema-a", 1)), new Variant("v1"), new Variant(7u)]),
            (Method: "GetOrCreateGroup", Outputs: (IReadOnlyList<Variant>)[
                new Variant(new NodeId("team-a", 1)), new Variant("not-a-bool")])
        };

        foreach (var (method, outputs) in cases)
        {
            var (session, writer) = await WriterForResource();
            session.MethodOutputs[method] = outputs;
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
            {
                switch (method)
                {
                    case "CreateResource":
                        _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1");
                        break;
                    case "GetOrCreateResource":
                        _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1");
                        break;
                    default:
                        _ = await writer.GetOrCreateGroupAsync(File, "team-a");
                        break;
                }
            });

            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
            await Assert.That(session.MethodCalls.Single().Target).IsEqualTo(
                method == "GetOrCreateGroup" ? File : GroupNode);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FederationMetadataCollectionPreservesValuesAndOptionalPresence(
        bool includeOptionalMembers)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers);

        var result = await source.ReadAsync(new(FederationOperation.Collection,
            "/teams/team-a/schemas"));

        var entities = result.Metadata.GetProperty("entities");
        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("collection");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(entities.EnumerateObject().Select(property => property.Name))
            .IsEquivalentTo(["metadata-a"]);
        await AssertMetadataOnlyProjection(entities.GetProperty("metadata-a"), includeOptionalMembers);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FederationMetadataEntityPreservesValuesAndOptionalPresence(
        bool includeOptionalMembers)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        var entity = result.Metadata.GetProperty("entity");
        await Assert.That(result.SelectedXid).IsEqualTo("/teams/team-a/schemas/metadata-a");
        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("resource");
        await AssertMetadataOnlyProjection(entity, includeOptionalMembers);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
        await Assert.That(session.PropertyReadsWhileFileOpen)
            .Contains(("metadata:ResourceId", new NodeId("Metadata", 1)));
        await Assert.That(session.PropertyReadsWhileFileOpen)
            .Contains(("metadata:VersionId", new NodeId("Metadata", 1)));
        await Assert.That(session.FileReadHandles)
            .Contains((new NodeId("Metadata", 1), 7u));
        await Assert.That(session.FileClosedHandles)
            .Contains((new NodeId("Metadata", 1), 7u));
    }

    [Test]
    public async Task FederationMetadataReadUsesPropertiesAndFileFromPinnedRevision()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.AdvanceMetadataRevisionOnOpen = true;

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        var entity = result.Metadata.GetProperty("entity");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Metadata resource");
        await Assert.That(entity.GetProperty("label").GetString()).IsEqualTo("metadata label");
        await Assert.That(session.JsonValues["metadata:Name"].Value.GetString())
            .IsEqualTo("Advanced metadata revision");
    }

    [Test]
    public async Task FederationMetadataVersionIsSelectedByObservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var observedVersionId = session.JsonValues["metadata:VersionId"].Value.GetString()!;

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            $"/teams/team-a/schemas/metadata-a/versions/{Uri.EscapeDataString(observedVersionId)}"));

        var version = result.Metadata.GetProperty("entity");
        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/metadata-a/versions/metadata-v1");
        await Assert.That(version.GetProperty("resourceid").GetString()).IsEqualTo("metadata-a");
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo(observedVersionId);
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(version.GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/metadata-a/versions/metadata-v1");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task FederationMetadataVersionDoesNotResolveUnobservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a/versions/not-observed")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task FederationMetadataReadRejectsMissingResourceIdProperty()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "ResourceId").ToArray())]);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"/teams/team-a/schemas/other\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsInvalidXidProperty(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:Xid"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"different\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataCollectionRejectsInvalidResourceIdProperty(
        string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:ResourceId"] = Value(json, builtInType);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsMissingResourceIdProperty()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "ResourceId").ToArray())]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsDuplicateResourceIds()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var groupReferences = session.BrowsePages[GroupNode].Peek().References;
        session.BrowsePages[GroupNode] = new([new([
            .. groupReferences,
            TypedObjectRef("metadata-a", new NodeId("metadata-resource-duplicate", 1),
                new NodeId("MetadataResourceType", 1))
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsEmptyResourceId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var groupReferences = session.BrowsePages[GroupNode].Peek().References;
        var resource = groupReferences.Single(reference => reference.BrowseName.Name == "metadata-a");
        session.BrowsePages[GroupNode] = new([new([
            .. groupReferences.Where(reference => reference.BrowseName.Name != "metadata-a"),
            new(resource.Target, new QualifiedName("", 1), resource.NodeClass, resource.TypeDefinition)
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataVersionDoesNotResolveWhenVersionIdIsAbsent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "VersionId").ToArray())]);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a/versions/not-observed")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsMalformedObservedVersionId(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:VersionId"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"different\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsInvalidResourceIdProperty(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:ResourceId"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task FederationMetadataProjectionDoesNotInventAnUnobservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "VersionId").ToArray())]);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        await Assert.That(result.Metadata.GetProperty("entity").TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
    }

    [Test]
    public async Task FederationMetadataMappingRequiresMaxVersionsOne()
    {
        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenMetadataOnlySource(includeOptionalMembers: false, maxVersions: 2));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task FederationMetadataMappingRequiresHasDocumentFalse()
    {
        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await OpenMetadataOnlySource(includeOptionalMembers: false, hasDocument: true));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task FederationMetadataHasNoMaterializedVersionsCollection()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas/metadata-a/versions")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataResourceRejectsNonFileMetadataComponent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new([
            .. session.BrowsePages[metadataResource].Peek().References
                .Where(reference => reference.BrowseName.Name != "Metadata"),
            new(new NodeId("Metadata", 1), new QualifiedName("Metadata", 1), NodeClass.Object,
                new NodeId("FolderType", 1))
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataResourceRequiresMetadataFileComponent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "Metadata").ToArray())]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataReadRejectsNonObjectJson()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes("[]");

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    [Test]
    public async Task FederationMetadataReadRejectsIdentityContradictingMetadataJson()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes("""{"resourceid":"different"}""");

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReadSource Source)>
        OpenMetadataOnlySource(bool includeOptionalMembers, int maxVersions = 1, bool hasDocument = false)
    {
        const string modelTemplate =
            """
            {"groups":{"teams":{"singular":"team","resources":{"schemas":{
              "singular":"schema","hasdocument":false,"maxversions":1,
              "attributes":{
                "label":{"type":"string"},
                "enabled":{"type":"boolean"},
                "rank":{"type":"integer"},
                "nullable":{"type":"any"},
                "empty":{"type":"array","item":{"type":"any"}},
                "ordered":{"type":"array","item":{"type":"any"}},
                "optionaltext":{"type":"string"},
                "optionalnull":{"type":"any"}
              }
            }}}}}
            """;
        var model = modelTemplate.Replace("\"maxversions\":1",
                $"\"maxversions\":{maxVersions}", StringComparison.Ordinal)
            .Replace("\"hasdocument\":false", $"\"hasdocument\":{hasDocument.ToString().ToLowerInvariant()}",
                StringComparison.Ordinal);
        const string metadataWithOptional =
            """{"label":"metadata label","enabled":false,"rank":0,"nullable":null,"empty":[],"ordered":["second","first"],"optionaltext":"present","optionalnull":null,"meta":{"epoch":0,"readonly":false,"name":"Metadata resource meta"}}""";
        const string metadataWithoutOptional =
            """{"label":"metadata label","enabled":false,"rank":0,"nullable":null,"empty":[],"ordered":["second","first"],"meta":{"epoch":0,"readonly":false,"name":"Metadata resource meta"}}""";

        var session = SourceSession(model);
        var metadataResourceType = new NodeId("MetadataResourceType", 1);
        var metadataResourceNode = new NodeId("metadata-resource-a", 1);

        session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
        session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
        session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
        session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
        session.BrowsePages[GroupNode] = new([new([
            .. GroupPropertiesPage().References,
            TypedObjectRef("metadata-a", metadataResourceNode, metadataResourceType)
        ])]);
        session.BrowsePages[metadataResourceNode] = new([new([
            VariableRef("ResourceId", "metadata"), VariableRef("VersionId", "metadata"),
            VariableRef("Xid", "metadata"), VariableRef("Epoch", "metadata"),
            VariableRef("CreatedAt", "metadata"), VariableRef("ModifiedAt", "metadata"),
            VariableRef("Name", "metadata"),
            FileRef("Metadata")
        ])]);
        session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:VersionId"] = Value("\"metadata-v1\"", BuiltInType.String);
        session.JsonValues["metadata:Xid"] =
            Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Epoch"] = Value("0", BuiltInType.UInt32);
        session.JsonValues["metadata:CreatedAt"] =
            Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["metadata:ModifiedAt"] =
            Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["metadata:Name"] = Value("\"Metadata resource\"", BuiltInType.String);
        var source = await OpenSource(session, new(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = OpcUaResourceTypeBinding.ForMetadataOnly(metadataResourceType)
            },
            AttributesType));
        session.Chunks.Add(System.Text.Encoding.UTF8.GetBytes(includeOptionalMembers
            ? metadataWithOptional : metadataWithoutOptional));
        session.Chunks.Add(Array.Empty<byte>());
        return (session, source);
    }

    private static async Task AssertMetadataOnlyProjection(JsonElement entity, bool includeOptionalMembers)
    {
        await Assert.That(entity.GetProperty("resourceid").GetString()).IsEqualTo("metadata-a");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Metadata resource");
        await Assert.That(entity.GetProperty("epoch").GetUInt32()).IsEqualTo(0u);
        await Assert.That(entity.GetProperty("label").GetString()).IsEqualTo("metadata label");
        await Assert.That(entity.GetProperty("enabled").ValueKind).IsEqualTo(JsonValueKind.False);
        await Assert.That(entity.GetProperty("rank").GetInt32()).IsEqualTo(0);
        await Assert.That(entity.GetProperty("nullable").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(entity.GetProperty("empty").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(entity.GetProperty("empty").GetArrayLength()).IsEqualTo(0);
        var ordered = entity.GetProperty("ordered");
        await Assert.That(ordered.GetArrayLength()).IsEqualTo(2);
        await Assert.That(ordered[0].GetString()).IsEqualTo("second");
        await Assert.That(ordered[1].GetString()).IsEqualTo("first");
        await Assert.That(entity.TryGetProperty("optionaltext", out var optionalText))
            .IsEqualTo(includeOptionalMembers);
        await Assert.That(entity.TryGetProperty("optionalnull", out var optionalNull))
            .IsEqualTo(includeOptionalMembers);
        if (includeOptionalMembers)
        {
            await Assert.That(optionalText.GetString()).IsEqualTo("present");
            await Assert.That(optionalNull.ValueKind).IsEqualTo(JsonValueKind.Null);
        }

        await Assert.That(entity.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(entity.TryGetProperty("versionsurl", out _)).IsFalse();
        var meta = entity.GetProperty("meta");
        await Assert.That(meta.TryGetProperty("defaultversionurl", out _)).IsFalse();
        await Assert.That(meta.GetProperty("epoch").GetUInt32()).IsEqualTo(0u);
        await Assert.That(meta.GetProperty("readonly").GetBoolean()).IsFalse();
        await Assert.That(meta.GetProperty("name").GetString()).IsEqualTo("Metadata resource meta");
    }

    [Test]
    [Arguments(FederationOperation.Collection, false)]
    [Arguments(FederationOperation.Collection, true)]
    [Arguments(FederationOperation.Entity, false)]
    [Arguments(FederationOperation.Entity, true)]
    public async Task FederationMetadataReadRejectsMalformedUtf8(
        FederationOperation operation, bool incompleteMultibyteSequence)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
        // Replace only the queued metadata payload, after initialization has consumed Model/Capabilities.
        // Keep the invalid bytes raw: decoding them here could silently insert replacement characters.
        byte[] malformed = incompleteMultibyteSequence ? [0xE2] : [0xC3, 0x28];
        session.Chunks[0] = [
            .. System.Text.Encoding.UTF8.GetBytes("{\"label\":\""), .. malformed,
            .. System.Text.Encoding.UTF8.GetBytes("\"}")
        ];
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    [Test]
    [Arguments(FederationOperation.Collection, false)]
    [Arguments(FederationOperation.Collection, true)]
    [Arguments(FederationOperation.Entity, false)]
    [Arguments(FederationOperation.Entity, true)]
    public async Task FederationMetadataReadRejectsDuplicateJsonNames(
        FederationOperation operation, bool escapedEquivalentName)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
        // Raw JSON preserves both conflicting members and the escape in the equivalent property name.
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes(escapedEquivalentName
            ? """{"label":"first","\u006cabel":"second"}"""
            : """{"label":"first","label":"second"}""");
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    private static async Task AssertMetadataFileWasRead(
        FakeSession session, int openedBefore, int closedBefore)
    {
        await Assert.That(session.OpenedFiles.Skip(openedBefore).ToArray())
            .IsEquivalentTo(new NodeId[] { new("Metadata", 1) });
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.OpenMode).IsEqualTo((byte)1);
        await Assert.That(session.ClosedFiles.Skip(closedBefore).ToArray())
            .IsEquivalentTo(new NodeId[] { new("Metadata", 1) });
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FederationMetadataDocumentReadRejectsBeforeFileTransfer(bool exactVersion)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataFile = new NodeId("Metadata", 1);
        var observedVersionId = session.JsonValues["metadata:VersionId"].Value.GetString()!;
        await Assert.That(observedVersionId).IsEqualTo("metadata-v1");
        var target = exactVersion
            ? $"/teams/team-a/schemas/metadata-a/versions/{Uri.EscapeDataString(observedVersionId)}"
            : "/teams/team-a/schemas/metadata-a";
        var domainDocumentFile = new NodeId("metadata-resource-a", 1);
        var openedFilesBefore = session.OpenedFiles.ToArray();
        var openedBefore = session.Opened;

        // Keep nonempty bytes queued so an accidental FileType read is observable,
        // rather than making an empty queue the reason the operation fails.
        byte[] sentinelBytes = [0x53, 0x45, 0x4e, 0x54, 0x49, 0x4e, 0x45, 0x4c];
        session.Chunks.Clear();
        session.Chunks.AddRange([sentinelBytes, Array.Empty<byte>()]);
        var chunksBefore = session.Chunks.Select(chunk => chunk.ToArray()).ToArray();

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Document,
                target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Opened).IsEqualTo(openedBefore);
        await Assert.That(session.OpenedFiles.ToArray()).IsEquivalentTo(openedFilesBefore);
        var openedAfter = session.OpenedFiles.Skip(openedFilesBefore.Length).ToArray();
        await Assert.That(openedAfter).IsEquivalentTo(Array.Empty<NodeId>());
        await Assert.That(openedAfter.Contains(metadataFile)).IsFalse();
        await Assert.That(openedAfter.Contains(domainDocumentFile)).IsFalse();

        await Assert.That(session.Chunks.Count).IsEqualTo(chunksBefore.Length);
        for (var index = 0; index < chunksBefore.Length; index++)
        {
            await Assert.That(session.Chunks[index]).IsEquivalentTo(chunksBefore[index]);
        }
    }

    [Test]
    public async Task VerifyRegistryRootAsyncReturnsTheExactRegistryNodeId()
    {
        var root = new NodeId("Registry", 1);
        var reader = new OpcUaRegistryReader(new FakeSession());

        var verified = await reader.VerifyRegistryRootAsync(root);

        await Assert.That(verified).IsEqualTo(root);
        await Assert.That(verified.NamespaceIndex).IsEqualTo((ushort)1);
        await Assert.That(verified.IdentifierAsString).IsEqualTo("Registry");
    }

    [Test]
    public async Task VerifyRegistryRootAsyncRejectsNullOrNonRegistryRoot()
    {
        var nullRootError = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession()).VerifyRegistryRootAsync(NodeId.Null));
        var wrongRoot = new NodeId("NotARegistry", 1);
        var wrongTypeError = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession { IsRegistry = false })
                .VerifyRegistryRootAsync(wrongRoot));

        await Assert.That(nullRootError!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(wrongTypeError!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task VerifyRootAsyncResolvesAdvertisedRootUsingSessionNamespaceArray()
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var reader = new OpcUaRegistryReader(new FakeSession());

        var root = await reader.VerifyRootAsync(target);

        await Assert.That(root.IdentifierAsString).IsEqualTo("Registry");
        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)1);
    }

    [Test]
    [Arguments("empty-server")]
    [Arguments("missing-namespace-entry")]
    [Arguments("different-uris")]
    public async Task VerifyRootAsyncRejectsInvalidServerAndNamespaceArrayIdentity(string mismatch)
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var session = mismatch switch
        {
            "empty-server" => new FakeSession { ServerArray = [] },
            "missing-namespace-entry" => new FakeSession
            {
                NamespaceArray = ["http://opcfoundation.org/UA/"]
            },
            "different-uris" => new FakeSession { ServerArray = ["urn:server:foreign"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var reader = new OpcUaRegistryReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.VerifyRootAsync(target));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task ResolveResourceNodeAsyncUsesMappedResourceTypeAndReturnsExactNodeId()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
            "schema-a", ResourceType, resourceVersionsType: ResourceVersionsType);

        await Assert.That(resolved).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore).First()).IsEqualTo(GroupNode);
        await Assert.That(session.BrowsedNodes[^1]).IsEqualTo(LogicalResourceNode);
    }

    [Test]
    public async Task ResolveResourceNodeAsyncUsesMappedVersionTypeAndReturnsExactVersionNodeId()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
            "schema-a", ResourceType, "v1", ResourceVersionsType);

        await Assert.That(resolved).IsEqualTo(DefaultVersionNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(
            new[] { GroupNode, LogicalResourceNode, VersionsNode, DefaultVersionNode });
        await Assert.That(session.JsonValues["version:VersionId"].Value.GetString()).IsEqualTo("v1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolveResourceNodeAsyncRejectsWrongChildKindOrTypeMapping(bool wrongType)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        var child = wrongType
            ? TypedObjectRef("schema-a", LogicalResourceNode, new NodeId("OtherResourceType", 1))
            : VariableRef("schema-a");
        session.BrowsePages[GroupNode] = new([new([child])]);
        var browsedBefore = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
                "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { GroupNode });
    }

    [Test]
    public async Task ResolveMetadataResourceNodeAsyncResolvesMetadataOnlyResourceByExactNodeId()
    {
        const string metadataModel =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false,"maxversions":1}}}}}""";
        var metadataType = new NodeId("MetadataResourceType", 1);
        var metadataNode = new NodeId("metadata-resource-a", 1);
        var session = new FakeSession();
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes("{}"), Array.Empty<byte>()
        ]);
        var reader = await ModelReader(session, metadataModel);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("metadata-a", metadataNode, metadataType)])
        ]);
        session.BrowsePages[metadataNode] = new([
            new([
                VariableRef("ResourceId", "metadata"), VariableRef("Xid", "metadata"),
                VariableRef("Name", "metadata"), FileRef("Metadata")
            ])
        ]);
        session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Xid"] =
            Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Name"] = Value("\"Metadata resource\"", BuiltInType.String);
        var openedBefore = session.OpenedFiles.Count;
        var closedBefore = session.Closed;

        var resolved = await reader.ResolveMetadataResourceNodeAsync(GroupNode, "/teams/team-a",
            "schemas", "metadata-a", metadataType);

        await Assert.That(resolved).IsEqualTo(metadataNode);
        await Assert.That(session.OpenedFiles.Skip(openedBefore))
            .IsEquivalentTo(new[] { new NodeId("Metadata", 1) });
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task OpcUaRegistryTypeMapResolvesExactTargetGroupResourceAndVersionTypes()
    {
        var groupType = new NodeId("TargetGroupType", 1);
        var resourceType = new NodeId("TargetResourceType", 1);
        var versionsType = new NodeId("TargetResourceVersionsType", 1);
        var attributesType = new NodeId("TargetAttributesType", 1);
        var binding = new OpcUaResourceTypeBinding(resourceType, versionsType);
        var map = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal) { ["teams"] = groupType },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = binding
            },
            attributesType);

        await Assert.That(map.GroupTypes["teams"]).IsEqualTo(groupType);
        await Assert.That(map.GroupTypes.ContainsKey("Teams")).IsFalse();
        await Assert.That(map.ResourceTypes.ContainsKey(("teams", "schemas"))).IsTrue();
        var resolvedBinding = map.ResourceTypes[("teams", "schemas")];
        await Assert.That(resolvedBinding.ResourceType).IsEqualTo(resourceType);
        await Assert.That(resolvedBinding.ResourceVersionsType).IsEqualTo(versionsType);
        await Assert.That(resolvedBinding.IsMetadataOnly).IsFalse();
        await Assert.That(map.ResourceTypes.ContainsKey(("Teams", "schemas"))).IsFalse();
        await Assert.That(map.AttributesType).IsEqualTo(attributesType);
    }

    [Test]
    public async Task OpcUaResourceTypeBindingForMetadataOnlyHasNoVersionsType()
    {
        var metadataType = new NodeId("TargetMetadataResourceType", 1);

        var binding = OpcUaResourceTypeBinding.ForMetadataOnly(metadataType);

        await Assert.That(binding.ResourceType).IsEqualTo(metadataType);
        await Assert.That(binding.IsMetadataOnly).IsTrue();
        await Assert.That(binding.ResourceVersionsType.IsNull).IsTrue();
    }

    [Test]
    public async Task OpcUaRegistryTypeMapAllowsAnEmptyMappingWithoutGuessingTypes()
    {
        var attributesType = new NodeId("TargetAttributesType", 1);
        var map = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal),
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>(),
            attributesType);

        await Assert.That(map.GroupTypes.Count).IsEqualTo(0);
        await Assert.That(map.ResourceTypes.Count).IsEqualTo(0);
        await Assert.That(map.GroupTypes.TryGetValue("teams", out _)).IsFalse();
        await Assert.That(map.ResourceTypes.TryGetValue(("teams", "schemas"), out _)).IsFalse();
        await Assert.That(map.AttributesType).IsEqualTo(attributesType);
    }

    [Test]
    public async Task ResolveGroupNodeAsyncResolvesExactGroupUsingMappedGroupType()
    {
        var groupType = new NodeId("TargetGroupType", 1);
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[File] = new([
            new([TypedObjectRef("team-a", GroupNode, groupType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveGroupNodeAsync(File, "teams", "team-a", groupType);

        await Assert.That(resolved).IsEqualTo(GroupNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { File, GroupNode });
        await Assert.That(session.JsonValues["group:GroupId"].Value.GetString()).IsEqualTo("team-a");
        await Assert.That(session.JsonValues["group:Xid"].Value.GetString()).IsEqualTo("/teams/team-a");
    }

    [Test]
    public async Task ResolveGroupNodeAsyncRejectsWrongMappedGroupType()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.BrowsePages[File] = new([
            new([TypedObjectRef("team-a", GroupNode, new NodeId("OtherGroupType", 1))])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ResolveGroupNodeAsync(File, "teams", "team-a",
                new NodeId("TargetGroupType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { File });
    }

    [Test]
    public async Task ResolveExternalTargetAsyncPassesExactIdentityAndRetainsLeaseUntilResultDisposal()
    {
        var fixture = CreateResolverFixture();
        OpcUaExternalNodeId? receivedIdentity = null;
        OpcUaTrustedProviderResolver resolver = (identity, _) =>
        {
            receivedIdentity = identity;
            return ValueTask.FromResult<IOpcUaTrustedProviderLease>(fixture.Lease);
        };

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, resolver, version => version == "1.0-rc4",
            OpcUaExternalTargetRole.LogicalResource);

        await Assert.That(receivedIdentity).IsNotNull();
        await Assert.That(receivedIdentity!.ServerApplicationUri).IsEqualTo(TargetServerApplicationUri);
        await Assert.That(receivedIdentity.NamespaceUri).IsEqualTo(TargetNamespaceUri);
        await Assert.That(receivedIdentity.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(receivedIdentity.Identifier).IsEqualTo(LogicalResourceNode.IdentifierAsString);
        await Assert.That(result.ReadSource.Context).IsEqualTo(fixture.Lease.Context);
        await Assert.That(result.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/schema-a");
        await Assert.That(result.Node).IsEqualTo(LogicalResourceNode);
        await Assert.That(result.Role).IsEqualTo(OpcUaExternalTargetRole.LogicalResource);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);

        await result.DisposeAsync();
        await result.DisposeAsync();

        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerNamespaceArrayMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionNamespaceArrayUri: "urn:namespace:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncResolvesVersionIdentityToExactTargetNodeId()
    {
        var fixture = CreateResolverFixture(
            "/teams/team-a/schemas/schema-a/versions/v1");

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, ReturnLease(fixture.Lease), _ => true, OpcUaExternalTargetRole.ExactVersion);

        await Assert.That(result.Node).IsEqualTo(DefaultVersionNode);
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.Role).IsEqualTo(OpcUaExternalTargetRole.ExactVersion);
        await Assert.That(result.ReadSource.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);
        await result.DisposeAsync();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(OpcUaExternalTargetRole.LogicalResource)]
    [Arguments(OpcUaExternalTargetRole.ExactVersion)]
    public async Task ResolveExternalTargetAsyncRejectsTargetThatContradictsDeclaredRole(
        OpcUaExternalTargetRole role)
    {
        var fixture = CreateResolverFixture(role == OpcUaExternalTargetRole.LogicalResource
            ? "/teams/team-a/schemas/schema-a/versions/v1"
            : "/teams/team-a/schemas/schema-a");

        var error = await ResolveFailureAsync(fixture, role: role);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).IsEqualTo(
            "The external target XID does not match the declared reference role.");
        await Assert.That(fixture.Session.BrowsedNodes.Contains(GroupNode)).IsFalse();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsUnknownRoleBeforeAcquiringProvider()
    {
        var fixture = CreateResolverFixture();
        var calls = 0;
        OpcUaTrustedProviderResolver resolver = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<IOpcUaTrustedProviderLease>(fixture.Lease);
        };

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                fixture.Identity, resolver, _ => true, (OpcUaExternalTargetRole)42);
        });

        await Assert.That(error!.ParamName).IsEqualTo("role");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);
        await Assert.That(fixture.Session.BrowsedNodes).IsEquivalentTo(Array.Empty<NodeId>());
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsInvalidSpecVersionAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionSpecVersion: "malformed-version");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsLeaseSpecVersionMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(leaseSpecVersion: "1.0");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsSpecVersionChangeBeforeReadingModel()
    {
        var fixture = CreateResolverFixture(registryPropertySpecVersion: "1.0-rc5");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
        await Assert.That(fixture.Session.OpenedFiles).IsEquivalentTo(Array.Empty<NodeId>());
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsNullRegistryRootAndDisposesLease()
    {
        var fixture = CreateResolverFixture(registryRoot: NodeId.Null);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsWrongRegistryRootAndDisposesLease()
    {
        var fixture = CreateResolverFixture(isRegistry: false);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerUriAvailableOnlyInAnotherContext()
    {
        var fixture = CreateResolverFixture(leaseServerApplicationUri: "urn:server:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerArrayMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionServerArrayUri: "urn:server:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsNamespaceUriAvailableOnlyInAnotherContext()
    {
        var fixture = CreateResolverFixture(identityNamespaceUri: "urn:namespace:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("application")]
    [Arguments("namespace")]
    public async Task ResolveExternalTargetAsyncRejectsIdentityMismatchBeforeReadingRegistryDocuments(
        string mismatch)
    {
        var fixture = mismatch == "application"
            ? CreateResolverFixture(leaseServerApplicationUri: "urn:server:foreign",
                sessionServerArrayUri: "urn:server:foreign")
            : CreateResolverFixture(identityNamespaceUri: "urn:namespace:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(mismatch == "application"
            ? FederationErrorCode.PolicyDenied
            : FederationErrorCode.NotFound);
        await Assert.That(fixture.Session.OpenedFiles).IsEquivalentTo(Array.Empty<NodeId>());
        await Assert.That(fixture.Session.BrowsedNodes).IsEquivalentTo(Array.Empty<NodeId>());
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("wrong-node")]
    [Arguments("wrong-kind")]
    [Arguments("wrong-type")]
    public async Task ResolveExternalTargetAsyncRejectsWrongNodeKindOrTypeMapping(string mismatch)
    {
        var fixture = mismatch switch
        {
            "wrong-node" => CreateResolverFixture(
                externalNode: new NodeId("unrelated-target-node", 1)),
            "wrong-kind" => CreateResolverFixture(
                "/teams/team-a/schemas/schema-a/versions/v1",
                externalNode: LogicalResourceNode),
            "wrong-type" => CreateResolverFixture(useWrongResourceType: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var error = await ResolveFailureAsync(fixture, role: mismatch == "wrong-kind"
            ? OpcUaExternalTargetRole.ExactVersion
            : OpcUaExternalTargetRole.LogicalResource);

        await Assert.That(error.Code).IsEqualTo(
            mismatch == "wrong-type" ? FederationErrorCode.NotFound : FederationErrorCode.InvalidPackage);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncResolvesMetadataOnlyResourceWithoutVersions()
    {
        var fixture = CreateResolverFixture(metadataOnly: true);

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, ReturnLease(fixture.Lease), _ => true, OpcUaExternalTargetRole.LogicalResource);

        await Assert.That(result.Node).IsEqualTo(MetadataResourceNode);
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/metadata-a");
        await Assert.That(result.ReadSource.Model.Groups["teams"].Resources["schemas"].HasDocument)
            .IsFalse();
        await Assert.That(result.ReadSource.Model.Groups["teams"].Resources["schemas"]
                .MaxVersions.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("1");
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);
        await result.DisposeAsync();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsMissingRequiredMappingBeforeTargetReadAndDisposesLease()
    {
        var fixture = CreateResolverFixture(omitResourceMapping: true);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(fixture.Session.BrowsedNodes.Contains(LogicalResourceNode)).IsFalse();
        await Assert.That(fixture.Session.BrowsedNodes.Contains(GroupNode)).IsFalse();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsMetadataOnlyXidWithVersionsAndDisposesLease()
    {
        var fixture = CreateResolverFixture(
            "/teams/team-a/schemas/metadata-a/versions/v1", metadataOnly: true);

        var error = await ResolveFailureAsync(fixture, role: OpcUaExternalTargetRole.ExactVersion);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).IsEqualTo("A metadata-only Resource cannot contain a Version.");
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncDisposesLeaseWhenTargetReadThrows()
    {
        var failure = new IOException("Synthetic target file read failure.");
        var fixture = CreateResolverFixture(readFailure: failure);

        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                fixture.Identity, ReturnLease(fixture.Lease), _ => true, OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    private const string TargetServerApplicationUri = "urn:trusted:target-server";
    private const string TargetNamespaceUri = TargetServerApplicationUri;
    private const string MetadataModel =
        """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false,"maxversions":1}}}}}""";
    private static readonly NodeId MetadataResourceNode = new("metadata-resource-a", 1);
    private static readonly NodeId MetadataResourceType = new("TargetMetadataResourceType", 1);

    private sealed record ResolverFixture(
        FakeSession Session,
        TrackingResolverLease Lease,
        OpcUaExternalNodeId Identity);

    private sealed class TrackingResolverLease(
        OpcUaRegistryReader reader,
        NodeId registryRoot,
        string specVersion,
        NativeRegistryContext context,
        OpcUaRegistryTypeMap types,
        string serverApplicationUri) : IOpcUaTrustedProviderLease
    {
        public string ServerApplicationUri { get; } = serverApplicationUri;
        public OpcUaRegistryReader Reader { get; } = reader;
        public NodeId RegistryRoot { get; } = registryRoot;
        public string SpecVersion { get; } = specVersion;
        public NativeRegistryContext Context { get; } = context;
        public OpcUaRegistryTypeMap Types { get; } = types;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }

    private static ResolverFixture CreateResolverFixture(
        string? targetXid = null,
        NodeId? externalNode = null,
        bool isRegistry = true,
        NodeId? registryRoot = null,
        string sessionSpecVersion = "1.0-rc4",
        string leaseSpecVersion = "1.0-rc4",
        string? leaseServerApplicationUri = null,
        string? sessionServerArrayUri = null,
        string? sessionNamespaceArrayUri = null,
        string identityNamespaceUri = TargetNamespaceUri,
        string registryPropertySpecVersion = "1.0-rc4",
        bool metadataOnly = false,
        bool useWrongResourceType = false,
        bool omitResourceMapping = false,
        Exception? readFailure = null)
    {
        targetXid ??= metadataOnly
            ? "/teams/team-a/schemas/metadata-a"
            : "/teams/team-a/schemas/schema-a";
        var targetNode = metadataOnly
            ? MetadataResourceNode
            : targetXid.Contains("/versions/", StringComparison.Ordinal)
                ? DefaultVersionNode
                : LogicalResourceNode;
        var resolvedExternalNode = externalNode ?? targetNode;
        var model = metadataOnly ? MetadataModel : TestModel;
        var sessionServerApplicationUri = sessionServerArrayUri ?? TargetServerApplicationUri;
        var session = new FakeSession
        {
            IsRegistry = isRegistry,
            ReadFailure = readFailure,
            SpecVersion = sessionSpecVersion,
            ServerArray = [sessionServerApplicationUri],
            NamespaceArray =
            [
                "http://opcfoundation.org/UA/",
                sessionNamespaceArrayUri ?? sessionServerApplicationUri
            ]
        };
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes(model), Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("""{"flags":["doc"]}"""), Array.Empty<byte>()
        ]);
        session.JsonValues["RegistryId"] = Value("\"registry-target\"", BuiltInType.String);
        session.JsonValues["SpecVersion"] = Value(
            System.Text.Json.JsonSerializer.Serialize(registryPropertySpecVersion), BuiltInType.String);
        session.JsonValues["Xid"] = Value("\"/\"", BuiltInType.String);
        session.JsonValues["Epoch"] = Value("1", BuiltInType.UInt32);
        session.JsonValues["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.BrowsePages[File] = new([
            new([SpecVersionRef()]),
            new([
                VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
                VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
            ]),
            new([FileRef("Model")]),
            new([FileRef("Capabilities")]),
            new([TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))])
        ]);

        var resourceType = useWrongResourceType
            ? new NodeId("WrongTargetResourceType", 1)
            : ResourceType;
        var typeBinding = metadataOnly
            ? OpcUaResourceTypeBinding.ForMetadataOnly(MetadataResourceType)
            : new OpcUaResourceTypeBinding(resourceType, ResourceVersionsType);
        var resourceMappings = new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>();
        if (!omitResourceMapping)
        {
            resourceMappings[("teams", "schemas")] = typeBinding;
        }
        var types = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            resourceMappings,
            AttributesType);
        var registryReader = new OpcUaRegistryReader(session);

        if (metadataOnly)
        {
            session.Chunks.AddRange([
                System.Text.Encoding.UTF8.GetBytes("{}"), Array.Empty<byte>()
            ]);
            session.BrowsePages[GroupNode] = new([
                GroupPropertiesPage(),
                new([TypedObjectRef("metadata-a", MetadataResourceNode, MetadataResourceType)])
            ]);
            session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
            session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
            session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
            session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
            session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
            session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
            session.BrowsePages[MetadataResourceNode] = new([
                new([
                    VariableRef("ResourceId", "metadata"), VariableRef("Xid", "metadata"),
                    VariableRef("Name", "metadata"), FileRef("Metadata")
                ])
            ]);
            session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
            session.JsonValues["metadata:Xid"] =
                Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
            session.JsonValues["metadata:Name"] = Value("\"Metadata A\"", BuiltInType.String);
            session.StringValues["metadata:Xid"] = targetXid;
        }
        else
        {
            ConfigureResourceAddressSpace(session);
            session.StringValues["logical:Xid"] = "/teams/team-a/schemas/schema-a";
            session.StringValues["version:Xid"] =
                "/teams/team-a/schemas/schema-a/versions/v1";
        }

        if (resolvedExternalNode == LogicalResourceNode)
        {
            session.StringValues["logical:Xid"] = targetXid;
        }
        else if (resolvedExternalNode == DefaultVersionNode)
        {
            session.StringValues["version:Xid"] = targetXid;
        }
        else if (resolvedExternalNode != MetadataResourceNode)
        {
            session.BrowsePages[resolvedExternalNode] = new([
                new([VariableRef("Xid", "external")])
            ]);
            session.StringValues["external:Xid"] = targetXid;
        }

        var identity = new OpcUaExternalNodeId(TargetServerApplicationUri, identityNamespaceUri,
            IdType.String, resolvedExternalNode.IdentifierAsString);
        var context = new NativeRegistryContext("opcua", "urn:trusted:target-context");
        var lease = new TrackingResolverLease(registryReader, registryRoot ?? File, leaseSpecVersion,
            context, types, leaseServerApplicationUri ?? TargetServerApplicationUri);
        return new(session, lease, identity);
    }

    private static OpcUaTrustedProviderResolver ReturnLease(TrackingResolverLease lease) =>
        (_, _) => ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);

    private static async Task<FederationException> ResolveFailureAsync(
        ResolverFixture fixture, Func<string, bool>? isSupportedSpecVersion = null,
        OpcUaExternalTargetRole role = OpcUaExternalTargetRole.LogicalResource)
    {
        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                fixture.Identity, ReturnLease(fixture.Lease), isSupportedSpecVersion ?? (_ => true), role));
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
        return error!;
    }
}
