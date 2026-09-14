using TUnit.Assertions;
using XRegistry.Bindings.Git;

namespace XRegistry.Git.Tests;

internal static class TestAssert
{
    internal static async Task Fails(Action action, GitFailure expected)
    {
        GitDataException? failure = null;
        try
        {
            action();
        }
        catch (GitDataException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Failure).IsEqualTo(expected);
    }
}
