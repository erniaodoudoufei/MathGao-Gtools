namespace Gtools.Tests;

// Keep the STA fixtures from competing for WPF/system-event initialization on hosted runners.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TodoUiTestCollection
{
    public const string Name = "Todo WPF fixtures";
}
