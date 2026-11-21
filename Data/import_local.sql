-- 导入 CSV 到本地 PostgreSQL（五表方案）
-- 用法（在项目根目录 e:\ncist\bus 下执行）：
--   chcp 65001
--   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -d bus -f "e:\ncist\bus\Data\import_local.sql"
--
-- 前提：station / station_distance / card / trip / fare_rule 五张表已建好。

\set ON_ERROR_STOP on
\encoding UTF8

BEGIN;

-- 1. 站点：CSV 的累计里程只是中间数据，不直接入库
CREATE TEMP TABLE stg_station (
    line_id        INT,
    seq            INT,
    station_name   TEXT,
    cum_distance_m INT
) ON COMMIT DROP;

\copy stg_station FROM 'e:/ncist/bus/Data/stations.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')

INSERT INTO station(seq, name)
SELECT seq, station_name
FROM stg_station
ON CONFLICT (seq) DO UPDATE SET name = EXCLUDED.name;

-- 2. 相邻站距离：累计里程相邻相减
DELETE FROM station_distance;

INSERT INTO station_distance(seq_from, seq_to, distance_m)
SELECT seq, seq + 1, next_cum - cum_distance_m
FROM (
    SELECT seq,
           cum_distance_m,
           LEAD(cum_distance_m) OVER (ORDER BY seq) AS next_cum
    FROM stg_station
) t
WHERE next_cum IS NOT NULL;

-- 3. 计价规则
CREATE TEMP TABLE stg_rule (
    line_id         INT,
    base_fare       NUMERIC(6,2),
    base_distance_m INT,
    step_distance_m INT,
    step_fare       NUMERIC(6,2),
    max_fare        NUMERIC(6,2),
    effective_from  TEXT,
    effective_to    TEXT
) ON COMMIT DROP;

\copy stg_rule FROM 'e:/ncist/bus/Data/fare_rules.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')

DELETE FROM fare_rule;

INSERT INTO fare_rule(base_fare, base_distance_m, step_distance_m, step_fare,
                      max_fare, effective_from, effective_to)
SELECT base_fare,
       base_distance_m,
       step_distance_m,
       step_fare,
       max_fare,
       COALESCE(NULLIF(effective_from, '')::date, CURRENT_DATE),
       NULLIF(effective_to, '')::date
FROM stg_rule;

-- 4. 数据校验：任何一项不通过都会抛异常并整体回滚
DO $$
DECLARE
    v_station_count INT;
    v_seg_count     INT;
    v_bad_gap       INT;
    v_total_m       INT;
    v_expected_m    INT;
    v_rule_count    INT;
BEGIN
    SELECT count(*) INTO v_station_count FROM station;
    IF v_station_count <> 16 THEN
        RAISE EXCEPTION '站台数应为 16，实际 %', v_station_count;
    END IF;

    SELECT count(*) INTO v_seg_count FROM station_distance;
    IF v_seg_count <> v_station_count - 1 THEN
        RAISE EXCEPTION '相邻区间数应为 %，实际 %', v_station_count - 1, v_seg_count;
    END IF;

    -- 除终点外，每段都应有下一段接上，否则中间断链
    SELECT count(*) INTO v_bad_gap
    FROM station_distance d
    WHERE d.seq_to <> (SELECT max(seq) FROM station)
      AND NOT EXISTS (
          SELECT 1 FROM station_distance d2 WHERE d2.seq_from = d.seq_to
      );

    IF v_bad_gap > 0 THEN
        RAISE EXCEPTION '存在断链的相邻区间，共 % 处', v_bad_gap;
    END IF;

    SELECT COALESCE(SUM(distance_m), 0) INTO v_total_m FROM station_distance;
    SELECT COALESCE(MAX(cum_distance_m), 0) INTO v_expected_m FROM stg_station;

    IF v_total_m <> v_expected_m THEN
        RAISE EXCEPTION '相邻距离合计 % m 与 CSV 累计里程 % m 不一致', v_total_m, v_expected_m;
    END IF;

    SELECT count(*) INTO v_rule_count FROM fare_rule;
    IF v_rule_count = 0 THEN
        RAISE EXCEPTION '计价规则为空';
    END IF;

    RAISE NOTICE '校验通过：% 个站台、% 段相邻距离、总里程 % m、% 条计价规则',
        v_station_count, v_seg_count, v_total_m, v_rule_count;
END $$;

COMMIT;

-- 5. 结果查看
SELECT seq, name FROM station ORDER BY seq;

SELECT seq_from, seq_to, distance_m FROM station_distance ORDER BY seq_from;

SELECT rule_id, base_fare, base_distance_m, step_distance_m, step_fare, max_fare
FROM fare_rule ORDER BY effective_from DESC;

-- 抽样：1 -> 16 的里程与票价（预期 37500 m、8.00 元）
WITH seg AS (
    SELECT COALESCE(SUM(distance_m), 0) AS distance_m
    FROM station_distance
    WHERE seq_from >= 1 AND seq_to <= 16
)
SELECT seg.distance_m,
       r.base_fare
       + CEIL(GREATEST(seg.distance_m - r.base_distance_m, 0)::numeric / r.step_distance_m) * r.step_fare
       AS fare
FROM seg
CROSS JOIN (
    SELECT * FROM fare_rule
    ORDER BY effective_from DESC
    LIMIT 1
) r;
