namespace XRegistry.Core.Tests;

internal static class TestErrors
{
    internal static RegistryDiagnostic Capture(Action action)
    {
        try
        {
            action();
        }
        catch (RegistryException exception)
        {
            return exception.Diagnostic;
        }

        throw new InvalidOperationException("The operation unexpectedly accepted invalid input.");
    }
}
