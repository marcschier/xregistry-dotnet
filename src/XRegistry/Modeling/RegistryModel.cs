// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;

namespace XRegistry;

/// <summary>A caller-supplied, synchronous model document resolver. Core never performs network or filesystem acquisition.</summary>
public interface IRegistryModelResolver
{
    /// <summary>Returns owned JSON for an absolute, fragment-free document URI, or throws an acquisition error.</summary>
    RegistryJson Resolve(Uri documentUri);
}

/// <summary>Explicit model acquisition context and inclusive compilation budgets.</summary>
public sealed record RegistryModelCompilationOptions
{
    /// <summary>Gets the absolute source document URI used for relative include resolution.</summary>
    public Uri? SourceUri { get; init; }
    /// <summary>Gets the only permitted external document resolver; null prohibits external includes.</summary>
    public IRegistryModelResolver? Resolver { get; init; }
    /// <summary>Gets the maximum distinct external include documents; defaults to 32.</summary>
    public int MaxIncludeDocuments { get; init; } = 32;
    /// <summary>Gets the maximum active include chain; defaults to 32.</summary>
    public int MaxIncludeDepth { get; init; } = 32;
    /// <summary>Gets the maximum transitive Resource import chain, independently of include depth.</summary>
    public int MaxResourceImportDepth { get; init; } = 64;
    /// <summary>Gets the maximum values visited during include expansion; defaults to 100,000.</summary>
    public int MaxExpandedNodes { get; init; } = 100_000;
    /// <summary>Gets the JSON budgets applied to expanded/effective output; defaults to 8 MiB and depth 128.</summary>
    public RegistryJsonLimits JsonLimits { get; init; } = new() { MaxBytes = 8 * 1024 * 1024, MaxDepth = 128 };

    internal void Validate()
    {
        if (SourceUri is { IsAbsoluteUri: false } || SourceUri is { Fragment.Length: > 0 })
        {
            throw new ArgumentException("SourceUri must be absolute and fragment-free.", nameof(SourceUri));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(MaxIncludeDocuments);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIncludeDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxIncludeDepth, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResourceImportDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxResourceImportDepth, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxExpandedNodes, 1);
        ArgumentNullException.ThrowIfNull(JsonLimits);
        JsonLimits.Validate();
    }
}

/// <summary>
/// A compiled, immutable, source-owning core model. Compilation checks structural model contracts,
/// not existing registry data, document formats, authorization or lifecycle state.
/// </summary>
public sealed class RegistryModel
{
    private readonly Lazy<RegistryJson> _effective;

    internal RegistryModel(RegistryJson source, RegistryModelAnnotations annotations,
        IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes,
        IReadOnlyDictionary<string, RegistryGroupDefinition> groups, RegistryJsonLimits limits)
    {
        Source = source;
        Annotations = annotations;
        Attributes = attributes.ToFrozenDictionary(StringComparer.Ordinal);
        Groups = groups.ToFrozenDictionary(StringComparer.Ordinal);
        _effective = new Lazy<RegistryJson>(() => RegistryJson.Create(writer => ModelWriter.Write(writer, this), limits));
    }

    private RegistryModel(RegistryJson source, RegistryModel resolved)
    {
        Source = source;
        Annotations = resolved.Annotations;
        Attributes = resolved.Attributes;
        Groups = resolved.Groups;
        _effective = resolved._effective;
    }

    /// <summary>Gets the original owned modelsource, including includes, imports, omissions and explicit nulls.</summary>
    public RegistryJson Source { get; }
    /// <summary>Gets the fully expanded structural model, including core definitions and defaults, without source directives.</summary>
    public RegistryJson EffectiveModel => _effective.Value;
    /// <summary>Gets informational model annotations.</summary>
    public RegistryModelAnnotations Annotations { get; }
    /// <summary>Gets effective Registry-level attributes.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> Attributes { get; }
    /// <summary>Gets effective Group definitions.</summary>
    public IReadOnlyDictionary<string, RegistryGroupDefinition> Groups { get; }

    /// <summary>Compiles modelsource using only the supplied resolver, with fail-fast structured model diagnostics.</summary>
    /// <exception cref="RegistryException">An invalid, unsupported or unresolved model construct is encountered.</exception>
    public static RegistryModel Compile(RegistryJson source, RegistryModelCompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new RegistryModelCompilationOptions();
        options.Validate();
        return ModelCompiler.Compile(source, options);
    }

    /// <summary>Compiles a captured, include-free source while retaining and checking its original source without external acquisition.</summary>
    /// <remarks>
    /// Resolver must be null. Original include syntax, local references and authored-member precedence are checked;
    /// local-only includes are expanded and compared completely. Unretained external include contents cannot be
    /// independently authenticated or reconstructed. The caller owns the capture's provenance and access policy.
    /// </remarks>
    public static RegistryModel CompileCaptured(RegistryJson source, RegistryJson resolvedSource,
        RegistryModelCompilationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolvedSource);
        options ??= new();
        options.Validate();
        if (options.Resolver is not null)
        {
            throw new ArgumentException("Captured model compilation does not accept an external resolver.", nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ModelCaptureCompiler.Validate(source, resolvedSource, options, cancellationToken);
        var model = ModelCompiler.Compile(resolvedSource, options);
        cancellationToken.ThrowIfCancellationRequested();
        return new RegistryModel(source, model);
    }
}
