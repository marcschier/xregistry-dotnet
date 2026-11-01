// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;

namespace XRegistry.Federation;

internal sealed class CapturedFederationReadScope
{
    private readonly IFederationReadSource _source;
    private readonly FederationResolutionOwner _owner;
    private readonly FederationReadBudget _budget;
    private readonly CancellationToken _token;

    internal CapturedFederationReadScope(IFederationReadSource source, FederationReadBudget budget,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _source = source;
        _budget = budget;
        _token = token;
        Context = source.Context;
        Model = source.Model ?? throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "Document acquisition requires the source's captured model.");
        _owner = FederationCapabilities.GetResolutionOwner(source.Capabilities);
    }

    internal NativeRegistryContext Context { get; }
    internal RegistryModel Model { get; }

    internal void Check()
    {
        _token.ThrowIfCancellationRequested();
        if (_source.Context != Context || !ReferenceEquals(_source.Model, Model) ||
            FederationCapabilities.GetResolutionOwner(_source.Capabilities) != _owner)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The source context, captured model or resolution owner changed between reads.");
        }
    }

    internal async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request)
    {
        Check();
        _budget.ChargeRequest();
        var result = await _source.ReadAsync(request, _token).ConfigureAwait(false);
        Check();
        if (result.Context != Context)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "A read returned bytes or metadata from another source context.");
        }
        if (!FederationSyntax.SameXid(result.SelectedXid, request.Target,
            request.Operation == FederationOperation.Collection))
        {
            throw FederationJson.Invalid("A read returned a different selected path.");
        }
        return result;
    }

    internal async ValueTask<JsonElement> MetadataAsync(FederationReadRequest request)
    {
        var result = await ReadAsync(request).ConfigureAwait(false);
        return Metadata(result.Metadata);
    }

    internal JsonElement Metadata(JsonElement value)
    {
        Check();
        FederationJson.RequireObject(value);
        var count = Encoding.UTF8.GetByteCount(value.GetRawText());
        _budget.CheckObjectBytes(count);
        _budget.ChargeBytes(count);
        _budget.ChargeObject();
        RegistryJson parsed;
        try
        {
            parsed = RegistryJson.FromElement(value, new RegistryJsonLimits
            {
                MaxBytes = _budget.Limits.MaxObjectBytes,
                MaxDepth = _budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Min(int.MaxValue, _budget.Limits.MaxWork)
            });
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        FederationJson.CountWork(parsed.RootElement, _budget, _token);
        return parsed.RootElement;
    }
}
