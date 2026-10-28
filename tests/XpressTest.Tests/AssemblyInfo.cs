// Hangfire keeps some process-wide static state (JobStorage.Current), so test hosts must not
// run concurrently. The suite stays fast because every service runs in-process.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
