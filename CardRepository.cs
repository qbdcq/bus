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
                    long tripId;
                    decimal preDeducted;

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT trip_id, pre_deducted FROM trip " +
                        "WHERE card_no = @card AND status = 'open' " +
                        "ORDER BY trip_id DESC LIMIT 1 FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        using (NpgsqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                tx.Rollback();
                                return null;
                            }
                            tripId = reader.GetInt64(0);
                            preDeducted = reader.GetDecimal(1);
                        }
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

                    long tripId;
                    int boardSeq;
                    decimal preDeducted;

                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "SELECT trip_id, board_seq, pre_deducted FROM trip " +
                        "WHERE card_no = @card AND status = 'open' " +
                        "ORDER BY trip_id DESC LIMIT 1 FOR UPDATE", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("card", cardNo);
                        using (NpgsqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                tx.Rollback();
                                return new EndTripResult { Status = EndTripStatus.NoOpenTrip };
                            }
                            tripId = reader.GetInt64(0);
                            boardSeq = reader.GetInt32(1);
                            preDeducted = reader.GetDecimal(2);
                        }
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
