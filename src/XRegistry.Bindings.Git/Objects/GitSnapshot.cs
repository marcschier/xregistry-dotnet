// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using XRegistry.Federation;

namespace XRegistry.Bindings.Git.Objects;

/// <summary>A commit-pinned, verified Git object-tree reader. This is not proof of authorship or SHA-1 collision hardening.</summary>
public sealed class GitSnapshot
{
    private readonly GitObjectSet _objects;
    private readonly GitReadLimits _limits;

    private GitSnapshot(GitObjectSet objects, GitObjectId selected, GitObjectId commit, GitObjectId tree, GitReadLimits limits)
    {
        _objects = objects;
        SelectedId = selected;
        CommitId = commit;
        RootTreeId = tree;
        _limits = limits;
    }

    /// <summary>Gets the original selected object, before any annotated-tag peeling.</summary>
    public GitObjectId SelectedId { get; }
    /// <summary>Gets the exact retained commit ID.</summary>
    public GitObjectId CommitId { get; }
    /// <summary>Gets the exact root tree referenced by that commit.</summary>
    public GitObjectId RootTreeId { get; }

    /// <summary>Peels bounded verified tag objects and requires an actual commit/root tree.</summary>
    public static GitSnapshot Open(GitObjectSet objects, GitObjectId selected, GitReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(selected);
        limits ??= GitReadLimits.Default;
        limits.Validate();
        var current = objects.Get(selected);
        var seen = new HashSet<GitObjectId>();
        var tags = 0;
        var visits = 0L;
        while (current.Type == GitObjectType.Tag)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitReadBudget.Charge(ref visits, 1, limits.MaxTraversalObjects, "snapshot object visits");
            if (++tags > limits.MaxTagDepth || !seen.Add(current.Id))
            {
                throw new GitDataException(GitFailure.LimitExceeded, "Tag peeling exceeded its depth budget or contains a cycle.");
            }

            var headers = Headers(current, limits, cancellationToken);
            var target = GitObjectId.Parse(objects.Algorithm, Required(headers, "object"));
            var declared = Required(headers, "type") switch
            {
                "commit" => GitObjectType.Commit,
                "tree" => GitObjectType.Tree,
                "blob" => GitObjectType.Blob,
                "tag" => GitObjectType.Tag,
                _ => throw Malformed("The tag declares an invalid target type.")
            };
            _ = Required(headers, "tag");
            current = objects.Get(target);
            if (current.Type != declared)
            {
                throw Malformed("The annotated tag target type does not match its verified object.");
            }
        }

        GitReadBudget.Charge(ref visits, 1, limits.MaxTraversalObjects, "snapshot object visits");
        if (current.Type != GitObjectType.Commit)
        {
            throw new GitDataException(GitFailure.UnsupportedFormat, "A Registry snapshot must resolve to a commit.");
        }

        var commitHeaders = Headers(current, limits, cancellationToken);
        var tree = GitObjectId.Parse(objects.Algorithm, Required(commitHeaders, "tree"));
        _ = Required(commitHeaders, "author");
        _ = Required(commitHeaders, "committer");
        GitReadBudget.Charge(ref visits, 1, limits.MaxTraversalObjects, "snapshot object visits");
        if (objects.Get(tree).Type != GitObjectType.Tree)
        {
            throw Malformed("The commit's root is not a tree object.");
        }

