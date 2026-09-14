using BenchmarkDotNet.Running;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray();
return summaries.Length == 0 || summaries.Any(summary =>
    summary.HasCriticalValidationErrors || summary.Reports.Length == 0 ||
    summary.Reports.Any(report => !report.Success)) ? 1 : 0;
