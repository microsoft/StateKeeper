# Orleans Compatibility Tests

The synchronization utilities that became StateKeeper were originally developed for use alongside [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/overview?pivots=orleans-10-0). StateKeeper helps us manage concurrency and prevent race conditions in complex [reentrant grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-scheduling) where many types of requests may need to access or modify multiple different pieces of mutable state within the same grain.

In the past we've discovered that some async libraries don't play well with Orleans' single-threaded task scheduler. Escaping out of the grain's activation thread can break the single-threadedness guarantees that make Orleans easy to work with, and will cause Orleans to throw exceptions when Orleans itself is invoked from the wrong thread. These tests use StateKeeper from inside of a grain to verify that it behaves correctly and respects the grain context.
