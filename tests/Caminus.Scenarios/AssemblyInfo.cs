using Xunit;

// Atlas only hosts one live server per process: scenario classes run sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
