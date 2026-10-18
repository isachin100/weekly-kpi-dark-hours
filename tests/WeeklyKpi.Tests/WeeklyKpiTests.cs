using System;
using System.Collections.Generic;
using Xunit;

public class WeeklyKpiTests
{
    static readonly DateTime From = new(2026, 10, 12, 0, 0, 0);
    static readonly DateTime To = new(2026, 10, 19, 0, 0, 0);
    static DateTime T(string s) => DateTime.Parse(s);

    // Same rows as the SQL fixture in the brief.
    static readonly List<Shift> Shifts = new()
    {
        new(1, "Wichita", T("2026-10-12T08:00"), T("2026-10-12T16:00"), false),
        new(2, "Wichita", T("2026-10-12T12:00"), T("2026-10-12T20:00"), false),
        new(3, "Wichita", T("2026-10-13T09:00"), T("2026-10-13T17:00"), true),
        new(4, "Wichita", T("2026-10-14T22:00"), T("2026-10-15T06:00"), false),
        new(5, "Memphis", T("2026-10-11T22:00"), T("2026-10-12T04:00"), false),
        new(6, "Memphis", T("2026-10-18T20:00"), T("2026-10-19T04:00"), false),
        new(7, "Memphis", T("2026-10-16T08:00"), T("2026-10-16T12:00"), false),
    };
    static readonly List<Coverage> Cov = new()
    {
        new(101, "Wichita", T("2026-10-12T08:00"), T("2026-10-12T13:00")),
        new(102, "Wichita", T("2026-10-12T12:00"), T("2026-10-12T15:00")),
        new(103, "Wichita", T("2026-10-12T18:00"), T("2026-10-12T20:00")),
        new(104, "Wichita", T("2026-10-14T23:00"), T("2026-10-15T03:00")),
        new(105, "Memphis", T("2026-10-11T23:00"), T("2026-10-12T02:00")),
        new(106, "Memphis", T("2026-10-18T21:00"), T("2026-10-19T02:00")),
        new(107, "Memphis", T("2026-10-16T07:00"), T("2026-10-16T09:00")),
    };

    // ---- Required regression tests ----

