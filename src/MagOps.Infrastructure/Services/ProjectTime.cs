namespace MagOps.Infrastructure.Services;

public static class ProjectTime
{
    public static TimeZoneInfo FromBasis(string? basis) => string.IsNullOrWhiteSpace(basis) ||
        basis == FieldMagDataService.BeijingProgressTimeZone ? Beijing : TimeZoneInfo.FindSystemTimeZoneById(basis);
    public static string ShortLabel(string? basis) => FromBasis(basis).Id is "Asia/Shanghai" or "China Standard Time" ? "北京时间" : Label(FromBasis(basis));
    public static TimeZoneInfo Beijing => TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    public static DateTimeOffset Display(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone);
    public static DateTime Date(DateTimeOffset utc, TimeZoneInfo zone) => Display(utc, zone).Date;
    public static string Label(TimeZoneInfo zone) => zone.Id is "Asia/Shanghai" or "China Standard Time" ? "北京时间（UTC+08:00）" : zone.DisplayName;
    public static string Basis(TimeZoneInfo zone) => zone.Id is "Asia/Shanghai" or "China Standard Time"
        ? FieldMagDataService.BeijingProgressTimeZone : zone.Id;
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new FormatException("该本地时间因夏令时跳转不存在，请选择有效时间。");
        if (zone.IsAmbiguousTime(local)) throw new FormatException("该本地时间因夏令时出现两次，请使用带明确 UTC 偏移的时间。");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
    public static DateTimeOffset DayStartUtc(DateTime date, TimeZoneInfo zone) => ToUtc(date.Date, zone);
}
