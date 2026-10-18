# Weekly KPI: Dark/Uncovered Hours Fix

Fixes the weekly scheduling KPI report, which disagreed with the scheduling screen. The corrected calculation is in C#, and there's a matching T-SQL version, both covered by regression tests.

**Repo layout**
- `src/WeeklyKpi/WeeklyKpi.cs`: the corrected C#. The original code is kept as `LegacyWeeklyKpi` only for comparison.
- `tests/WeeklyKpi.Tests/WeeklyKpiTests.cs`: xUnit tests (12, all passing).
- `sql/WeeklyKpi.sql`: a set-based T-SQL version, run on SQL Server LocalDB.

**To check it yourself**
```powershell
dotnet test                                                        # expect: Passed 12 of 12
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i sql\WeeklyKpi.sql -W      # expect: the table in section C
```

---

## A. Defects and operational impact

| # | Defect | Example in the test data | Operational impact |
|---|---|---|---|
| 1 | **Removed shifts are still counted.** The `Removed` flag is never checked. | Wichita shift 3 (8h, removed) is included. | Cancelled shifts show up as unstaffed "dark" hours, which triggers false alerts and can lead to unnecessary staffing. |
| 2 | **Overlapping hours are added twice.** Durations are summed instead of merged. | Shifts 1 and 2 overlap 12:00–16:00 (+4h). Coverage 101 and 102 overlap 12:00–13:00 (+1h). | Scheduled and covered hours are inflated, so the report disagrees with the scheduling screen. |
| 3 | **Shifts crossing the week boundary are dropped** instead of trimmed to the window. | Memphis shifts 5 and 6 and coverage 105 and 106 are ignored completely. | Overnight shifts at the start and end of the week disappear, so Memphis is under-reported by 8 scheduled hours. |
| 4 | **Coverage is not limited to scheduled time.** | Coverage 107 runs 07:00–08:00, before shift 7 starts at 08:00. | Coverage when nobody is scheduled reduces dark hours, which hides real gaps. |
| 5 | **Dark hours can go negative**, and hours are calculated as floating-point numbers. | — | Negative or slightly off KPIs that don't add up when rolled up. |

The old code reports Wichita 32 / 14 / 18 and Memphis 4 / 2 / 2 (scheduled / covered / dark). Both rows are wrong, and the test `Legacy_ProducesWrongNumbers` reproduces them.

## B. The fix and how interval merging works

The corrected code is in `src/WeeklyKpi/WeeklyKpi.cs`. The method signature is unchanged, so existing Web Forms pages can call it without changes. A matching T-SQL version is in `sql/WeeklyKpi.sql`.

The calculation for each location:
1. **Filter:** keep only active shifts (`Removed = false`) for the location.
2. **Trim:** cut each shift and coverage block to the reporting window. Partly-inside shifts are trimmed, not dropped.
3. **Merge:** sort the intervals by start time and walk through them. If the next interval starts before the current one ends, extend the current one; otherwise start a new one. The result is a list of non-overlapping blocks, so adding them up counts every minute once. Duplicate coverage collapses automatically. Because intervals are `[Start, End)`, back-to-back blocks such as 08–12 and 12–16 share no minute and are never double-counted.
4. **Intersect:** walk through the merged shifts and merged coverage together and keep only the time where both exist. This is "covered" time, and it excludes coverage outside scheduled shifts.
5. **Total:** work in whole minutes for exact precision. Dark = max(0, scheduled − covered). Hours are rounded to 2 decimals only for display.

Core of the merge step:
```csharp
foreach (var iv in items.OrderBy(i => i.Start))
{
    if (result.Count > 0 && iv.Start <= result[^1].End)
        result[^1] = (result[^1].Start, Max(result[^1].End, iv.End));  // overlap → extend
    else
        result.Add(iv);                                                  // gap → new block
}
```

The T-SQL version does the same with set operations: `CASE` to trim, "gaps-and-islands" to merge (a running `MAX(EndAt)` marks where each new block starts), an overlap join to intersect, and `ROLLUP` for the grand total.

## C. Expected results (week of 2026-10-12 to 2026-10-19)

| Location | Scheduled | Covered | Dark |
|---|---:|---:|---:|
| Memphis | 12.00 | 6.00 | 6.00 |
| Wichita | 20.00 | 13.00 | 7.00 |
| **Grand total** | **32.00** | **19.00** | **13.00** |

How the numbers come about:
- **Wichita scheduled = 20h:** Oct 12 08:00–20:00 (shifts 1 and 2 merged) is 12h, plus Oct 14 22:00 to Oct 15 06:00 is 8h. Shift 3 is removed.
- **Wichita covered = 13h:** 08:00–15:00 (101 and 102 merged) is 7h, plus 18:00–20:00 is 2h, plus 23:00–03:00 is 4h.
- **Memphis scheduled = 12h:** shift 5 trimmed to 00:00–04:00 is 4h, shift 6 trimmed to 20:00–24:00 is 4h, and shift 7 is 4h.
- **Memphis covered = 6h:** 105 trimmed to 00:00–02:00 is 2h, 106 trimmed to 21:00–24:00 is 3h, and 107 limited to shift 7 (08:00–09:00) is 1h.

