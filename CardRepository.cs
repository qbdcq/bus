using System;
using Npgsql;
using NpgsqlTypes;

namespace _125GateApp
{
    public class OpenTripInfo
    {
        public long TripId { get; set; }
        public int BoardSeq { get; set; }
        public decimal PreDeducted { get; set; }
    }

    public enum StartTripStatus
    {
        Success,
        CardNotFound,
        InsufficientBalance,
        AlreadyOnBoard
    }

    public class StartTripResult
    {
        public StartTripStatus Status { get; set; }
        public decimal Balance { get; set; }
    }

    public enum EndTripStatus
    {
        Success,
        CardNotFound,
        NoOpenTrip
    }

    public class EndTripResult
    {
        public EndTripStatus Status { get; set; }
        public int BoardSeq { get; set; }
        public decimal Refund { get; set; }
        public decimal Balance { get; set; }
    }

    /// <summary>
    /// 卡余额与行程的数据库读写。上车预扣全程票价，下车按实际里程退差。
    /// </summary>
    public class CardRepository
    {
        private readonly string connectionString;

        public CardRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        public decimal? GetBalance(string cardNo)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT balance FROM card WHERE card_no = @card", conn))
                {
                    cmd.Parameters.AddWithValue("card", cardNo);
                    object value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value ? (decimal?)null : (decimal)value;
                }
            }
        }

        /// <summary>
        /// 轻量连通性探测：仅执行 SELECT 1，用于空闲时兜底测量数据库延迟。
        /// </summary>
        public void Ping()
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT 1", conn))
                {
                    cmd.ExecuteScalar();
                }
            }
        }

        /// <summary>
        /// 充值；卡不存在时自动创建。
        /// </summary>
        public decimal Recharge(string cardNo, decimal amount)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO card(card_no, balance) VALUES(@card, @amount) " +
                    "ON CONFLICT (card_no) DO UPDATE " +
                    "SET balance = card.balance + EXCLUDED.balance, updated_at = now() " +
                    "RETURNING balance", conn))
                {
                    cmd.Parameters.AddWithValue("card", cardNo);
                    cmd.Parameters.AddWithValue("amount", amount);
                    return (decimal)cmd.ExecuteScalar();
                }
            }
        }

        /// <summary>
        /// 读取卡属性（普通卡/学生卡/敬老卡/爱心卡）；卡不存在或属性未设置时返回 null。
        /// </summary>
        public string GetCardProperty(string cardNo)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT property FROM card WHERE card_no = @card", conn))
                {
                    cmd.Parameters.AddWithValue("card", cardNo);
                    object value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value ? null : (string)value;
                }
            }
        }

        /// <summary>
        /// 保存卡属性；卡不存在时返回 false（按注册充值页流程，应先充值建卡）。
        /// </summary>
        public bool UpdateCardProperty(string cardNo, string property)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE card SET property = @property, updated_at = now() " +
                    "WHERE card_no = @card RETURNING card_no", conn))
                {
                    cmd.Parameters.AddWithValue("property", property);
                    cmd.Parameters.AddWithValue("card", cardNo);
                    return cmd.ExecuteScalar() != null;
                }
            }
        }

        public OpenTripInfo GetOpenTrip(string cardNo)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT trip_id, board_seq, pre_deducted FROM trip " +
                    "WHERE card_no = @card AND status = 'open' ORDER BY trip_id DESC LIMIT 1", conn))
                {
                    cmd.Parameters.AddWithValue("card", cardNo);
                    using (NpgsqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }
                        return new OpenTripInfo
                        {
                            TripId = reader.GetInt64(0),
                            BoardSeq = reader.GetInt32(1),
                            PreDeducted = reader.GetDecimal(2)
                        };
                    }
                }
            }
        }

        /// <summary>
        /// 结算上一次忘记刷卡的行程：预扣的全程票价不再退还，状态记为 missing_alight。
        /// 返回被结算的金额；没有未完成行程时返回 null。
        /// </summary>
        public decimal? CloseForgottenTrip(string cardNo)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlTransaction tx = conn.BeginTransaction())
                {
                    long tripId = 0;
                    decimal preDeducted = 0;
                    bool found;

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT trip_id, pre_deducted FROM trip " +
                        "WHERE card_no = @card AND status = 'open' " +
                        "ORDER BY trip_id DESC LIMIT 1 FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        using (NpgsqlDataReader reader = cmd.ExecuteReader())
                        {
                            found = reader.Read();
                            if (found)
                            {
                                tripId = reader.GetInt64(0);
                                preDeducted = reader.GetDecimal(1);
                            }
                        }
                    }

                    // 必须在 reader 关闭之后再回滚，否则 Npgsql 会报
                    // "A command is already in progress"
                    if (!found)
                    {
                        tx.Rollback();
                        return null;
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "UPDATE trip SET status = 'missing_alight', fare = pre_deducted " +
                        "WHERE trip_id = @id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("id", tripId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return preDeducted;
                }
            }
        }

        /// <summary>
        /// 上车：校验余额、扣全程票价、插入一条 open 行程。
        /// </summary>
        public StartTripResult StartTrip(string cardNo, int boardSeq, DateTime boardTime, decimal preDeduct)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlTransaction tx = conn.BeginTransaction())
                {
                    decimal balance;

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT balance FROM card WHERE card_no = @card FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        object value = cmd.ExecuteScalar();

                        if (value == null || value == DBNull.Value)
                        {
                            tx.Rollback();
                            return new StartTripResult { Status = StartTripStatus.CardNotFound };
                        }
                        balance = (decimal)value;
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT 1 FROM trip WHERE card_no = @card AND status = 'open' LIMIT 1", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        if (cmd.ExecuteScalar() != null)
                        {
                            tx.Rollback();
                            return new StartTripResult
                            {
                                Status = StartTripStatus.AlreadyOnBoard,
                                Balance = balance
                            };
                        }
                    }

                    if (balance < preDeduct)
                    {
                        tx.Rollback();
                        return new StartTripResult
                        {
                            Status = StartTripStatus.InsufficientBalance,
                            Balance = balance
                        };
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "UPDATE card SET balance = balance - @pre, updated_at = now() " +
                        "WHERE card_no = @card", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        cmd.Parameters.AddWithValue("pre", preDeduct);
                        cmd.ExecuteNonQuery();
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "INSERT INTO trip(card_no, board_seq, board_time, pre_deducted, status) " +
                        "VALUES(@card, @board, @time, @pre, 'open')", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        cmd.Parameters.AddWithValue("board", boardSeq);
                        cmd.Parameters.Add(new NpgsqlParameter("time", NpgsqlDbType.TimestampTz)
                        {
                            Value = ToUtc(boardTime)
                        });
                        cmd.Parameters.AddWithValue("pre", preDeduct);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return new StartTripResult
                    {
                        Status = StartTripStatus.Success,
                        Balance = balance - preDeduct
                    };
                }
            }
        }

        /// <summary>
        /// 下车：按实际票价退差、补全行程。返回退还给乘客的金额和最新余额。
        /// </summary>
        public EndTripResult EndTrip(string cardNo, int alightSeq, DateTime alightTime,
                                     int distanceM, decimal actualFare)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlTransaction tx = conn.BeginTransaction())
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT 1 FROM card WHERE card_no = @card FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        if (cmd.ExecuteScalar() == null)
                        {
                            tx.Rollback();
                            return new EndTripResult { Status = EndTripStatus.CardNotFound };
                        }
                    }

                    long tripId = 0;
                    int boardSeq = 0;
                    decimal preDeducted = 0;
                    bool found;

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT trip_id, board_seq, pre_deducted FROM trip " +
                        "WHERE card_no = @card AND status = 'open' " +
                        "ORDER BY trip_id DESC LIMIT 1 FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        using (NpgsqlDataReader reader = cmd.ExecuteReader())
                        {
                            found = reader.Read();
                            if (found)
                            {
                                tripId = reader.GetInt64(0);
                                boardSeq = reader.GetInt32(1);
                                preDeducted = reader.GetDecimal(2);
                            }
                        }
                    }

                    if (!found)
                    {
                        tx.Rollback();
                        return new EndTripResult { Status = EndTripStatus.NoOpenTrip };
                    }

                    decimal refund = preDeducted - actualFare;
                    if (refund < 0)
                    {
                        refund = 0;
                    }

                    decimal balance;
                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "UPDATE card SET balance = balance + @refund, updated_at = now() " +
                        "WHERE card_no = @card RETURNING balance", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        cmd.Parameters.AddWithValue("refund", refund);
                        balance = (decimal)cmd.ExecuteScalar();
                    }

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "UPDATE trip SET alight_seq = @alight, alight_time = @time, " +
                        "distance_m = @distance, fare = @fare, status = 'completed' " +
                        "WHERE trip_id = @id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("alight", alightSeq);
                        cmd.Parameters.Add(new NpgsqlParameter("time", NpgsqlDbType.TimestampTz)
                        {
                            Value = ToUtc(alightTime)
                        });
                        cmd.Parameters.AddWithValue("distance", distanceM);
                        cmd.Parameters.AddWithValue("fare", actualFare);
                        cmd.Parameters.AddWithValue("id", tripId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return new EndTripResult
                    {
                        Status = EndTripStatus.Success,
                        BoardSeq = boardSeq,
                        Refund = refund,
                        Balance = balance
                    };
                }
            }
        }

        /// <summary>
        /// 到达终点站时，统一结算所有未刷卡下车的行程：
        /// 补全下车站、下车时间、里程，票价按上车时预扣的全程票价，状态记为 missing_alight。
        /// 返回被结算的行程数。
        /// </summary>
        public int SettleOpenTripsAtLastStation(int lastSeq, DateTime alightTime)
        {
            const string sql = @"
                UPDATE trip t
                SET alight_seq = @last,
                    alight_time = @time,
                    distance_m = (
                        SELECT COALESCE(SUM(d.distance_m), 0)
                        FROM station_distance d
                        WHERE d.seq_from >= LEAST(t.board_seq, @last)
                          AND d.seq_to   <= GREATEST(t.board_seq, @last)
                    ),
                    fare = t.pre_deducted,
                    status = 'missing_alight'
                WHERE t.status = 'open'";

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();
                using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("last", lastSeq);
                    cmd.Parameters.Add(new NpgsqlParameter("time", NpgsqlDbType.TimestampTz)
                    {
                        Value = ToUtc(alightTime)
                    });
                    return cmd.ExecuteNonQuery();
                }
            }
        }

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