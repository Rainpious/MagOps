using System.Text.RegularExpressions;

namespace MagOps.Desktop;

internal sealed record UiChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

internal static class UiText
{
    private static readonly Dictionary<string, string> Names =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Project / task / gate / QC
            ["PRECHECK"] = "前期检查",
            ["ACTIVE"] = "进行中",
            ["PAUSED"] = "已暂停",
            ["REVIEW_REQUIRED"] = "需复核",
            ["BLOCKED"] = "已阻断",
            ["RELEASED"] = "已放行",
            ["TODO"] = "待办",
            ["IN_PROGRESS"] = "进行中",
            ["DONE"] = "已完成",
            ["PENDING"] = "待确认",
            ["PASSED"] = "通过",
            ["FAILED"] = "失败",
            ["DEFERRED"] = "暂缓",
            ["PASS"] = "通过",
            ["ERROR"] = "错误",

            // Flight / issues / handoff
            ["PLANNED"] = "计划中",
            ["COMPLETED"] = "已完成",
            ["OPEN"] = "未解决",
            ["RESOLVED"] = "已解决",
            ["LOW"] = "低",
            ["MEDIUM"] = "中",
            ["HIGH"] = "高",
            ["CRITICAL"] = "严重",
            ["DRAFT"] = "草稿",
            ["READY"] = "可交接",
            ["HANDED_OFF"] = "已交接",

            // Priority
            ["P0"] = "P0（最高）",
            ["P1"] = "P1（高）",
            ["P2"] = "P2（普通）",
            ["P3"] = "P3（低）",

            // Gate names
            ["source"] = "源资料",
            ["coordinate"] = "坐标",
            ["equipment"] = "设备",
            ["route"] = "航线",
            ["field"] = "外业",
            ["processing"] = "数据处理",
            ["report"] = "报告",

            // Artifact roles / stages
            ["raw"] = "原始资料",
            ["registry"] = "资料登记",
            ["route_result"] = "航线结果",
            ["derived"] = "派生成果",
            ["figure"] = "图件",
            ["external_software"] = "外部软件/GIS",
            ["project_management"] = "项目管理",
            ["other"] = "其他",
            ["unclassified"] = "未分类",
            ["workarea"] = "工区资料",
            ["reporting"] = "报告编制",
            ["gis"] = "GIS/软件",
            ["project"] = "项目",
            ["management"] = "管理数据",

            // Artifact QA / tracking
            ["UNREVIEWED"] = "未复核",
            ["STALE"] = "已过期，需重算",
            ["NEW"] = "新增",
            ["UNCHANGED"] = "未变化",
            ["CHANGED_SINCE_LAST_SCAN"] = "自上次扫描后变化",
            ["MOVED"] = "已移动",
            ["MISSING"] = "缺失",
            ["ASSIGNED"] = "已归属工区",
            ["COMPUTED"] = "已计算",
            ["SKIPPED"] = "已跳过",
            ["RECORDED"] = "已记录",
            ["REGISTERED"] = "已登记",
            ["NOT_SCANNED"] = "未扫描",
            ["NOT_COMPUTED"] = "未计算",
            ["NOT_REQUIRED"] = "无需执行",
            ["SCANNED"] = "已扫描",
            ["MIGRATED"] = "已迁移",
            ["REQUIRED"] = "必备",
            ["OPTIONAL"] = "可选",

            // Route view
            ["OK"] = "正常",
            ["OVER_LIMIT"] = "超限",
            ["projected"] = "平面投影坐标",
            ["geographic"] = "经纬度",
            ["terrain_elevation_m"] = "地面高程",
            ["planned_agl_m"] = "计划离地高度",
            ["planned_altitude_m"] = "计划绝对高度",

            // Booleans shown in choices
            ["True"] = "是",
            ["False"] = "否",

        };

    private static readonly HashSet<string> GridProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Status", "Role", "ProcessingStage", "QualityStatus", "ChangeStatus",
            "HashStatus", "Selected", "Priority", "Severity", "Requirement",
            "ReleaseStatus", "ChecklistStatus", "TestStatus", "HandoffStatus",
            "Gate", "ResultStatus", "Manual", "CoordMode", "ScanStatus"
        };

    public static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? "";
        return Names.TryGetValue(value.Trim(), out string? display)
            ? display
            : value;
    }

    public static bool TryDisplay(string value, out string display)
    {
        if (Names.TryGetValue(value.Trim(), out string? mapped))
        {
            display = mapped;
            return true;
        }
        display = value;
        return false;
    }

    public static UiChoice Choice(string value) => new(value, string.IsNullOrWhiteSpace(value) ? "（未设置）" : Display(value));

    public static string SelectedValue(ComboBox combo)
    {
        return combo.SelectedItem switch
        {
            UiChoice choice => choice.Value,
            string text => text,
            _ => combo.Text.Trim()
        };
    }

    public static bool ShouldTranslateGridProperty(string propertyName) =>
        GridProperties.Contains(propertyName);

    public static string HeightType(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return "未填写";

        return text.ToLowerInvariant() switch
        {
            "terrain_elevation_m" => "地面高程",
            "planned_agl_m" => "计划离地高度",
            "planned_altitude_m" => "计划绝对高度",
            _ => text
        };
    }

    public static string LocalizeStatusSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Sentence-level replacement is intentionally restricted to machine-like
        // uppercase tokens, so ordinary file paths and English filenames are untouched.
        string result = text;
        foreach (var pair in Names
                     .Where(x => x.Key.Any(char.IsLetter) &&
                                 x.Key.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                     .OrderByDescending(x => x.Key.Length))
        {
            result = Regex.Replace(
                result,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(pair.Key)}(?![A-Za-z0-9_])",
                pair.Value);
        }
        return result;
    }
}
