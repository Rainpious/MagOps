using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Reuses explicit project confirmation without promoting a route cache to project authority.</summary>
public static class RouteCentralMeridianPolicy
{
    public static double? ConfirmedProjectValue(WorkspaceRecord? workspace)
    {
        if (workspace?.CentralMeridian is not { } value || !double.IsFinite(value) ||
            value is < -180 or > 180 || string.IsNullOrWhiteSpace(workspace.CentralMeridianConfirmedAt) ||
            string.IsNullOrWhiteSpace(workspace.CentralMeridianSource) ||
            WorkspaceService.IsLegacyCoordinateDerivedCentralMeridianSource(workspace.CentralMeridianSource))
            return null;
        return value;
    }

    public static double? ReuseForRouteLoad(WorkspaceRecord? workspace,
        string? routeValue, bool restoringCachedProject)
    {
        double? confirmed = ConfirmedProjectValue(workspace);
        if (!confirmed.HasValue) return null;
        if (restoringCachedProject || string.IsNullOrWhiteSpace(routeValue)) return confirmed;
        return double.TryParse(routeValue, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double local) &&
            double.IsFinite(local) && Math.Abs(local - confirmed.Value) <= 1e-9
                ? confirmed : null;
    }
}
