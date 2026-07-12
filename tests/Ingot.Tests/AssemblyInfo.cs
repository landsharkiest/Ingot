using Xunit;

// Diagnostics tests attach process-global ActivityListener/MeterListener to Ingot's
// ActivitySource and Meter. Running collections serially keeps another test's extraction
// from emitting into those listeners mid-assertion. The suite is small and fast.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
