namespace Gtools.Tests;

// Enabling the process-wide diagnostic sink cannot run alongside tests which write normal logs.
[CollectionDefinition("Process-wide diagnostics",DisableParallelization=true)]
public sealed class DiagnosticTestCollection { }
