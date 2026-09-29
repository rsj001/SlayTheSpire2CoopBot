namespace CoopBot.Session;

public sealed record HostFailureObservation(
    long RootRevision,
    string FailureCode,
    int ConsecutiveCount,
    bool Tripped);

public sealed class HostFailureCircuitBreaker
{
    public const int TripThreshold = 3;
    private long? _rootRevision;
    private string? _failureCode;
    private int _count;

    public HostFailureObservation Record(long rootRevision, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Failure code is required.", nameof(failureCode));
        if (_rootRevision != rootRevision
            || !string.Equals(_failureCode, failureCode, StringComparison.Ordinal))
        {
            _rootRevision = rootRevision;
            _failureCode = failureCode;
            _count = 0;
        }
        _count++;
        return new HostFailureObservation(
            rootRevision,
            failureCode,
            _count,
            _count >= TripThreshold);
    }

    public void Reset()
    {
        _rootRevision = null;
        _failureCode = null;
        _count = 0;
    }
}
