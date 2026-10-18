# Weekly KPI: Dark/Uncovered Hours Fix

**Repo layout**
- `src/WeeklyKpi/WeeklyKpi.cs`: the corrected C#. The original code is kept as `LegacyWeeklyKpi` only for comparison.
- `tests/WeeklyKpi.Tests/WeeklyKpiTests.cs`: xUnit tests (12, all passing).
- `sql/WeeklyKpi.sql`: a set-based T-SQL version, run on SQL Server LocalDB.
- Run: `dotnet test`, then `sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i sql\WeeklyKpi.sql`

---

## A. Defects and operational impact

| # | Defect (starter code) | Evidence in fixture | Operational impact |
|---|---|---|---|
| 1 | **Removed shifts not excluded** (`Removed` never checked) | Wichita shift 3 (8h) counted | Cancelled shifts show up as scheduled, uncovered hours. Inflated dark-hours alerts and possible over-staffing decisions |
| 2 | **Durations summed, not unioned** | Shifts 1+2 overlap 12:00–16:00 (+4h). Coverage 101+102 overlap 12:00–13:00 (+1h) | Double-counted scheduled and covered hours. The KPI disagrees with the scheduling screen, which shows the timeline |
| 3 | **Containment filter drops boundary-spanning intervals** (`Start>=from && End<=to`) instead of clipping them | Memphis shifts 5 and 6 and coverage 105 and 106 are dropped entirely | Overnight shifts at the week edges disappear. Memphis is under-reported by 8h scheduled |
| 4 | **Coverage not clipped to active scheduled time** | Coverage 107 07:00–08:00 is before shift 7 starts | Coverage outside any shift lowers dark hours, so real gaps get hidden |
| 5 | **Dark can go negative** (`scheduled - covered`) | Follows from 2 and 4 | Negative KPIs, and totals look wrong when rolled up |
| 6 | `double` hour sums and no minute normalization | — | Floating-point drift. Rule 7 asks for minute precision |
| minor | No guard for `End <= Start`. Location compare is case- and whitespace-sensitive | — | Bad rows silently skew results |

The legacy code gives Wichita **32 / 14 / 18** and Memphis **4 / 2 / 2**. Both are wrong, and the test `Legacy_ProducesWrongNumbers` pins this behaviour down.

## B. The fix and how interval merging works

The full code is in `src/WeeklyKpi/WeeklyKpi.cs`. The public `Calculate` signature is unchanged, so it's a drop-in for Web Forms callers. `CalculateMinutes` returns integer minutes.

The pipeline runs once per location:
1. **Filter**: active shifts only (`!Removed`). Location match is trimmed and case-insensitive.
2. **Normalize**: clip each interval to `[from,to)`, truncate to the minute, and drop empty or inverted intervals.
3. **Merge (union)**: sort by `Start`, then walk the list. If the next interval starts at or before the current one's `End`, extend `End` to the larger of the two. Otherwise close the current interval and start a new one. Because intervals are half-open, `[08,12)` and `[12,16)` share no minute, so merging them doesn't double-count. The result is a sorted list of disjoint intervals, so summing it gives the true union.
4. **Intersect**: a two-pointer sweep over merged shifts and merged coverage. Each step emits `[max(starts), min(ends))` when it's non-empty, then advances the list whose interval ends first. This clips coverage to scheduled time, and duplicate coverage was already collapsed in step 3.
5. **Totals** in whole minutes: `dark = max(0, scheduled − covered)`. Hours are formatted to 2 dp only for display.

The T-SQL version does the same thing with set operations: a `CASE` clip, gaps-and-islands merging (a running `MAX(EndAt)` over the preceding rows flags where a new island starts), an overlap join for the intersection, and `ROLLUP` for the grand total.

## C. Expected results (window [2026-10-12, 2026-10-19))

| Location | Scheduled | Covered | Dark |
|---|---:|---:|---:|
| Memphis | 12.00 | 6.00 | 6.00 |
| Wichita | 20.00 | 13.00 | 7.00 |
| **Grand total** | **32.00** | **19.00** | **13.00** |

How the numbers break down:
- **Wichita**: scheduled = 10-12 08–20 (union of 1 and 2, 12h) + 10-14 22:00→10-15 06:00 (8h) = 20h. Shift 3 is removed. Covered = 08–15 (101∪102, 7h) + 18–20 (2h) + 23–03 (4h) = 13h.
- **Memphis**: scheduled = shift 5 clipped to 00–04 (4h) + shift 6 clipped to 20–24 (4h) + shift 7 (4h) = 12h. Covered = 105 clipped to 00–02 (2h) + 106 clipped to 21–24 (3h) + 107 ∩ shift 7 = 08–09 (1h) = 6h.

These were computed by hand first, then confirmed by the C# tests and, separately, by the T-SQL script on LocalDB. All three agree.

