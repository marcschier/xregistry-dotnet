using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitObjectIdTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task CanonicalTypedHashesMatchIndependentGitForAllObjectKinds(GitHashAlgorithm algorithm)
    {
        foreach (var (name, type) in new[]
        {
            ("empty", GitObjectType.Blob),
            ("hello", GitObjectType.Blob),
            ("binary", GitObjectType.Blob),
            ("root", GitObjectType.Tree),
            ("commit", GitObjectType.Commit),
            ("tag", GitObjectType.Tag),
        })
        {
            var content = Fixtures.Content(algorithm, name);
            var id = GitObjectId.Compute(algorithm, type, content);
            await Assert.That(id.ToString()).IsEqualTo(Fixtures.Id(algorithm, name));
            await Assert.That(id.Algorithm).IsEqualTo(algorithm);
            using var stream = new MemoryStream(content);
            var streamed = GitObjectId.Compute(algorithm, type, stream, content.LongLength);
            await Assert.That(streamed).IsEqualTo(id);
            await Assert.That(stream.CanRead).IsTrue();
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1, 20)]
    [Arguments(GitHashAlgorithm.Sha256, 32)]
    public async Task ParserCanonicalizesCaseAndOwnsExactAlgorithmTaggedBytes(GitHashAlgorithm algorithm, int width)
    {
        var expected = Fixtures.Id(algorithm, "hello");
        var id = GitObjectId.Parse(algorithm, expected.ToUpperInvariant());
        var bytes = Convert.FromHexString(expected);
        var copied = GitObjectId.FromBytes(algorithm, bytes);
        Array.Fill(bytes, (byte)0);
        await Assert.That(id.ToString()).IsEqualTo(expected);
        await Assert.That(id.Bytes.Length).IsEqualTo(width);
        await Assert.That(GitObjectId.GetByteLength(algorithm)).IsEqualTo(width);
        await Assert.That(copied).IsEqualTo(id);
        await Assert.That(copied.GetHashCode()).IsEqualTo(id.GetHashCode());
        await Assert.That(Convert.ToHexString(copied.Bytes).ToLowerInvariant()).IsEqualTo(expected);
        var destination = new byte[width];
        id.CopyTo(destination);
        await Assert.That(Convert.ToHexString(destination).ToLowerInvariant()).IsEqualTo(expected);
        await Assert.That(id.Equals(new object())).IsFalse();
        await Assert.That(GitObjectId.Parse(algorithm, new string('0', width * 2)).IsZero).IsTrue();
        await Assert.That(id.Equals(null)).IsFalse();
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task ParserRejectsAbbreviationsWrongFormatWhitespaceAndNonHex(GitHashAlgorithm algorithm)
    {
        var valid = Fixtures.Id(algorithm, "hello");
        foreach (var invalid in new[]
        {
            "", valid[..^1], valid + "0", " " + valid[1..], valid[..^1] + "\n",
            "g" + valid[1..], "\0" + valid[1..], "\uFF11" + valid[1..],
            Fixtures.Id(algorithm == GitHashAlgorithm.Sha1 ? GitHashAlgorithm.Sha256 : GitHashAlgorithm.Sha1, "hello"),
        })
        {
            await TestAssert.Fails(() => GitObjectId.Parse(algorithm, invalid), GitFailure.MalformedData);
        }

        await TestAssert.Fails(() => GitObjectId.FromBytes(algorithm, new byte[valid.Length / 2 - 1]), GitFailure.MalformedData);
        await TestAssert.Fails(() => GitObjectId.FromBytes(algorithm, new byte[valid.Length / 2 + 1]), GitFailure.MalformedData);
    }

    [Test]
    public async Task AlgorithmIsPartOfIdentityAndTheGitHeaderIsPartOfTheHash()
    {
        var sha1 = GitObjectId.FromBytes(GitHashAlgorithm.Sha1, new byte[20]);
        var sha256 = GitObjectId.FromBytes(GitHashAlgorithm.Sha256, new byte[32]);
        await Assert.That(sha1.Equals(sha256)).IsFalse();
        await Assert.That(GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Blob, "hello\n"u8).ToString())
            .IsEqualTo("ce013625030ba8dba906f756967f9e9ca394464a");
        await Assert.That(GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Blob, [])).IsNotEqualTo(
            GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Tree, []));
    }

    [Test]
    public async Task StreamLengthsAreExactBoundedAndNeverNarrowedToInt()
    {
        using var source = new MemoryStream("hello\n"u8.ToArray());
        await TestAssert.Fails(
            () => GitObjectId.Compute(GitHashAlgorithm.Sha256, GitObjectType.Blob, source, 6, maxBytes: 5),
            GitFailure.LimitExceeded);
        await Assert.That(source.Position).IsEqualTo(0L);
        await TestAssert.Fails(
            () => GitObjectId.Compute(GitHashAlgorithm.Sha256, GitObjectType.Blob, source, 5),
            GitFailure.MalformedData);
        source.Position = 0;
        await TestAssert.Fails(
            () => GitObjectId.Compute(GitHashAlgorithm.Sha256, GitObjectType.Blob, source, 7),
            GitFailure.TruncatedInput);
        source.Position = 0;
        await TestAssert.Fails(
            () => GitObjectId.Compute(GitHashAlgorithm.Sha256, GitObjectType.Blob, source, (long)uint.MaxValue + 1, maxBytes: long.MaxValue),
            GitFailure.TruncatedInput);
        await Assert.That(source.Position).IsEqualTo(6L);
        using var empty = new MemoryStream();
        await Assert.That(GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Blob, empty, 0, maxBytes: 0).ToString())
            .IsEqualTo(Fixtures.Id(GitHashAlgorithm.Sha1, "empty"));
    }

    [Test]
    public async Task HashingRejectsCancellationAndInvalidArgumentsWithoutDisposingInput()
    {
        using var source = new MemoryStream("hello\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => GitObjectId.Compute(
            GitHashAlgorithm.Sha256, GitObjectType.Blob, source, 6, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.Position).IsEqualTo(0L);
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(() => GitObjectId.Compute((GitHashAlgorithm)0, GitObjectType.Blob, [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => GitObjectId.Compute(GitHashAlgorithm.Sha1, (GitObjectType)5, [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Blob, source, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => GitObjectId.Compute(GitHashAlgorithm.Sha1, GitObjectType.Blob, source, 6, maxBytes: -1)).Throws<ArgumentOutOfRangeException>();
    }
}
