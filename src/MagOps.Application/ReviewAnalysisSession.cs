using System.Text.Json;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public enum ReviewRecalculation { Recognition, SelectionOverlay, ReuseCurrent }

/// <summary>Opaque captured work. Requests are copied before leaving the UI thread.</summary>
public sealed class ReviewCalculationTicket
{
    internal ReviewCalculationTicket(long revision, long decisionRevision, FieldMagFolderAnalysis analysis,
        PlannedSurveyLineTask[] tasks, AuditProductionRequest request, string route, string recognitionKey,
        string requestKey, ReviewRecalculation plan)
    {
        Revision = revision; DecisionRevision = decisionRevision; Analysis = analysis; Tasks = tasks;
        Request = request; Route = route; RecognitionKey = recognitionKey; RequestKey = requestKey; Plan = plan;
    }
    public long Revision { get; }
    public ReviewRecalculation Plan { get; }
    internal long DecisionRevision { get; }
    internal FieldMagFolderAnalysis Analysis { get; }
    internal PlannedSurveyLineTask[] Tasks { get; }
    internal AuditProductionRequest Request { get; }
    internal string Route { get; }
    internal string RecognitionKey { get; }
    internal string RequestKey { get; }
}

public sealed record ReviewCalculationCompletion(AuditProductionResult Result, bool IsCurrent);

/// <summary>
/// Single session owner of recognition caches and computation revisions. View
/// changes never enter this class. An invalidated result is discarded, not
/// described as a cancelled algorithm. No global or on-disk cache is added.
/// </summary>
public sealed class ReviewAnalysisSession
{
    private readonly object _stateGate = new(), _calculationGate = new();
    private readonly AuditProductionService _service;
    private readonly ReviewDecisionState _decisions;
    private long _revision;
    private AuditProductionResult? _current;
    private FieldMagFolderAnalysis? _analysis;
    private string? _recognitionKey, _requestKey;

    public ReviewAnalysisSession(ReviewDecisionState decisions, AuditProductionService? service = null)
    { _decisions = decisions; _service = service ?? new(); }

    // Legacy callers may inspect counters; production should use AnalyzeForProduction
    // so all access to the mutable calculation cache shares the same lock.
    public AuditProductionService Service => _service;

    public ReviewCalculationTicket Prepare(FieldMagFolderAnalysis analysis, IReadOnlyList<PlannedSurveyLineTask> tasks,
        AuditProductionRequest request, string route, bool forceRecognition)
    {
        var captured = JsonSerializer.Deserialize<AuditProductionRequest>(JsonSerializer.Serialize(request))!;
        var capturedTasks = tasks.ToArray();
        string recognitionKey = AuditProductionService.RecognitionInputFingerprint(analysis, capturedTasks, captured, route);
        string requestKey = ProductionSnapshotService.RequestFingerprint(captured);
        lock (_stateGate)
        {
            bool reusable = !forceRecognition && ReferenceEquals(analysis, _analysis) && _recognitionKey == recognitionKey;
            var plan = reusable && _current is not null && _requestKey == requestKey ? ReviewRecalculation.ReuseCurrent :
                reusable ? ReviewRecalculation.SelectionOverlay : ReviewRecalculation.Recognition;
            return new(++_revision, _decisions.Revision, analysis, capturedTasks, captured, route, recognitionKey, requestKey, plan);
        }
    }

    public bool IsCurrent(ReviewCalculationTicket ticket)
    {
        lock (_stateGate) return ticket.Revision == _revision && ticket.DecisionRevision == _decisions.Revision;
    }

    public void Invalidate()
    {
        lock (_stateGate) { _revision++; _current = null; _analysis = null; _recognitionKey = null; _requestKey = null; }
    }

    public ReviewCalculationCompletion Calculate(ReviewCalculationTicket ticket, Action<string>? progress = null)
    {
        lock (_calculationGate)
        {
            AuditProductionResult? result = null;
            lock (_stateGate)
                if (IsCurrent(ticket) && ticket.Plan == ReviewRecalculation.ReuseCurrent) result = _current;
            result ??= _service.Analyze(ticket.Analysis, ticket.Tasks, ticket.Request, ticket.Route,
                reuseRecognition: ticket.Plan != ReviewRecalculation.Recognition, progress: progress);
            lock (_stateGate)
            {
                bool current = IsCurrent(ticket);
                if (current)
                {
                    _current = result; _analysis = ticket.Analysis;
                    _recognitionKey = ticket.RecognitionKey; _requestKey = ticket.RequestKey;
                }
                return new(result, current);
            }
        }
    }

    public AuditProductionResult AnalyzeForProduction(FieldMagFolderAnalysis analysis, IReadOnlyList<PlannedSurveyLineTask> tasks,
        AuditProductionRequest request, string route)
    {
        // Formal production rechecks selection and anomalies even for an identical
        // review request; it may reuse only the service's validated recognition.
        var captured = JsonSerializer.Deserialize<AuditProductionRequest>(JsonSerializer.Serialize(request))!;
        var capturedTasks = tasks.ToArray();
        lock (_calculationGate) return _service.Analyze(analysis, capturedTasks, captured, route, reuseRecognition: true);
    }
}
