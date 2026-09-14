using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Queries;

namespace XRegistry.Federation.Tests;

public class ProducerModelCaptureTests
{
    private const string TargetModel = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","attributes":{
          "reference":{"type":"url","target":"/gs/rs"}
        }}}}}}
        """;

    [Test]
    public async Task FailedCaptureInitializationCannotBeReusedAsAnAdmittedSource()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(TargetModel));
        var data = new ProducerModelDataSource("selected", model);
        data.AddGroup("g");
        var source = new MutableCaptureSource(data) { Model = RegistryModel.Compile(RegistryJson.Parse("{}")) };
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var initial = await Assert.That(async () => { await view.ReadAsync(RegistryPath.Parse("/")); }).Throws<FederationException>();
        await Assert.That(initial!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        var repeated = await Assert.That(async () => { await view.ReadAsync(RegistryPath.Parse("/")); }).Throws<FederationException>();
        await Assert.That(repeated!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(data.Reads.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("revision")]
    [Arguments("model")]
    [Arguments("ownership")]
    public async Task TargetPolicyCannotChangeTheCapturedSourceBeforeProducerCompletion(string change)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(TargetModel));
        var data = new ProducerModelDataSource("selected", model);
        data.AddGroup("g");
        data.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"reference":"/gs/g/rs/target"}""",
        });
        var source = new MutableCaptureSource(data);
        var registration = source.Registration(validateTarget: (_, _) =>
        {
            if (change == "revision") { source.ChangeRevision(); }
            else if (change == "model") { source.Model = RegistryModel.Compile(RegistryJson.Parse(TargetModel)); }
            else { source.ProducerOwned = true; }
            return ValueTask.FromResult(true);
        });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(data.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CollectionCannotCompleteAfterAnEarlierConsumedSourceChanges(bool query)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""{"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r"}}}}}"""));
        var firstData = new ProducerModelDataSource("first", model);
        firstData.AddGroup("g");
        var first = new MutableCaptureSource(firstData);
        var secondData = new ProducerModelDataSource("second", model);
        secondData.AddGroup("g");
        secondData.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = "{}" });
        var second = new MutableCaptureSource(secondData)
        {
            AfterRead = request =>
            {
                if (request.Operation == FederationOperation.Collection && request.Target == "/gs/g/rs") { first.ChangeRevision(); }
            },
        };
        var root = new Uri("https://view.test/registry");
        await using var view = new ProducerRegistryView(model, root, "view", [first.Registration(), second.Registration()]);
        var path = RegistryPath.Parse("/gs/g/rs");

        var error = await Assert.That(async () =>
        {
            if (query)
            {
                using var budget = new RegistryQueryBudget();
                await view.ReadQueryAsync(new RegistryQueryRequest(path, root), new ProducerViewOptions(), budget);
            }
            else { await view.ReadAsync(path); }
        }).Throws<FederationException>();

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
    }

    private sealed class MutableCaptureSource : IFederationReadSource
    {
        private readonly ProducerModelDataSource _inner;
        internal MutableCaptureSource(ProducerModelDataSource inner)
        {
            _inner = inner;
            Model = inner.Model;
            Context = new("http", inner.Context.Source, "r1");
        }

        public NativeRegistryContext Context { get; private set; }
        public RegistryModel Model { get; set; }
        internal bool ProducerOwned { get; set; }
        internal Action<FederationReadRequest>? AfterRead { get; init; }
        public JsonElement Capabilities => RegistryJson.Parse(ProducerOwned
            ? """{"federation":{"resolution":"producer"}}""" : "{}").RootElement;

        internal void ChangeRevision() => Context = new("http", Context.Source, "r2");
        internal FederationSourceRegistration Registration(ProducerTargetValidator? validateTarget = null) =>
            new(Context.Source.Contains("first", StringComparison.Ordinal) ? "first" :
                Context.Source.Contains("second", StringComparison.Ordinal) ? "second" : "selected",
                FederationRepresentation.ApiView,
                (_, _) => ValueTask.FromResult(new FederationSourceLease(this, static () => ValueTask.CompletedTask)),
                validateTarget: validateTarget);

        public async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
        {
            var capture = Context;
            var result = await _inner.ReadAsync(request, cancellationToken);
            AfterRead?.Invoke(request);
            return FederationReadResult.FromMetadata(result.SelectedXid, capture, result.Metadata);
        }
    }
}
