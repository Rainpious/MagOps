namespace MagOps.Core.Services;

public static class StateMachine
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRECHECK"] = ["PRECHECK", "ACTIVE", "PAUSED", "REVIEW_REQUIRED", "BLOCKED"],
        ["ACTIVE"] = ["ACTIVE", "PAUSED", "REVIEW_REQUIRED", "BLOCKED", "RELEASED"],
        ["PAUSED"] = ["PAUSED", "PRECHECK", "ACTIVE", "REVIEW_REQUIRED", "BLOCKED"],
        ["REVIEW_REQUIRED"] = ["REVIEW_REQUIRED", "PRECHECK", "ACTIVE", "BLOCKED", "RELEASED"],
        ["BLOCKED"] = ["BLOCKED", "PRECHECK", "ACTIVE", "REVIEW_REQUIRED"],
        ["RELEASED"] = ["RELEASED", "REVIEW_REQUIRED", "BLOCKED"],
    };

    private static readonly HashSet<string> ReasonRequired =
        new(StringComparer.OrdinalIgnoreCase) { "PAUSED", "REVIEW_REQUIRED", "BLOCKED" };

    public static void Validate(string current, string target, string note = "")
    {
        if (!AppConstants.ProjectStatuses.Contains(current, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"当前状态无效: {current}");
        if (!AppConstants.ProjectStatuses.Contains(target, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"目标状态无效: {target}");
        if (!Allowed[current].Contains(target))
            throw new InvalidOperationException($"不允许状态转换: {current} -> {target}");
        if (ReasonRequired.Contains(target) && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException($"进入 {target} 必须填写原因备注");
        if (new[] { "PAUSED", "REVIEW_REQUIRED", "BLOCKED" }.Contains(current, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException($"解除 {current} 状态必须填写解除依据或验证说明");
    }
}