## D. Regression tests (`tests/WeeklyKpi.Tests/WeeklyKpiTests.cs`)

The three required tests:
1. `OverlappingShifts_AreUnioned_NotSummed`: 08–16 and 12–20 give 12h scheduled, not 16h.
2. `RemovedShift_ContributesNothing`: a removed shift contributes 0h, and coverage over it isn't counted. The input row is kept, so it stays auditable.
3. `DuplicateCoverage_IsNotDoubleCounted`: an exact duplicate plus a nested coverage row still count as 4h.

Additional tests: boundary clipping, coverage outside a shift (107), adjacent half-open intervals, minute precision (20 min → "0.33"), bad rows and non-negative dark, the full fixture per location plus the grand total, and the documented legacy behaviour. Result: **Passed 12/12** (`dotnet test`, net8.0).

## E. Deployment, rollback and payroll risk (≤100 words)

Ship the new DLL behind a config flag. Run the old and new calculations side by side on the last 4 weekly reports and reconcile each difference against the scheduling screen before switching over. There's no schema change. Rollback means flipping the flag or redeploying the previous DLL. Payroll risk: if payroll or billing consumes these hours, the corrected (lower) scheduled and dark totals change downstream amounts. Get Payroll/Finance sign-off, annotate rather than silently restate closed periods, and notify KPI consumers. DST: production should store UTC or `datetimeoffset`, clip in UTC, and convert for display, so 23h and 25h days are correct.

## F. AI workflow

**Tool used:** Claude Code CLI (model: Claude Opus 5.5), on Windows, in plan mode first and then implementation mode. I gave it the scenario, reviewed its plan, and approved it. It then scaffolded the projects, wrote the code, tests and SQL, and ran `dotnet test` and `sqlcmd` locally.

**Prompt 1 (planning):**
> "I need to submit a screening test, Please review the mentioned sceniro and come up with a plan how to do it, what exactly client want." *(followed by the full scenario text)*

Response excerpt (from the plan it produced):
> "Removed flag never filtered — Wichita shift 3 (8h) counted … Containment filter (Start>=from && End<=to) **drops** boundary-spanning intervals instead of **clipping** … Buggy output vs. correct: Wichita 32/14/18 (should be 20/13/7). Memphis 4/2/2 (should be 12/6/6)."

**Prompt 2 (implementation):** approving the plan ("User has approved your plan. You can now start coding").

Response excerpt (actual tool output):
> "Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12"
> SQL: "Memphis 12.00 6.00 6.00 / Wichita 20.00 13.00 7.00 / GRAND TOTAL 32.00 19.00 13.00"

**Accepted and verified:** the AI's design: merge into a disjoint union, then intersect coverage with the scheduled union using two pointers, with integer minutes as the unit. I verified it three separate ways: (1) by working out the fixture timelines by hand, (2) with the xUnit fixture tests, and (3) with a separately written set-based T-SQL script on LocalDB. All three give 32/19/13.

**Challenged and corrected:**
- *The fixture didn't run as given.* The plan assumed the brief's SQL fixture would run unchanged. It actually failed with `Msg 241 … Conversion failed when converting date and/or time` on SQL Server, because ISO-8601 literals with `T` need seconds (`hh:mm:ss`). We added `:00` and noted it in the script. The values are unchanged.
- *A likely failure mode, checked on purpose:* a typical "quick fix" (including a first-pass AI fix) clips coverage only to the **reporting window**. That gives Memphis covered = 7h, because coverage 107's 07:00–08:00 slice falls outside every shift. Rule 4 says coverage must be clipped to **active scheduled intervals**, so the right answer is 6h. The test `CoverageOutsideScheduledShift_IsIgnored` locks this in. I also challenged whether adjacent intervals `[a,b)+[b,c)` should merge. They should: it's safe because half-open intervals share no minute, and the test `AdjacentIntervals_DoNotOverlap` covers it.

**Validation method:** hand-computed timelines → unit tests → a separate SQL implementation, cross-checked for exact agreement. A legacy test confirms the original defects reproduce.

## G. Time spent (approximate)

| Phase | Minutes |
|---|---|
| Discovery (reading the rules, hand-computing the fixture, listing defects) | ~8 |
| Implementation (C# fix, tests, T-SQL) | ~12 |
| Verification (dotnet test, LocalDB run, fixing the fixture literals, write-up) | ~8 |
| **Total** | **~28** |

**What AI sped up:** listing the defects, scaffolding the projects and tests, the T-SQL gaps-and-islands boilerplate, and drafting this write-up. **What stayed human-checked:** the expected numbers, the coverage-clipping rule, and the payroll and release judgement.

**Not done in the timebox:** wiring this into the actual ASP.NET Web Forms page and data-access layer (not provided), DST/UTC conversion code (described above only), and moving the SQL into a stored procedure with parameters.
