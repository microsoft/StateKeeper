namespace StateKeeper.Tests.OrleansCompat.Helper;

/// <summary>
/// Callback the grain invokes to let test code assert that execution
/// is still happening on the grain's task scheduler.
/// </summary>
public delegate Task CheckOrleans();

/// <summary>
/// An async test callback that runs inside a grain and receives a
/// <see cref="CheckOrleans"/> to verify scheduler affinity.
/// </summary>
public delegate Task GrainTestCallback(CheckOrleans checkOrleans);
