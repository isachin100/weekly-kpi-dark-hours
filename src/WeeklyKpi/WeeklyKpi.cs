using System;
using System.Collections.Generic;
using System.Linq;

public record Shift(int Id, string Location, DateTime Start, DateTime End, bool Removed);
public record Coverage(int Id, string Location, DateTime Start, DateTime End);

public static class WeeklyKpi
{
    // Same signature as the legacy version, so Web Forms callers don't change.
    // Every interval is half-open: [Start, End).
    public static (double scheduled, double covered, double dark) Calculate(
        string location, DateTime from, DateTime to,
        IEnumerable<Shift> shifts, IEnumerable<Coverage> coverage)
    {
        var (s, c, d) = CalculateMinutes(location, from, to, shifts, coverage);
        return (s / 60.0, c / 60.0, d / 60.0);
    }

    // Integer minutes are the source of truth. Hours get rounded to 2 dp only when displayed.
    public static (long scheduled, long covered, long dark) CalculateMinutes(
        string location, DateTime from, DateTime to,
        IEnumerable<Shift> shifts, IEnumerable<Coverage> coverage)
    {
        if (to <= from) return (0, 0, 0);
        from = TruncateToMinute(from);
        to = TruncateToMinute(to);

        // Rule 2: removed shifts are left out of the math. The rows stay in the DB for audit.
        var activeShifts = Merge(Normalize(
            shifts.Where(s => !s.Removed && SameLocation(s.Location, location))
                  .Select(s => (s.Start, s.End)), from, to));

        var coverageUnion = Merge(Normalize(
            coverage.Where(c => SameLocation(c.Location, location))
                    .Select(c => (c.Start, c.End)), from, to));

        // Rule 4: coverage counts only where an active shift exists.
        var covered = Intersect(activeShifts, coverageUnion);

        long schedMin = TotalMinutes(activeShifts);
        long covMin = TotalMinutes(covered);
        return (schedMin, covMin, Math.Max(0, schedMin - covMin)); // Rule 5
    }

    public static string FormatHours(long minutes) => (minutes / 60m).ToString("0.00");

    // Clip to [from, to), truncate to the minute, and drop empty or inverted intervals.
    internal static IEnumerable<(DateTime Start, DateTime End)> Normalize(
        IEnumerable<(DateTime Start, DateTime End)> items, DateTime from, DateTime to)
    {
        foreach (var (start, end) in items)
        {
            var s = TruncateToMinute(start < from ? from : start);
            var e = TruncateToMinute(end > to ? to : end);
            if (e > s) yield return (s, e);
        }
    }

    // Sort by start and fold. Overlapping or touching intervals merge into one.
    // Merging [a,b) with [b,c) is safe because they share no minute.
    internal static List<(DateTime Start, DateTime End)> Merge(
        IEnumerable<(DateTime Start, DateTime End)> items)
    {
        var result = new List<(DateTime Start, DateTime End)>();
        foreach (var iv in items.OrderBy(i => i.Start))
        {
            if (result.Count > 0 && iv.Start <= result[^1].End)
            {
                var last = result[^1];
                if (iv.End > last.End) result[^1] = (last.Start, iv.End);
            }
            else
            {
                result.Add(iv);
            }
        }
        return result;
    }

    // Two-pointer intersection of two sorted, disjoint interval lists.
    internal static List<(DateTime Start, DateTime End)> Intersect(
        List<(DateTime Start, DateTime End)> a, List<(DateTime Start, DateTime End)> b)
    {
        var result = new List<(DateTime Start, DateTime End)>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            var s = a[i].Start > b[j].Start ? a[i].Start : b[j].Start;
            var e = a[i].End < b[j].End ? a[i].End : b[j].End;
            if (e > s) result.Add((s, e));
            if (a[i].End < b[j].End) i++; else j++;
        }
        return result;
    }

    private static long TotalMinutes(IEnumerable<(DateTime Start, DateTime End)> items) =>
        items.Sum(i => (long)(i.End - i.Start).TotalMinutes);

    private static DateTime TruncateToMinute(DateTime d) =>
        new DateTime(d.Ticks - d.Ticks % TimeSpan.TicksPerMinute, d.Kind);

    private static bool SameLocation(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

// Original starter code, kept unchanged so tests can show the defects. Do not use in production.
public static class LegacyWeeklyKpi
{
    public static (double scheduled, double covered, double dark) Calculate(
        string location, DateTime from, DateTime to,
        IEnumerable<Shift> shifts, IEnumerable<Coverage> coverage)
    {
        var scheduled = shifts.Where(s => s.Location == location && s.Start >= from && s.End <= to)
            .Sum(s => (s.End - s.Start).TotalHours);
        var covered = coverage.Where(c => c.Location == location && c.Start >= from && c.End <= to)
            .Sum(c => (c.End - c.Start).TotalHours);
        return (scheduled, covered, scheduled - covered);
    }
}
