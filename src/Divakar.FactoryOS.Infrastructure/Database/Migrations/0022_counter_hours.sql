-- Hourly roll-ups of counter tags (kWh, parts, gas, water…): how much each counter rose in each UTC hour, as the sum of its
-- rises between readings (a drop — meter replaced, reset, rolled over — adds nothing). Kept up to date on every write, so
-- energy, carbon, OEE and ISO 50001 reports read a few hundred rows instead of every raw reading, and the history outlives
-- raw-reading retention (baselines usually need 12 months).

CREATE TABLE telemetry.CounterHours (
    TagId       uniqueidentifier NOT NULL,
    Hour        datetime2(0)     NOT NULL,      -- UTC hour start
    TenantId    uniqueidentifier NOT NULL,
    Rise        float            NOT NULL,
    CONSTRAINT PK_CounterHours PRIMARY KEY (TagId, Hour)
);
CREATE INDEX IX_CounterHours_Tenant ON telemetry.CounterHours(TenantId, Hour) INCLUDE (Rise);
GO

CREATE TYPE telemetry.TagHours AS TABLE (
    TagId   uniqueidentifier NOT NULL,
    Hour    datetime2(0)     NOT NULL,
    PRIMARY KEY (TagId, Hour)
);
GO

-- Recomputes the given hours of counter tags (other tags are ignored). An hour's first rise is measured from the last
-- reading before it, so late or back-filled readings are handled by recomputing their hour and the hour after.
CREATE PROCEDURE telemetry.RollupCounterHours @hours telemetry.TagHours READONLY
AS
BEGIN
    SET NOCOUNT ON;
    WITH wanted AS (
        SELECT h.TagId, h.Hour, t.TenantId
        FROM @hours h
        JOIN plant.Tags t ON t.TagId = h.TagId
        JOIN plant.SensorTypes st ON st.SensorTypeCode = t.SensorTypeCode AND st.SimProfile LIKE 'counter%'
    ), pts AS (
        SELECT w.TagId, w.Hour, w.TenantId, x.Ts, x.Value
        FROM wanted w
        CROSS APPLY (
            SELECT r.Ts, r.Value FROM telemetry.Readings r
            WHERE r.TagId = w.TagId AND r.Ts >= w.Hour AND r.Ts < DATEADD(HOUR, 1, w.Hour) AND r.Value IS NOT NULL AND r.Quality <= 1
            UNION ALL
            SELECT p.Ts, p.Value FROM (
                SELECT TOP (1) r.Ts, r.Value FROM telemetry.Readings r
                WHERE r.TagId = w.TagId AND r.Ts < w.Hour AND r.Ts >= DATEADD(DAY, -2, w.Hour) AND r.Value IS NOT NULL AND r.Quality <= 1
                ORDER BY r.Ts DESC) p
        ) x
    ), steps AS (
        SELECT TagId, Hour, TenantId, Ts, Value - LAG(Value) OVER (PARTITION BY TagId, Hour ORDER BY Ts) AS Step FROM pts
    ), hourly AS (
        SELECT TagId, Hour, TenantId, SUM(CASE WHEN Ts >= Hour AND Step > 0 THEN Step ELSE 0 END) AS Rise
        FROM steps GROUP BY TagId, Hour, TenantId
        HAVING COUNT(CASE WHEN Ts >= Hour THEN 1 END) > 0
    )
    MERGE telemetry.CounterHours AS c
    USING hourly AS n ON c.TagId = n.TagId AND c.Hour = n.Hour
    WHEN MATCHED THEN UPDATE SET Rise = n.Rise
    WHEN NOT MATCHED THEN INSERT (TagId, Hour, TenantId, Rise) VALUES (n.TagId, n.Hour, n.TenantId, n.Rise);
END
GO

-- Roll up the history already stored (row-level security hides readings from a session that is not marked as the system)
EXEC sp_set_session_context N'System', 1;
WITH steps AS (
    SELECT r.TagId, r.TenantId, r.Ts,
           r.Value - LAG(r.Value) OVER (PARTITION BY r.TagId ORDER BY r.Ts) AS Step
    FROM telemetry.Readings r
    WHERE r.Value IS NOT NULL AND r.Quality <= 1
      AND r.TagId IN (SELECT t.TagId FROM plant.Tags t JOIN plant.SensorTypes st ON st.SensorTypeCode = t.SensorTypeCode WHERE st.SimProfile LIKE 'counter%')
)
INSERT telemetry.CounterHours (TagId, Hour, TenantId, Rise)
SELECT TagId, CAST(DATEADD(HOUR, DATEDIFF(HOUR, CAST('2000-01-01' AS datetime2(0)), Ts), CAST('2000-01-01' AS datetime2(0))) AS datetime2(0)), TenantId,
       SUM(CASE WHEN Step > 0 THEN Step ELSE 0 END)
FROM steps
GROUP BY TagId, TenantId, CAST(DATEADD(HOUR, DATEDIFF(HOUR, CAST('2000-01-01' AS datetime2(0)), Ts), CAST('2000-01-01' AS datetime2(0))) AS datetime2(0));
GO

-- Reports filter by tenant and row-level security reads TenantId, so the tag/time index carries it too (no look-ups)
CREATE INDEX IX_Readings_Tag_Ts ON telemetry.Readings(TagId, Ts) INCLUDE (Value, Quality, TenantId) WITH (DROP_EXISTING = ON);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON telemetry.CounterHours;
GO
