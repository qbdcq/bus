import getpass
import sys

import psycopg


DB_CONFIG = {
    "host": "aws-1-ap-southeast-1.pooler.supabase.com",
    "port": 5432,
    "dbname": "postgres",
    "user": "postgres.uobfxexmerutzwcceies",
    "password": "aTGek0R4PSc9UJGF",
    "connect_timeout": 10,
    "sslmode": "require",
}


def main() -> int:
    #DB_CONFIG["password"] = getpass.getpass("Supabase database password: ")

    try:
        with psycopg.connect(**DB_CONFIG) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT table_schema, column_name, data_type, is_nullable
                    FROM information_schema.columns
                    WHERE table_name = %s
                      AND table_schema IN (%s, %s)
                    ORDER BY table_schema, ordinal_position
                    """,
                    ("station", "public", "Bus-db"),
                )
                columns = cur.fetchall()

        if not columns:
            print("未找到 public.station 或 Bus-db.station 表，或当前用户无权查看其字段。")
            return 1

        print("station 表字段：")
        for schema, name, data_type, nullable in columns:
            print(f"{schema}.station.{name} | 类型: {data_type} | 可空: {nullable}")
        return 0
    except psycopg.Error as exc:
        print(f"查询失败：{exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
