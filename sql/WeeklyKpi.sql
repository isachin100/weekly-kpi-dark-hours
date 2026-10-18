-- Weekly KPI (scheduled / covered / dark hours), set-based T-SQL.
-- Method: clip to window -> merge overlaps (gaps-and-islands) -> intersect coverage with schedule.
-- All intervals are half-open [StartAt, EndAt).
SET NOCOUNT ON;

-- ===== Synthetic fixture (from the brief) =====
-- NOTE: the brief's literals ('2026-10-12T08:00') fail with Msg 241 on SQL Server, because the ISO-8601 'T' form
-- needs seconds. ':00' has been added. The values are otherwise unchanged.
DROP TABLE IF EXISTS #Shifts; DROP TABLE IF EXISTS #Coverage;
CREATE TABLE #Shifts (Id INT, Location VARCHAR(30), StartAt DATETIME2, EndAt DATETIME2, Removed BIT);
CREATE TABLE #Coverage (Id INT, Location VARCHAR(30), StartAt DATETIME2, EndAt DATETIME2);
INSERT INTO #Shifts VALUES
(1,'Wichita','2026-10-12T08:00:00','2026-10-12T16:00:00',0),
(2,'Wichita','2026-10-12T12:00:00','2026-10-12T20:00:00',0),
(3,'Wichita','2026-10-13T09:00:00','2026-10-13T17:00:00',1),
(4,'Wichita','2026-10-14T22:00:00','2026-10-15T06:00:00',0),
(5,'Memphis','2026-10-11T22:00:00','2026-10-12T04:00:00',0),
(6,'Memphis','2026-10-18T20:00:00','2026-10-19T04:00:00',0),
(7,'Memphis','2026-10-16T08:00:00','2026-10-16T12:00:00',0);
INSERT INTO #Coverage VALUES
(101,'Wichita','2026-10-12T08:00:00','2026-10-12T13:00:00'),
(102,'Wichita','2026-10-12T12:00:00','2026-10-12T15:00:00'),
(103,'Wichita','2026-10-12T18:00:00','2026-10-12T20:00:00'),
(104,'Wichita','2026-10-14T23:00:00','2026-10-15T03:00:00'),
(105,'Memphis','2026-10-11T23:00:00','2026-10-12T02:00:00'),
(106,'Memphis','2026-10-18T21:00:00','2026-10-19T02:00:00'),
(107,'Memphis','2026-10-16T07:00:00','2026-10-16T09:00:00');

DECLARE @From DATETIME2 = '2026-10-12T00:00:00', @To DATETIME2 = '2026-10-19T00:00:00';

WITH
-- 1) Keep rows that overlap the window and clip them to it. Removed shifts are excluded here
--    but stay in the table for audit.
S AS (
    SELECT Location,
           CASE WHEN StartAt < @From THEN @From ELSE StartAt END AS StartAt,
           CASE WHEN EndAt   > @To   THEN @To   ELSE EndAt   END AS EndAt
    FROM #Shifts
    WHERE Removed = 0 AND StartAt < @To AND EndAt > @From AND EndAt > StartAt),
C AS (
    SELECT Location,
           CASE WHEN StartAt < @From THEN @From ELSE StartAt END AS StartAt,
           CASE WHEN EndAt   > @To   THEN @To   ELSE EndAt   END AS EndAt
    FROM #Coverage
    WHERE StartAt < @To AND EndAt > @From AND EndAt > StartAt),
-- 2) Gaps-and-islands: a row starts a new island when it begins after every earlier row has ended.
SFlag AS (
    SELECT *, CASE WHEN StartAt <= MAX(EndAt) OVER (PARTITION BY Location ORDER BY StartAt, EndAt
                    ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) THEN 0 ELSE 1 END AS NewIsland
    FROM S),
SIsland AS (
    SELECT *, SUM(NewIsland) OVER (PARTITION BY Location ORDER BY StartAt, EndAt ROWS UNBOUNDED PRECEDING) AS Grp
    FROM SFlag),
SchedU AS (SELECT Location, MIN(StartAt) AS StartAt, MAX(EndAt) AS EndAt FROM SIsland GROUP BY Location, Grp),
CFlag AS (
    SELECT *, CASE WHEN StartAt <= MAX(EndAt) OVER (PARTITION BY Location ORDER BY StartAt, EndAt
                    ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) THEN 0 ELSE 1 END AS NewIsland
    FROM C),
CIsland AS (
    SELECT *, SUM(NewIsland) OVER (PARTITION BY Location ORDER BY StartAt, EndAt ROWS UNBOUNDED PRECEDING) AS Grp
    FROM CFlag),
CovU AS (SELECT Location, MIN(StartAt) AS StartAt, MAX(EndAt) AS EndAt FROM CIsland GROUP BY Location, Grp),
-- 3) Covered = schedule islands intersected with coverage islands. Both sets are disjoint
--    inside a location, so the pairwise overlaps don't overlap each other either.
Sched AS (SELECT Location, SUM(DATEDIFF(MINUTE, StartAt, EndAt)) AS SchedMin FROM SchedU GROUP BY Location),
Cov AS (
    SELECT s.Location,
           SUM(DATEDIFF(MINUTE,
               CASE WHEN c.StartAt > s.StartAt THEN c.StartAt ELSE s.StartAt END,
               CASE WHEN c.EndAt   < s.EndAt   THEN c.EndAt   ELSE s.EndAt   END)) AS CovMin
    FROM SchedU s JOIN CovU c
      ON c.Location = s.Location AND c.StartAt < s.EndAt AND c.EndAt > s.StartAt
    GROUP BY s.Location),
K AS (
    SELECT s.Location, s.SchedMin, ISNULL(c.CovMin, 0) AS CovMin
    FROM Sched s LEFT JOIN Cov c ON c.Location = s.Location)
SELECT ISNULL(Location, 'GRAND TOTAL') AS Location,
       CAST(SUM(SchedMin) / 60.0 AS DECIMAL(9,2)) AS ScheduledHours,
       CAST(SUM(CovMin)   / 60.0 AS DECIMAL(9,2)) AS CoveredHours,
       CAST(SUM(CASE WHEN SchedMin > CovMin THEN SchedMin - CovMin ELSE 0 END) / 60.0 AS DECIMAL(9,2)) AS DarkHours
FROM K
GROUP BY ROLLUP(Location)
ORDER BY GROUPING(Location), Location;
