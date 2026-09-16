// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>Inclusive finite bounds shared by all reads of one operation.</summary>
public sealed class FederationReadLimits
{
    /// <summary>Creates immutable limits. All values must be positive; JSON depth is at most 256.</summary>
    public FederationReadLimits(int maxObjectBytes = 16 * 1024 * 1024, long maxTotalBytes = 64 * 1024 * 1024,
        long maxRequests = 4096, long maxObjects = 4096, long maxWork = 1_000_000,
        long maxSources = 64, int maxHops = 16, int maxDepth = 64, int maxJsonDepth = 64,
        int maxResultBytes = 64 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjectBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWork, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSources, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHops, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxJsonDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxJsonDepth, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResultBytes, 1);
        MaxObjectBytes = maxObjectBytes;
        MaxTotalBytes = maxTotalBytes;
        MaxRequests = maxRequests;
        MaxObjects = maxObjects;
        MaxWork = maxWork;
        MaxSources = maxSources;
        MaxHops = maxHops;
        MaxDepth = maxDepth;
        MaxJsonDepth = maxJsonDepth;
        MaxResultBytes = maxResultBytes;
    }

    /// <summary>Maximum exact bytes in one input object.</summary>
    public int MaxObjectBytes { get; }
    /// <summary>Maximum aggregate input bytes.</summary>
    public long MaxTotalBytes { get; }
    /// <summary>Maximum source calls and backing-object opens.</summary>
    public long MaxRequests { get; }
    /// <summary>Maximum distinct input objects.</summary>
    public long MaxObjects { get; }
    /// <summary>Maximum JSON values and explicit traversal steps.</summary>
    public long MaxWork { get; }
    /// <summary>Maximum source contexts opened by composition.</summary>
    public long MaxSources { get; }
    /// <summary>Maximum nested federation hops.</summary>
    public int MaxHops { get; }
    /// <summary>Maximum containment or dependency traversal depth.</summary>
    public int MaxDepth { get; }
    /// <summary>Maximum JSON object/array nesting depth.</summary>
    public int MaxJsonDepth { get; }
    /// <summary>Maximum encoded result bytes.</summary>
    public int MaxResultBytes { get; }
}

/// <summary>Thread-safe, cumulative accounting. Failed charges do not change counters.</summary>
public sealed class FederationReadBudget
{
    private readonly object gate = new();
    private long requests;
    private long bytes;
    private long objects;
    private long work;
    private long sources;

    /// <summary>Creates accounting for an operation and its descendants.</summary>
    public FederationReadBudget(FederationReadLimits? limits = null) => Limits = limits ?? new();

    /// <summary>The immutable bounds.</summary>
    public FederationReadLimits Limits { get; }
    /// <summary>Bytes successfully charged so far.</summary>
    public long BytesRead { get { lock (gate) { return bytes; } } }
    /// <summary>Requests successfully charged so far.</summary>
    public long Requests { get { lock (gate) { return requests; } } }
    /// <summary>Distinct objects successfully charged so far.</summary>
    public long ObjectsRead { get { lock (gate) { return objects; } } }
    /// <summary>Work units successfully charged so far.</summary>
    public long Work { get { lock (gate) { return work; } } }
    /// <summary>Charges one source call or backing-object open before invoking it.</summary>
    public void ChargeRequest() { lock (gate) { Charge(ref requests, 1, Limits.MaxRequests); } }
    /// <summary>Charges exact received bytes, not a declared length.</summary>
    public void ChargeBytes(long count) { lock (gate) { Charge(ref bytes, count, Limits.MaxTotalBytes); } }
    /// <summary>Charges one new input object.</summary>
    public void ChargeObject() { lock (gate) { Charge(ref objects, 1, Limits.MaxObjects); } }
    /// <summary>Charges JSON values or traversal steps.</summary>
    public void ChargeWork(long count = 1) { lock (gate) { Charge(ref work, count, Limits.MaxWork); } }
    /// <summary>Charges a newly opened source context.</summary>
    public void ChargeSource() { lock (gate) { Charge(ref sources, 1, Limits.MaxSources); } }
    /// <summary>Checks an absolute federation nesting depth.</summary>
    public void CheckHops(int hops) => Check(hops, Limits.MaxHops);
    /// <summary>Checks an absolute traversal depth.</summary>
    public void CheckDepth(int depth) => Check(depth, Limits.MaxDepth);
    /// <summary>Checks an exact or declared single-object length before allocation.</summary>
    public void CheckObjectBytes(long count) => Check(count, Limits.MaxObjectBytes);
    /// <summary>Checks a materialized result length.</summary>
    public void CheckResultBytes(long count) => Check(count, Limits.MaxResultBytes);

    private static void Charge(ref long value, long count, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > limit - value)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The operation exhausted a read budget.");
        }
        value += count;
    }

    private static void Check(long value, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value > limit)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The operation exceeds a configured bound.");
        }
    }
}
