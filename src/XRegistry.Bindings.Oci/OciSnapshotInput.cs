using System.Collections.ObjectModel;
using System.Text;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>Owned coherent producer input: portable entity records plus exactly the embedded Version documents.</summary>
/// <remarks>
/// Capture records under the source's coherent-read contract before constructing this input. This type
/// copies collections and retains only immutable owned values; later caller collection changes cannot
/// tear a publication. It does not infer a global snapshot from a Registry epoch or fetch include/document URIs.
/// Records use the published OCI record schema, including the Registry's model, original source and modelresolved.
/// </remarks>
public sealed class OciSnapshotInput
{
    /// <summary>Captures immutable records and exact documents under finite input bounds.</summary>
    public OciSnapshotInput(IReadOnlyList<RegistryJson> records,
        IReadOnlyDictionary<string, FederationDocument>? documents = null, FederationReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var budget = new FederationReadBudget(limits ?? OciWriteOptions.DefaultLimits);
        var captured = new List<RegistryJson>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            budget.ChargeObject();
            var size = Encoding.UTF8.GetByteCount(record.RootElement.GetRawText());
            budget.CheckObjectBytes(size);
            budget.ChargeBytes(size);
            captured.Add(record);
        }
        var blobs = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        if (documents is not null)
        {
            foreach (var item in documents)
            {
                ArgumentNullException.ThrowIfNull(item.Value);
                OciFormat.Xid(item.Key);
                budget.ChargeObject();
                budget.CheckObjectBytes(item.Value.Length);
                budget.ChargeBytes(item.Value.Length);
                if (!blobs.TryAdd(item.Key, item.Value)) { throw OciJson.Invalid("Duplicate producer document XID."); }
            }
        }
        Records = captured.AsReadOnly();
        Documents = new ReadOnlyDictionary<string, FederationDocument>(blobs);
    }

    /// <summary>The immutable captured portable configs; exactly one record describes the Registry root.</summary>
    public ReadOnlyCollection<RegistryJson> Records { get; }
    /// <summary>The complete exact-byte map for embedded Versions, never placeholders for absent content.</summary>
    public IReadOnlyDictionary<string, FederationDocument> Documents { get; }
}

/// <summary>Deterministic producer partition choices and finite staging/validation budgets.</summary>
public sealed record OciWriteOptions
{
    internal static FederationReadLimits DefaultLimits { get; } = new(
        maxTotalBytes: 256 * 1024 * 1024, maxRequests: 32_768, maxObjects: 32_768, maxWork: 4_000_000);

    /// <summary>Maximum descriptors per routing index, from 2 through the normative 256. Fixed controls are not reduced by this choice.</summary>
    public int MaxDescriptorsPerIndex { get; init; } = 256;
    /// <summary>Maximum exact bytes per index, including fixed entity indexes; at most the normative 1,048,576.</summary>
    public int MaxIndexBytes { get; init; } = 1_048_576;
    /// <summary>Finite cumulative input, staging and complete validation bounds for one producer operation.</summary>
    public FederationReadLimits Limits { get; init; } = DefaultLimits;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDescriptorsPerIndex, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDescriptorsPerIndex, OciFormat.MaxDescriptors);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIndexBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxIndexBytes, OciFormat.MaxIndexBytes);
        ArgumentNullException.ThrowIfNull(Limits);
    }
}