These were checked three separate ways: by hand, with the C# tests, and with the T-SQL script on SQL Server LocalDB. All three agree.

## D. Regression tests (`tests/WeeklyKpi.Tests/WeeklyKpiTests.cs`)

1. **Overlap:** `OverlappingShifts_AreUnioned_NotSummed`. Shifts 08–16 and 12–20 give 12h scheduled, not 16h.
2. **Removed shifts:** `RemovedShift_ContributesNothing`. A removed shift adds 0h, coverage during it is not counted, and the original record is left untouched for audit.
3. **Duplicate coverage:** `DuplicateCoverage_IsNotDoubleCounted`. Two identical coverage rows plus one nested inside them count as 4h total.

Additional tests cover window-boundary trimming, coverage outside a shift, back-to-back intervals, minute precision, invalid rows, the full test data per location and grand total, and the old code's wrong numbers. **Result: 12 of 12 pass.**

## E. Deployment, rollback and payroll risk (≤100 words)

Deploy the new calculation behind a config flag. Before switching, run old and new side by side on the last four weekly reports and reconcile every difference against the scheduling screen. There are no database schema changes, so rollback is turning the flag off or redeploying the previous DLL. Payroll risk: if payroll or billing use these hours, the corrected numbers will change downstream totals. Get Payroll/Finance sign-off first, and don't silently restate closed pay periods; annotate them instead. For DST in production, store times in UTC (`datetimeoffset`) and convert only for display, so 23- and 25-hour days are counted correctly.

## F. AI workflow

**Tool used:** Claude Code (CLI) with the Claude Opus 5.5 model, on Windows. It planned first (plan mode), and I approved the plan before it wrote any code. It then built the projects, code, tests and SQL, and ran them locally.

*Prompts are quoted verbatim from the session, unedited.*

**Prompt 1 (planning, before any code was written):**
> "I need to submit a screening test, Please review the mentioned sceniro and come up with a plan how to do it, what exactly client want." *(followed by the full scenario)*

Response excerpt:
> "Removed flag never filtered — Wichita shift 3 (8h) counted … Containment filter drops boundary-spanning intervals instead of clipping … Buggy output vs. correct: Wichita 32/14/18 (should be 20/13/7). Memphis 4/2/2 (should be 12/6/6)."

**How I used it:** I didn't take the defect list on trust. I worked out the correct numbers by hand first, then approved the plan. Later the `Legacy_ProducesWrongNumbers` test confirmed the AI's buggy-output figures exactly.

**Prompt 2 (verification):**
> "how to test the implementation locally?"

Response excerpt:
> "`dotnet test` … To show the tests aren't passing by accident, break the code on purpose … delete `!s.Removed && ` … `RemovedShift_ContributesNothing` and the Wichita fixture test should fail."

**How I used it:** I ran the check. With the filter removed, 3 of 12 tests failed (the two the AI predicted, plus `Fixture_GrandTotal`). With it restored, all 12 passed again. So the tests catch real regressions and aren't passing by accident.

**AI suggestion accepted and verified:** merge the intervals into non-overlapping blocks, then intersect coverage with scheduled time, counting in whole minutes. I verified it by working out the expected numbers by hand first, then confirming them with the unit tests and with a separately written SQL query. All three give 32 / 19 / 13.

**AI assumption that was wrong and corrected:**
- The AI's plan assumed the SQL test data from the brief would run as provided. It didn't: SQL Server rejected it with `Msg 241 – Conversion failed when converting date and/or time`, because times written like `2026-10-12T08:00` need seconds (`T08:00:00`). We added `:00` to each time, left the values unchanged, and documented this in the script.
- The AI also first packaged the ZIP with a command that flattened the folder structure. I caught this when checking the ZIP contents, and it was rebuilt with `git archive`.

**Possible failure I checked on purpose:** a quick fix (often the first one an AI suggests) trims coverage only to the *reporting week*. That gives Memphis 7h covered instead of 6h, because coverage 107 starts an hour before shift 7. Rule 4 says coverage only counts during *scheduled* time, so a dedicated test (`CoverageOutsideScheduledShift_IsIgnored`) guards against it.

**Validation method:** hand calculation, then 12 unit tests, then a separate SQL implementation, cross-checked for an exact match. I also confirmed the old code still produces the wrong numbers, and that removing a fix makes the tests fail.

## G. Time spent (approximate)

| Phase | Minutes |
|---|---:|
| Discovery: reading the rules, hand-calculating results, listing defects | ~8 |
| Implementation: C# fix, tests, T-SQL | ~12 |
| Verification: running tests and SQL, fixing the test data, write-up | ~8 |
| **Total** | **~28** |

**What AI sped up:** spotting the defects, setting up the projects and tests, writing the SQL boilerplate, and drafting this write-up.
**What I checked myself:** the expected numbers, the coverage rule, and the release and payroll judgement.
**Not finished in the 30-minute timebox:** connecting the fix to the real Web Forms page (that code wasn't provided), writing the DST/UTC conversion code (only described above), and turning the SQL into a parameterized stored procedure.
