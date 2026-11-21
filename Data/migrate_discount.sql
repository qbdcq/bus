-- fare_rule 增加卡属性折扣价格方案：普通/学生/敬老/爱心 各一个折扣系数（0=免费，1=全价）
-- 说明：card.property 以中文存四种属性，折扣系数集中在 fare_rule（随线路规则一起生效）。
-- 用法：psql -U postgres -d bus -f "e:\ncist\bus\Data\migrate_discount.sql"

\set ON_ERROR_STOP on
\encoding UTF8

BEGIN;

ALTER TABLE fare_rule ADD COLUMN IF NOT EXISTS discount_normal  NUMERIC(3,2) NOT NULL DEFAULT 1.00;
ALTER TABLE fare_rule ADD COLUMN IF NOT EXISTS discount_student NUMERIC(3,2) NOT NULL DEFAULT 1.00;
ALTER TABLE fare_rule ADD COLUMN IF NOT EXISTS discount_elder   NUMERIC(3,2) NOT NULL DEFAULT 1.00;
ALTER TABLE fare_rule ADD COLUMN IF NOT EXISTS discount_love    NUMERIC(3,2) NOT NULL DEFAULT 1.00;

-- 现有规则行填充价格方案：普通全价 / 学生 5 折 / 敬老 5 折 / 爱心免费（可按需要调整）
UPDATE fare_rule SET
    discount_normal  = 1.00,
    discount_student = 0.50,
    discount_elder   = 0.50,
    discount_love    = 0.00;

ALTER TABLE fare_rule DROP CONSTRAINT IF EXISTS fare_rule_discount_range_check;
ALTER TABLE fare_rule ADD CONSTRAINT fare_rule_discount_range_check
    CHECK (discount_normal  >= 0 AND discount_normal  <= 1
       AND discount_student >= 0 AND discount_student <= 1
       AND discount_elder   >= 0 AND discount_elder   <= 1
       AND discount_love    >= 0 AND discount_love    <= 1);

SELECT rule_id, base_fare, discount_normal, discount_student, discount_elder, discount_love
FROM fare_rule ORDER BY rule_id;

COMMIT;