    [Fact] // Overlap: two shifts overlapping 12:00-16:00 count as a union (12h, not 16h).
    public void OverlappingShifts_AreUnioned_NotSummed()
    {
        var shifts = new[]
        {
            new Shift(1, "X", T("2026-10-12T08:00"), T("2026-10-12T16:00"), false),
            new Shift(2, "X", T("2026-10-12T12:00"), T("2026-10-12T20:00"), false),
        };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, Array.Empty<Coverage>());
        Assert.Equal(12 * 60, r.scheduled);
        Assert.Equal(12 * 60, r.dark);
    }

    [Fact] // Removed shift: no hours, and its coverage doesn't count either. The input row stays (auditable).
    public void RemovedShift_ContributesNothing()
    {
        var shifts = new[] { new Shift(3, "X", T("2026-10-13T09:00"), T("2026-10-13T17:00"), true) };
        var cov = new[] { new Coverage(1, "X", T("2026-10-13T09:00"), T("2026-10-13T17:00")) };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, cov);
        Assert.Equal((0L, 0L, 0L), r);
        Assert.Single(shifts); // the caller's audit data is not mutated
    }

    [Fact] // Duplicate coverage: identical and overlapping rows count once.
    public void DuplicateCoverage_IsNotDoubleCounted()
    {
        var shifts = new[] { new Shift(1, "X", T("2026-10-12T08:00"), T("2026-10-12T16:00"), false) };
        var cov = new[]
        {
            new Coverage(1, "X", T("2026-10-12T08:00"), T("2026-10-12T12:00")),
            new Coverage(2, "X", T("2026-10-12T08:00"), T("2026-10-12T12:00")), // exact duplicate
            new Coverage(3, "X", T("2026-10-12T10:00"), T("2026-10-12T11:00")), // nested inside 1
        };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, cov);
        Assert.Equal(4 * 60, r.covered);
        Assert.Equal(4 * 60, r.dark);
    }

    // ---- Additional edge cases ----

    [Fact] // Shifts crossing the window boundary get clipped, not dropped.
    public void BoundarySpanningIntervals_AreClipped()
    {
        var r = WeeklyKpi.CalculateMinutes("Memphis", From, To, Shifts, Cov);
        Assert.Equal(12 * 60, r.scheduled); // shifts 5 and 6 contribute 4h each
    }

    [Fact] // Coverage outside any active shift doesn't count (coverage 107 from 07:00 to 08:00).
    public void CoverageOutsideScheduledShift_IsIgnored()
    {
        var shifts = new[] { new Shift(7, "X", T("2026-10-16T08:00"), T("2026-10-16T12:00"), false) };
        var cov = new[] { new Coverage(107, "X", T("2026-10-16T07:00"), T("2026-10-16T09:00")) };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, cov);
        Assert.Equal((240L, 60L, 180L), r);
    }

    [Fact] // Half-open intervals: [08,12) and [12,16) are adjacent. 8h total, nothing double-counted.
    public void AdjacentIntervals_DoNotOverlap()
    {
        var shifts = new[]
        {
            new Shift(1, "X", T("2026-10-12T08:00"), T("2026-10-12T12:00"), false),
            new Shift(2, "X", T("2026-10-12T12:00"), T("2026-10-12T16:00"), false),
        };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, Array.Empty<Coverage>());
        Assert.Equal(8 * 60, r.scheduled);
    }

    [Fact] // Minute precision survives (no float drift), and display shows 2 dp.
    public void MinutePrecision_IsPreserved()
    {
        var shifts = new[] { new Shift(1, "X", T("2026-10-12T08:00"), T("2026-10-12T08:20"), false) };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, Array.Empty<Coverage>());
        Assert.Equal(20, r.scheduled);
        Assert.Equal("0.33", WeeklyKpi.FormatHours(r.scheduled));
    }

    [Fact] // Inverted or empty intervals are ignored, so dark is never negative.
    public void BadRows_AreIgnored_DarkNeverNegative()
    {
        var shifts = new[] { new Shift(1, "X", T("2026-10-12T10:00"), T("2026-10-12T08:00"), false) };
        var cov = new[] { new Coverage(1, "X", T("2026-10-12T08:00"), T("2026-10-12T12:00")) };
        var r = WeeklyKpi.CalculateMinutes("X", From, To, shifts, cov);
        Assert.Equal((0L, 0L, 0L), r);
    }

    // ---- Full fixture (Section C) ----

    [Theory]
    [InlineData("Wichita", "20.00", "13.00", "7.00")]
    [InlineData("Memphis", "12.00", "6.00", "6.00")]
    public void Fixture_PerLocation(string loc, string sched, string cov, string dark)
    {
        var r = WeeklyKpi.CalculateMinutes(loc, From, To, Shifts, Cov);
        Assert.Equal(sched, WeeklyKpi.FormatHours(r.scheduled));
        Assert.Equal(cov, WeeklyKpi.FormatHours(r.covered));
        Assert.Equal(dark, WeeklyKpi.FormatHours(r.dark));
    }

    [Fact]
    public void Fixture_GrandTotal()
    {
        long s = 0, c = 0, d = 0;
        foreach (var loc in new[] { "Wichita", "Memphis" })
        {
            var r = WeeklyKpi.CalculateMinutes(loc, From, To, Shifts, Cov);
            s += r.scheduled; c += r.covered; d += r.dark;
        }
        Assert.Equal(("32.00", "19.00", "13.00"),
            (WeeklyKpi.FormatHours(s), WeeklyKpi.FormatHours(c), WeeklyKpi.FormatHours(d)));
    }

    [Fact] // Pins down the legacy behaviour so the defect is documented by a test.
    public void Legacy_ProducesWrongNumbers()
    {
        Assert.Equal((32.0, 14.0, 18.0), LegacyWeeklyKpi.Calculate("Wichita", From, To, Shifts, Cov));
        Assert.Equal((4.0, 2.0, 2.0), LegacyWeeklyKpi.Calculate("Memphis", From, To, Shifts, Cov));
    }
}
