using System;
using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace _125GateApp
{
    /// <summary>
    /// 乘客 OD 分析查询：分时客流、断面客流，以及乘车记录写入。
    /// </summary>
    public class OdAnalysisRepository
    {
        private readonly string connectionString;

        public OdAnalysisRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        /// <summary>
        /// 分时客流：固定返回 00:00 ~ 23:00 共 24 个时段，
        /// 只统计所选区间内的数据，切换区间时坐标轴不变。
        /// </summary>
        public DataTable GetFlowByTime(DateTime from, DateTime to, bool downDirection)
        {
            string directionFilter = downDirection
                ? "board_seq > alight_seq"
                : "board_seq < alight_seq";

            string sql = @"
                SELECT lpad(h.hour::text, 2, '0') AS ""时段"",
                       COALESCE(f.trips, 0)::int AS ""乘次""
                FROM generate_series(0, 23) AS h(hour)
                LEFT JOIN (
                    SELECT extract(hour from board_time)::int AS hour,
                           count(*)::int AS trips
                    FROM trip
                    WHERE board_time >= @from AND board_time < @to
                      AND " + directionFilter + @"
                    GROUP BY 1
                ) f ON f.hour = h.hour
                ORDER BY h.hour;";

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                AddTimeRange(cmd, from, to);

                DataTable dt = new DataTable();
                using (NpgsqlDataAdapter adapter = new NpgsqlDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
                return dt;
            }
        }

        /// <summary>
        /// 分时客流的全量峰值，用于固定 Y 轴刻度。
        /// </summary>
        public int GetHourlyPeak()
        {
            const string sql = @"
                SELECT COALESCE(MAX(cnt), 0)
                FROM (
                    SELECT count(*) AS cnt
                    FROM trip
                    GROUP BY date_trunc('hour', board_time)
                ) t;";

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 断面客流的全量峰值，用于固定 X 轴刻度。
        /// </summary>
        public int GetSegmentPeak()
        {
            const string sql = @"
                SELECT COALESCE(MAX(cnt), 0)
                FROM (
                    SELECT count(*) AS cnt
                    FROM station_distance d
                    JOIN trip t
                      ON d.seq_from >= LEAST(t.board_seq, t.alight_seq)
                     AND d.seq_to   <= GREATEST(t.board_seq, t.alight_seq)
                    GROUP BY d.seq_from
                ) x;";

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>
        /// 断面客流：统计每个相邻区间被多少条乘车记录跨越。
        /// </summary>
        public DataTable GetFlowBySegment(DateTime from, DateTime to, bool downDirection)
        {
            string directionFilter = downDirection
                ? "t.board_seq > t.alight_seq"
                : "t.board_seq < t.alight_seq";

            string sql = @"
                SELECT d.seq_from || '→' || d.seq_to AS ""路段"",
                       s1.name || ' → ' || s2.name AS ""全称"",
                       count(t.trip_id)::int AS ""人次""
                FROM station_distance d
                JOIN station s1 ON s1.seq = d.seq_from
                JOIN station s2 ON s2.seq = d.seq_to
                LEFT JOIN trip t
                       ON d.seq_from >= LEAST(t.board_seq, t.alight_seq)
                      AND d.seq_to   <= GREATEST(t.board_seq, t.alight_seq)
                      AND t.board_time >= @from AND t.board_time < @to
                      AND " + directionFilter + @"
                GROUP BY d.seq_from, d.seq_to, s1.name, s2.name
                ORDER BY d.seq_from;";

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                AddTimeRange(cmd, from, to);

                DataTable dt = new DataTable();
                using (NpgsqlDataAdapter adapter = new NpgsqlDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
                return dt;
            }
        }

        /// <summary>
        /// 写入一条乘车记录。卡号在 card 表中不存在时先补一条，避免外键失败。
        /// </summary>
        public void InsertTrip(string cardNo, int boardSeq, int alightSeq,
                               DateTime boardTime, DateTime alightTime,
                               int distanceM, decimal fare)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();

                using (NpgsqlTransaction tx = conn.BeginTransaction())
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "INSERT INTO card(card_no, balance) VALUES (@card, 0) " +
                        "ON CONFLICT (card_no) DO NOTHING", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        cmd.ExecuteNonQuery();
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "INSERT INTO trip(card_no, board_seq, alight_seq, board_time, alight_time, distance_m, fare) " +
                        "VALUES (@card, @board, @alight, @board_time, @alight_time, @distance, @fare)", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        cmd.Parameters.AddWithValue("board", boardSeq);
                        cmd.Parameters.AddWithValue("alight", alightSeq);
                        cmd.Parameters.Add(new NpgsqlParameter("board_time", NpgsqlDbType.TimestampTz)
                        {
                            Value = ToUtc(boardTime)
                        });
                        cmd.Parameters.Add(new NpgsqlParameter("alight_time", NpgsqlDbType.TimestampTz)
                        {
                            Value = ToUtc(alightTime)
                        });
                        cmd.Parameters.AddWithValue("distance", distanceM);
                        cmd.Parameters.AddWithValue("fare", fare);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }
        }

        private static void AddTimeRange(NpgsqlCommand cmd, DateTime from, DateTime to)
        {
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz)
            {
                Value = ToUtc(from)
            });
            cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz)
            {
                Value = ToUtc(to)
            });
        }

        /// <summary>
        /// Npgsql 6+ 写 timestamptz 只接受 Kind=Utc 的 DateTime。
        /// </summary>
        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }
            return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
        }
    }
}
