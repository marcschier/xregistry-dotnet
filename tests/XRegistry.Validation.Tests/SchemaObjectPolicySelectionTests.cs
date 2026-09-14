using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectPolicySelectionTests
{
    [Test]
    [Arguments("JsonSchema/draft-07", """{"type":"object"}""", "", SchemaTypeKind.JsonSchema)]
    [Arguments("JsonSchema/draft/2019-09", """{"type":"object"}""", "", SchemaTypeKind.JsonSchema)]
    [Arguments("JsonSchema/draft/2020-12", """{"type":"object"}""", "", SchemaTypeKind.JsonSchema)]
    [Arguments("JsonStructure", """{"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/s","name":"S","type":"string"}""", "", SchemaTypeKind.JsonStructure)]
    [Arguments("JsonStructure/draft-04", """{"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/s","name":"S","type":"string"}""", "", SchemaTypeKind.JsonStructure)]
    [Arguments("Avro/1.8.2", """{"type":"record","name":"R","fields":[]}""", "#R", SchemaTypeKind.AvroRecord)]
    [Arguments("avro/1.11.0", """{"type":"record","name":"R","fields":[]}""", "#R", SchemaTypeKind.AvroRecord)]
    [Arguments("Protobuf/2", """syntax="proto2"; message M {}""", "#M", SchemaTypeKind.ProtobufMessage)]
    [Arguments("Protobuf/3", """syntax="proto3"; message M {}""", "#M", SchemaTypeKind.ProtobufMessage)]
    [Arguments("xsd/1.0", """<schema xmlns="http://www.w3.org/2001/XMLSchema"><element name="E" type="string"/></schema>""", "", SchemaTypeKind.XmlSchemaElement)]
    public async Task EverySupportedFormatSelectsSuppliedBytesWithoutAcquiringTheSchemaUri(
        string format, string schema, string suffix, SchemaTypeKind kind)
    {
        var calls = 0;
        var result = await SchemaObjectSelector.SelectAsync(format, Encoding.UTF8.GetBytes(schema),
            "https://must-not-acquire.invalid/document" + suffix, new()
            {
                Validation = new()
                {
                    ResolveReference = (_, _) =>
                    {
                        calls++;
                        throw new IOException("No acquisition was authorized.");
                    },
                },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(kind);
        await Assert.That(result.Selection.Format).IsEqualTo(format);
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(schema);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("Unknown/1")]
    [Arguments("JsonSchema/draft-99")]
    [Arguments("Avro/99")]
    [Arguments("Protobuf/2023")]
    [Arguments("XSD/1.1")]
    [Arguments("JsonStructure/rfc-0000")]
    public async Task UnknownFormatsAreDistinctFromInvalidSelectors(string format)
    {
        var result = await SchemaObjectSelector.SelectAsync(format, "invalid"u8.ToArray(), "schema#/%ZZ");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Unsupported);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("format.unsupported");
        await Assert.That(result.Diagnostics[0].Path).IsEqualTo("$");
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12", """{"type":"not-a-type"}""", "", "schema.type")]
    [Arguments("JsonSchema/draft/2020-12", "true", "", "selection.not_type")]
    [Arguments("JsonStructure/draft-04", "{}", "", "schema.required")]
    [Arguments("Avro/1.11.0", """{"type":"record","name":"R","fields":[{"name":"x","type":"Missing"}]}""", "#R", "avro.unknown_name")]
    [Arguments("Protobuf/3", """syntax="proto2"; message M {}""", "#M", "protobuf.syntax")]
    [Arguments("XSD/1.0", "<root/>", "", "xsd.root")]
    public async Task InvalidDocumentsNeverYieldSuccessShapedSelections(string format, string schema, string suffix, string code)
    {
        var result = await SchemaObjectSelector.SelectAsync(format, Encoding.UTF8.GetBytes(schema), "schema" + suffix);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics[0].Detail.Length).IsGreaterThan(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12", """{"$ref":"https://must-not-fetch.invalid/schema"}""", "")]
    [Arguments("Protobuf/3", """syntax="proto3"; import "dependency.proto"; message M {}""", "#M")]
    [Arguments("XSD/1.0", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="dependency.xsd"/><xs:element name="E" type="xs:string"/></xs:schema>""", "")]
    public async Task UnavailableExplicitDependenciesAreIndeterminateNotImplicitlyFetched(string format, string schema, string suffix)
    {
        var result = await SchemaObjectSelector.SelectAsync(format, Encoding.UTF8.GetBytes(schema), "schema" + suffix);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("reference.unresolved");
    }

    [Test]
    [Arguments(1, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(2, SchemaObjectSelectionStatus.Selected)]
    [Arguments(3, SchemaObjectSelectionStatus.Selected)]
    public async Task PerDocumentByteLimitIsInclusive(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", "{}"u8.ToArray(), "s",
            new() { Validation = new() { MaxDocumentBytes = limit } });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.bytes");
            await Assert.That(result.Selection).IsNull();
        }
        else
        {
            await Assert.That(Encoding.UTF8.GetString(result.Selection!.Document.Span)).IsEqualTo("{}");
        }
    }

    [Test]
    [Arguments(15, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(16, SchemaObjectSelectionStatus.Selected)]
    [Arguments(17, SchemaObjectSelectionStatus.Selected)]
    public async Task AggregateByteLimitIncludesOwnedDependenciesAtTheExactBoundary(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """{"$ref":"dep"}"""u8.ToArray(), "root",
            new()
            {
                Validation = new()
                {
                    MaxTotalBytes = limit,
                    ResolveReference = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>("{}"u8.ToArray()),
                },
            });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.total_bytes");
            await Assert.That(result.Selection).IsNull();
        }
        else
        {
            await Assert.That(Encoding.UTF8.GetString(result.Selection!.References["dep"].Span)).IsEqualTo("{}");
        }
    }

    [Test]
    [Arguments(2, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(3, SchemaObjectSelectionStatus.Selected)]
    [Arguments(4, SchemaObjectSelectionStatus.Selected)]
    public async Task ParsedNodeLimitIsInclusive(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """{"$defs":{"X":{}}}"""u8.ToArray(), "s#/$defs/X",
            new() { Validation = new() { MaxNodes = limit } });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.nodes");
        }
        else
        {
            await Assert.That(result.Selection!.JsonPointer).IsEqualTo("/$defs/X");
        }
    }

    [Test]
    [Arguments(2, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(3, SchemaObjectSelectionStatus.Selected)]
    [Arguments(4, SchemaObjectSelectionStatus.Selected)]
    public async Task ParsedDepthLimitCountsTheRootAndIsInclusive(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """{"$defs":{"X":{}}}"""u8.ToArray(), "s#/$defs/X",
            new() { Validation = new() { MaxDepth = limit } });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.depth");
        }
        else
        {
            await Assert.That(result.Selection!.JsonPointer).IsEqualTo("/$defs/X");
        }
    }

    [Test]
    [Arguments(5, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(6, SchemaObjectSelectionStatus.Selected)]
    [Arguments(7, SchemaObjectSelectionStatus.Selected)]
    public async Task WorkBudgetIncludesIdentityCopyParsingAndSelection(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", "{}"u8.ToArray(), "s",
            new() { Validation = new() { MaxWork = limit } });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
            await Assert.That(result.Selection).IsNull();
        }
        else
        {
            await Assert.That(result.Selection!.JsonPointer).IsEqualTo("");
        }
    }

    [Test]
    [Arguments(0, SchemaObjectSelectionStatus.Indeterminate, 0)]
    [Arguments(1, SchemaObjectSelectionStatus.Selected, 1)]
    public async Task ReferenceLimitIsEnforcedBeforeTheCallback(int limit, SchemaObjectSelectionStatus expected, int expectedCalls)
    {
        var calls = 0;
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """{"$ref":"dep"}"""u8.ToArray(), "root",
            new()
            {
                Validation = new()
                {
                    MaxReferences = limit,
                    ResolveReference = (_, _) =>
                    {
                        calls++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>("{}"u8.ToArray());
                    },
                },
            });

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(calls).IsEqualTo(expectedCalls);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.references");
        }
        else
        {
            await Assert.That(result.Selection!.References.Count).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(7, 10, 2, "limit.selector")]
    [Arguments(8, 9, 2, "limit.uri")]
    [Arguments(8, 10, 1, "limit.selector_steps")]
    [Arguments(8, 10, 2, null)]
    public async Task UriSelectorAndStepBudgetsHaveExactIndependentBoundaries(
        int selectorLength, int uriLength, int steps, string? code)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """{"$defs":{"X":{}}}"""u8.ToArray(), "s#/$defs/X",
            new() { MaxSelectorLength = selectorLength, MaxUriLength = uriLength, MaxSelectorSegments = steps });

        await Assert.That(result.Status).IsEqualTo(code is null ? SchemaObjectSelectionStatus.Selected : SchemaObjectSelectionStatus.Indeterminate);
        if (code is not null)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        }
        else
        {
            await Assert.That(result.Selection!.JsonPointer).IsEqualTo("/$defs/X");
        }
    }

    [Test]
    public async Task JsonStructureImplicitRootSelectorStillHonorsTheSelectorBudget()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonStructure/draft-04", """
            {"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/s",
             "name":"S","$root":"/definitions/X","definitions":{"X":{"type":"string"}}}
            """u8.ToArray(), "s", new() { MaxSelectorLength = 13 });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.selector");
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12")]
    [Arguments("JsonStructure/draft-04")]
    [Arguments("Avro/1.11.0")]
    [Arguments("Protobuf/3")]
    [Arguments("XSD/1.0")]
    public async Task PreCancelledSelectionAlwaysPropagatesCancellation(string format)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () =>
        {
            await SchemaObjectSelector.SelectAsync(format, "invalid"u8.ToArray(), "schema#M", cancellationToken: cancellation.Token);
        }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CancellationDuringExplicitResolutionPropagatesEvenWhenTheResolverIgnoresIt()
    {
        using var cancellation = new CancellationTokenSource();
        var suppliedToken = CancellationToken.None;
        var options = new SchemaObjectSelectionOptions
        {
            Validation = new()
            {
                ResolveReference = async (_, token) =>
                {
                    suppliedToken = token;
                    await cancellation.CancelAsync();
                    return "{}"u8.ToArray();
                },
            },
        };

        await Assert.That(async () =>
        {
            await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
                """{"$ref":"dependency"}"""u8.ToArray(), "schema", options, cancellation.Token);
        }).Throws<OperationCanceledException>();
        await Assert.That(suppliedToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task ExplicitResolverFailuresAreNotMaskedAsSchemaDecisions()
    {
        var options = new SchemaObjectSelectionOptions
        {
            Validation = new() { ResolveReference = (_, _) => throw new IOException("Authorized resolver failed.") },
        };

        await Assert.That(async () =>
        {
            await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
                """{"$ref":"dependency"}"""u8.ToArray(), "schema", options);
        }).Throws<IOException>();
    }

    [Test]
    [Arguments(0, 64, 4_096)]
    [Arguments(16_384, 0, 4_096)]
    [Arguments(16_384, 129, 4_096)]
    [Arguments(16_384, 64, 0)]
    [Arguments(65_537, 64, 4_096)]
    [Arguments(16_384, 64, 65_537)]
    public async Task InvalidSelectorOptionsThrowInsteadOfProducingSuccess(int uriLength, int steps, int selectorLength)
    {
        var options = new SchemaObjectSelectionOptions
        {
            MaxUriLength = uriLength,
            MaxSelectorSegments = steps,
            MaxSelectorLength = selectorLength,
        };
        await Assert.That(async () =>
        {
            await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", "{}"u8.ToArray(), "s", options);
        }).Throws<ArgumentOutOfRangeException>();
    }
}
