// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;

namespace XRegistry.Bindings.Git.Objects;

/// <summary>An immutable, single-format set of fully verified owned objects, not a filesystem repository.</summary>
public sealed class GitObjectSet
{
    private readonly Dictionary<GitObjectId, GitObject> objects;
    private readonly ReadOnlyCollection<GitObject> values;

    internal GitObjectSet(GitHashAlgorithm algorithm, Dictionary<GitObjectId, GitObject> objects)
    {
        Algorithm = algorithm;
        this.objects = objects;
        values = Array.AsReadOnly(objects.Values.ToArray());
    }

    /// <summary>Gets the single object format of this set.</summary>
    public GitHashAlgorithm Algorithm { get; }

    /// <summary>Gets the unique object count; identical duplicate pack entries collapse to one object.</summary>
    public int Count => objects.Count;

    /// <summary>Gets a read-only view of all verified objects, with no ordering guarantee.</summary>
    public IReadOnlyCollection<GitObject> Objects => values;

    /// <summary>Gets an exact ID or throws MissingObject; never fetches or substitutes another object.</summary>
    public GitObject Get(GitObjectId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Algorithm != Algorithm)
        {
            throw new GitDataException(GitFailure.MalformedData, "A Git object reference uses a different object format.");
        }

        return objects.TryGetValue(id, out var value)
            ? value
            : throw new GitDataException(GitFailure.MissingObject, "A required pinned Git object is missing.");
    }

    /// <summary>
    /// Builds an owned immutable index from already-verified objects. Counts and byte budgets include
    /// duplicate inputs. Mixed formats and conflicting content for an ID are rejected.
    /// </summary>
    public static GitObjectSet Create(
        GitHashAlgorithm algorithm,
        IEnumerable<GitObject> objects,
        GitReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        _ = GitObjectId.GetByteLength(algorithm);
        limits ??= GitReadLimits.Default;
        limits.Validate();
        var index = new Dictionary<GitObjectId, GitObject>();
        long count = 0;
        long bytes = 0;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var value in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(value);
            GitReadBudget.Charge(ref count, 1, limits.MaxObjects, "object count");
            GitReadBudget.Charge(ref bytes, value.Length, limits.MaxTotalDecompressedBytes, "object-set bytes");
            if (value.Id.Algorithm != algorithm)
            {
                throw new GitDataException(GitFailure.MalformedData, "A Git object set cannot mix object formats.");
            }

            if (value.Length > limits.MaxObjectBytes)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The Git object byte budget was exceeded.");
            }

            Add(index, value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new GitObjectSet(algorithm, index);
    }

    internal static void Add(Dictionary<GitObjectId, GitObject> index, GitObject value)
    {
        if (index.TryGetValue(value.Id, out var previous))
        {
            if (previous.Type != value.Type || !previous.Content.SequenceEqual(value.Content))
            {
                throw new GitDataException(GitFailure.IntegrityMismatch, "Conflicting Git objects have the same ID.");
            }
        }
        else
        {
            index.Add(value.Id, value);
        }
    }
}