        return new GitSnapshot(objects, selected, current.Id, tree, limits);
    }

    /// <summary>Reads one exact UTF-8 portable path from the pinned object tree without a checkout, filter, or LFS fetch.</summary>
    /// <remarks>Present wrong-kind paths are malformed, not missing. Signature-only, LF and CRLF Git LFS blobs are unsupported.</remarks>
    public GitObject ReadBlob(string rootRelativePath, CancellationToken cancellationToken = default)
    {
        DocumentTreePath.Validate(rootRelativePath);
        var parts = rootRelativePath.Split('/');
        if (parts.Length > _limits.MaxTreeDepth)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The requested tree path exceeds its depth budget.");
        }

        var current = RootTreeId;
        long visits = 0;
        long entries = 0;
        for (var depth = 0; depth < parts.Length; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitReadBudget.Charge(ref visits, 1, _limits.MaxTraversalObjects, "tree visits");
            var tree = _objects.Get(current);
            if (tree.Length > _limits.MaxObjectBytes)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The selected tree exceeds this read's object budget.");
            }
            if (tree.Type != GitObjectType.Tree)
            {
                throw Malformed("A directory entry does not refer to a tree.");
            }

            var found = Find(tree, Encoding.UTF8.GetBytes(parts[depth]), ref entries, cancellationToken);
            if (found is null)
            {
                throw new GitDataException(GitFailure.PathNotFound, "The exact path does not exist in the pinned tree.");
            }

            if (found.Value.Mode == 0xa000)
            {
                throw new GitDataException(GitFailure.PolicyDenied, "A selected symbolic link cannot be followed.");
            }

            if (found.Value.Mode == 0xe000)
            {
                throw new GitDataException(GitFailure.UnsupportedIndirection, "A selected gitlink is not a locally readable tree.");
            }

            var directory = found.Value.Mode == 0x4000;
            if (directory != (depth + 1 < parts.Length))
            {
                throw Malformed("The path names the wrong file/directory shape.");
            }

            current = found.Value.Id;
        }

        GitReadBudget.Charge(ref visits, 1, _limits.MaxTraversalObjects, "blob visits");
        var blob = _objects.Get(current);
        if (blob.Length > _limits.MaxObjectBytes)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The selected blob exceeds this read's object budget.");
        }
        if (blob.Type != GitObjectType.Blob)
        {
            throw Malformed("A regular-file tree entry does not refer to a blob.");
        }

        if (blob.Content.SequenceEqual("version https://git-lfs.github.com/spec/v1"u8) ||
            blob.Content.StartsWith("version https://git-lfs.github.com/spec/v1\n"u8) ||
            blob.Content.StartsWith("version https://git-lfs.github.com/spec/v1\r\n"u8))
        {
            throw new GitDataException(GitFailure.UnsupportedIndirection, "LFS pointers are not silently fetched as Registry Documents.");
        }

        return blob;
    }

    private (int Mode, GitObjectId Id)? Find(
        GitObject tree, byte[] selectedName, ref long totalEntries, CancellationToken cancellationToken)
    {
        var input = tree.Content;
        var count = 0;
        ReadOnlySpan<byte> previous = default;
        var previousMode = 0;
        (int Mode, GitObjectId Id)? found = null;
        while (!input.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > _limits.MaxTreeEntries)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The tree exceeds its entry-count budget.");
            }

            GitReadBudget.Charge(ref totalEntries, 1, _limits.MaxTraversalEntries, "tree entry visits");
            var space = input.IndexOf((byte)' ');
            if (space is < 1 or > 6)
            {
                throw Malformed("A tree entry has an invalid mode field.");
            }

            var mode = 0;
            foreach (var digit in input[..space])
            {
                if (digit is < (byte)'0' or > (byte)'7')
                {
                    throw Malformed("A tree mode must be octal.");
                }

                mode = mode * 8 + digit - '0';
            }

            if (mode is not (0x4000 or 0x81a4 or 0x81ed or 0xa000 or 0xe000))
            {
                throw Malformed("A tree entry uses an unsupported mode.");
            }

            input = input[(space + 1)..];
            var end = input.IndexOf((byte)0);
            if (end < 1 || end > _limits.MaxTreeNameBytes)
            {
                throw new GitDataException(end > _limits.MaxTreeNameBytes ? GitFailure.LimitExceeded : GitFailure.MalformedData,
                    "A tree name is empty, truncated or exceeds its byte budget.");
            }

            var name = input[..end];
            if (name.Contains((byte)'/') || name.SequenceEqual("."u8) || name.SequenceEqual(".."u8) ||
                !previous.IsEmpty && Compare(previous, previousMode, name, mode) >= 0)
            {
                throw Malformed("Tree entries have invalid, duplicate or unsorted names.");
            }

            input = input[(end + 1)..];
            var length = GitObjectId.GetByteLength(_objects.Algorithm);
            if (input.Length < length)
            {
                throw new GitDataException(GitFailure.TruncatedInput, "A tree object reference is truncated.");
            }

            var id = GitObjectId.FromBytes(_objects.Algorithm, input[..length]);
            if (id.IsZero)
            {
                throw Malformed("A tree entry cannot refer to an all-zero object ID.");
            }

            if (name.SequenceEqual(selectedName))
            {
                found = (mode, id);
            }

            previous = name;
            previousMode = mode;
            input = input[length..];
        }

        return found;
    }

    private static int Compare(ReadOnlySpan<byte> left, int leftMode, ReadOnlySpan<byte> right, int rightMode)
    {
        var length = Math.Min(left.Length, right.Length);
        var comparison = left[..length].SequenceCompareTo(right[..length]);
        if (comparison != 0)
        {
            return comparison;
        }

        if (left.Length == right.Length)
        {
            return 0;
        }

        var leftNext = length < left.Length ? left[length] : leftMode == 0x4000 ? '/' : 0;
        var rightNext = length < right.Length ? right[length] : rightMode == 0x4000 ? '/' : 0;
        return leftNext.CompareTo(rightNext);
    }

    private static Dictionary<string, string> Headers(
        GitObject value, GitReadLimits limits, CancellationToken cancellationToken)
    {
        var boundary = value.Content.IndexOf("\n\n"u8);
        if (boundary < 0 || boundary + 2 > limits.MaxRecordHeaderBytes)
        {
            throw new GitDataException(boundary < 0 ? GitFailure.MalformedData : GitFailure.LimitExceeded,
                "The commit/tag header is incomplete or exceeds its byte budget.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var input = value.Content[..boundary];
        var lines = 0;
        var hasPrevious = false;
        while (!input.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++lines > limits.MaxRecordHeaders)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The commit/tag header count budget was exceeded.");
            }

            var newline = input.IndexOf((byte)'\n');
            var line = newline < 0 ? input : input[..newline];
            input = newline < 0 ? [] : input[(newline + 1)..];
            if (line[0] == ' ')
            {
                if (!hasPrevious) { throw Malformed("A header continuation has no preceding header."); }
                continue;
            }

            var space = line.IndexOf((byte)' ');
            if (space <= 0 || space + 1 == line.Length || line.Contains((byte)0) || line.Contains((byte)'\r'))
            {
                throw Malformed("A commit/tag header is malformed.");
            }

            var name = Encoding.ASCII.GetString(line[..space]);
            if (!name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                throw Malformed("A commit/tag header name is malformed.");
            }

            var text = Encoding.UTF8.GetString(line[(space + 1)..]);
            if (name is "tree" or "parent" or "object")
            {
                _ = GitObjectId.Parse(value.Id.Algorithm, text);
            }

            if (name != "parent" && !result.TryAdd(name, text) &&
                name is "tree" or "author" or "committer" or "object" or "type" or "tag" or "tagger")
            {
                throw Malformed("A required singleton commit/tag header is duplicated.");
            }

            hasPrevious = true;
        }

        return result;
    }

    private static string Required(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : throw Malformed("A required commit/tag header is absent.");
    private static GitDataException Malformed(string message) => new(GitFailure.MalformedData, message);
}
