-- 支持“未刷卡下车”与“一卡同时只能有一条未完成行程”
-- 用法：psql -U postgres -d bus -f "e:\ncist\bus\Data\migrate_open_trip.sql"

BEGIN;

-- 1. 允许 alight_seq 为空（上车后尚未下车）
ALTER TABLE trip DROP CONSTRAINT IF EXISTS trip_alight_seq_not_null;
ALTER TABLE trip ALTER COLUMN alight_seq DROP NOT NULL;

-- 2. 记录上车预扣金额和行程状态
ALTER TABLE trip ADD COLUMN IF NOT EXISTS pre_deducted NUMERIC(10,2) NOT NULL DEFAULT 0;
ALTER TABLE trip ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'completed';

-- 3. 状态约束
ALTER TABLE trip DROP CONSTRAINT IF EXISTS trip_status_check;
ALTER TABLE trip ADD CONSTRAINT trip_status_check
    CHECK (status IN ('open', 'completed', 'missing_alight'));

-- 4. 同一张卡最多只能有一条未完成行程：防止未下车重复刷上车
CREATE UNIQUE INDEX IF NOT EXISTS ux_trip_open ON trip(card_no) WHERE status = 'open';

-- 5. 分析查询用的索引
CREATE INDEX IF NOT EXISTS ix_trip_board_time ON trip(board_time);
CREATE INDEX IF NOT EXISTS ix_trip_card_status ON trip(card_no, status);

-- 6. 把原来的 db.json 卡片迁移到数据库（余额为负的按 0 处理，空卡号跳过）
INSERT INTO card(card_no, balance) VALUES
    ('1234567899', 100.00),
    ('0123456789',  50.00),
    ('0005860000',  30.00),
    ('1111111111',  20.00),
    ('0006849456',  15.50),
    ('0006852948',  97.00),
    ('0006624725',   7.40),
    ('0006081976',  48.50)
ON CONFLICT (card_no) DO NOTHING;

COMMIT;

SELECT column_name, is_nullable, column_default
FROM information_schema.columns
WHERE table_name = 'trip' AND column_name IN ('alight_seq', 'pre_deducted', 'status');

SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'trip';
