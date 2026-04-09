namespace Anka.LoadTest;

/// <summary>Parsed results from a single <c>wrk</c> run.</summary>
internal sealed record WrkResult(
    int    Connections,
    double RequestsPerSec,
    long   TotalRequests,
    long   NonOkResponses,
    double LatencyAvgMs,
    double P50Ms,
    double P90Ms,
    double P99Ms,
    double P999Ms,
    double TransferMbSec)
{
    public double ErrorPct =>
        TotalRequests > 0 ? NonOkResponses * 100.0 / TotalRequests : 0;
}
