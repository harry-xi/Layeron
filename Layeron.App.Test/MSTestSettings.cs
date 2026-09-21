// The logging tests share one process-wide Log, so they must not run in parallel with each other.
// Set at assembly level rather than per class because a future test class touching Log needs the
// same guarantee, and forgetting it would make failures order-dependent.
[assembly: DoNotParallelize]
