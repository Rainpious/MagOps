namespace MagOps.Core.Domain;

// Wire markers remain case-sensitive and unchanged. Unknown values are retained
// as evidence; parsing never promotes them to survey work or approved quality.
public enum RoutePurpose { Unknown, Undetermined, Survey, Transit, Connector }
public enum PassQuality { Unknown, Pending, Passed, Failed }
public enum FlightExecutionDirection { Unknown, Forward, Reverse }

public static class RoutePurposeMarkers
{
    public const string Undetermined = "UNDETERMINED", Survey = "SURVEY",
        Transit = "TRANSIT", Connector = "CONNECTOR";
    public static RoutePurpose Parse(string? value) => value switch
    {
        Undetermined => RoutePurpose.Undetermined, Survey => RoutePurpose.Survey,
        Transit => RoutePurpose.Transit, Connector => RoutePurpose.Connector,
        _ => RoutePurpose.Unknown
    };
}

public static class PassQualityMarkers
{
    public const string Pending = "PENDING_QC", Passed = "PASS", Failed = "FAIL";
    public static PassQuality Parse(string? value) => value switch
    {
        Pending => PassQuality.Pending, Passed => PassQuality.Passed,
        Failed => PassQuality.Failed, _ => PassQuality.Unknown
    };
}

public static class ExecutionDirectionMarkers
{
    public const string Forward = "FORWARD", Reverse = "REVERSE";
    public static FlightExecutionDirection Parse(string? value) => value switch
    {
        Forward => FlightExecutionDirection.Forward,
        Reverse => FlightExecutionDirection.Reverse,
        _ => FlightExecutionDirection.Unknown
    };
}
