using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>The complete read-only abstract operation set; no mutation operation is defined.</summary>
public enum FederationOperation
{
    /// <summary>Registry, Group, Resource, Meta, or Version metadata.</summary>
    Entity,
    /// <summary>A complete typed collection, optionally uniquely selected by a literal label.</summary>
    Collection,
    /// <summary>Exact domain bytes or an explicit external document descriptor.</summary>
    Document,
    /// <summary>The selected Registry's model material.</summary>
    Model,
    /// <summary>The selected view's enabled capabilities.</summary>
    Capabilities,
}

/// <summary>The metadata representation requested from a source.</summary>
public enum FederationRepresentation
{
    /// <summary>Native Core document view with response-local navigation.</summary>
    DocumentView,
    /// <summary>A binding-defined API view with retrievable navigation.</summary>
    ApiView,
}

/// <summary>A literal label value comparator; map keys are exact and values use ordinal ignore-case.</summary>
public sealed record FederationLabelSelector
{
    /// <summary>Creates a selector without wildcard parsing or Unicode normalization.</summary>
    public FederationLabelSelector(string label, string value)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(value);
        if (!FederationSyntax.MapKey(label))
        {
            throw FederationJson.Invalid("A selector must name an exact Core label key.");
        }
        Label = label;
        Value = value;
    }

    /// <summary>The exact label-map key.</summary>
    public string Label { get; }
    /// <summary>The literal value, including a possible empty string.</summary>
    public string Value { get; }

    /// <summary>Compares against a labels map. An undefined (absent) map never matches.</summary>
    public bool Matches(JsonElement labels)
    {
        if (labels.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }
        FederationSyntax.Labels(labels);
        return labels.TryGetProperty(Label, out var value) &&
            string.Equals(value.GetString(), Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>An exact abstract read, independent of HTTP URLs or storage paths.</summary>
public sealed record FederationReadRequest
{
    /// <summary>Creates and validates a typed read. Unknown enum values are explicitly unsupported.</summary>
    public FederationReadRequest(FederationOperation operation, string target,
        FederationLabelSelector? selector = null,
        FederationRepresentation representation = FederationRepresentation.DocumentView)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(representation))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported read operation or view.");
        }
        if (selector is not null && operation != FederationOperation.Collection)
        {
            throw FederationJson.Invalid("A selector applies only to a collection.");
        }
        Parts = FederationSyntax.Xid(target, operation == FederationOperation.Collection);
        if (operation is FederationOperation.Model or FederationOperation.Capabilities && target != "/")
        {
            throw FederationJson.Invalid("Model and capabilities reads target the Registry root.");
        }
        if (operation == FederationOperation.Document && Parts.Length is not (4 or 6))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "A document read targets a Resource or an exact Version.");
        }
        Operation = operation;
        Target = target;
        Selector = selector;
        Representation = representation;
    }

    /// <summary>The read operation.</summary>
    public FederationOperation Operation { get; }
    /// <summary>The original, case-sensitive XID or typed collection path.</summary>
    public string Target { get; }
    /// <summary>An optional unique-match selector.</summary>
    public FederationLabelSelector? Selector { get; }
    /// <summary>The explicit metadata view, never silently changed by a binding.</summary>
    public FederationRepresentation Representation { get; }
    internal string[] Parts { get; }

    /// <summary>Parses the pinned illustrative request envelope; it is not a new wire protocol.</summary>
    public static FederationReadRequest Parse(ReadOnlyMemory<byte> utf8Json,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        var data = FederationJson.Parse(utf8Json, budget, cancellationToken);
        FederationJson.Fields(data, "operation", "target", "selector");
        var operation = FederationJson.String(data, "operation") switch
        {
            "entity" => FederationOperation.Entity,
            "collection" => FederationOperation.Collection,
            "document" => FederationOperation.Document,
            "model" => FederationOperation.Model,
            "capabilities" => FederationOperation.Capabilities,
            _ => throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "Only abstract read operations are supported."),
        };
        FederationLabelSelector? selector = null;
        if (data.TryGetProperty("selector", out var selection))
        {
            FederationJson.Fields(selection, "label", "value");
            selector = new(FederationJson.String(selection, "label"),
                FederationJson.String(selection, "value", true));
        }
        return new(operation, FederationJson.String(data, "target"), selector);
    }
}

/// <summary>The side responsible for composing the selected Registry view.</summary>
public enum FederationResolutionOwner
{
    /// <summary>The consumer applies local shadowing and explicitly configured sources.</summary>
    Consumer,
    /// <summary>The producer already supplies the requested view; its catalog must not be retraversed.</summary>
    Producer,
}

/// <summary>Interpretation of the enabled (not offered) federation capability.</summary>
public static class FederationCapabilities
{
    /// <summary>Absent means consumer; malformed or unsupported signals are never defaulted.</summary>
    public static FederationResolutionOwner GetResolutionOwner(JsonElement enabledCapabilities)
    {
        FederationJson.RequireObject(enabledCapabilities);
        FederationJson.ValidateKeys(enabledCapabilities);
        if (!enabledCapabilities.TryGetProperty("federation", out var federation))
        {
            return FederationResolutionOwner.Consumer;
        }
        var resolution = FederationJson.String(federation, "resolution");
        if (federation.EnumerateObject().Any(property => property.Name != "resolution"))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported federation capability field.");
        }
        return resolution switch
        {
            "consumer" => FederationResolutionOwner.Consumer,
            "producer" => FederationResolutionOwner.Producer,
            _ => throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported resolution owner."),
        };
    }
}
