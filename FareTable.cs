using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Npgsql;

namespace _125GateApp
{
    public class StationItem
    {
        public int LineId { get; set; }
        public int Seq { get; set; }
        public string StationName { get; set; }
        public int CumDistanceM { get; set; }
    }

    public class FareRule
    {
        public int LineId { get; set; }
        public decimal BaseFare { get; set; }
        public int BaseDistanceM { get; set; }
        public int StepDistanceM { get; set; }
        public decimal StepFare { get; set; }
        public decimal? MaxFare { get; set; }
        public string EffectiveFrom { get; set; }
        public string EffectiveTo { get; set; }
    }

    /// <summary>
    /// 里程票价表：从 Data\stations.csv 与 Data\fare_rules.csv 加载站点与计价规则，
    /// 按“起步价 + 每档加价”实时计算任意两站之间的票价。
    /// </summary>
    public class FareTable
    {
        private readonly Dictionary<int, StationItem> bySeq = new Dictionary<int, StationItem>();
        private readonly Dictionary<Tuple<int, int>, decimal> cache = new Dictionary<Tuple<int, int>, decimal>();

        public FareRule Rule { get; private set; }
        public IList<StationItem> Stations { get; private set; }

        private FareTable()
        {
        }

        /// <summary>
        /// 从程序目录的 Data 文件夹加载默认价目表。
        /// </summary>
        public static FareTable LoadDefault()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            return Load(Path.Combine(dir, "stations.csv"), Path.Combine(dir, "fare_rules.csv"));
        }

        public static FareTable Load(string stationsCsvPath, string ruleCsvPath)
        {
            List<StationItem> stations = ReadCsv(stationsCsvPath)
                .Select(delegate(Dictionary<string, string> row)
                {
                    return new StationItem
                    {
                        LineId = int.Parse(row["line_id"], CultureInfo.InvariantCulture),
                        Seq = int.Parse(row["seq"], CultureInfo.InvariantCulture),
                        StationName = row["station_name"],
                        CumDistanceM = int.Parse(row["cum_distance_m"], CultureInfo.InvariantCulture)
                    };
                })
                .OrderBy(s => s.Seq)
                .ToList();

            if (stations.Count < 2)
            {
                throw new InvalidDataException("站点表至少需要 2 条记录：" + stationsCsvPath);
            }

            List<Dictionary<string, string>> ruleRows = ReadCsv(ruleCsvPath);
            if (ruleRows.Count == 0)
            {
                throw new InvalidDataException("计价规则为空：" + ruleCsvPath);
            }

            int lineId = stations[0].LineId;
            Dictionary<string, string> ruleRow = ruleRows.FirstOrDefault(
                r => int.Parse(r["line_id"], CultureInfo.InvariantCulture) == lineId) ?? ruleRows[0];

            FareRule rule = new FareRule
            {
                LineId = int.Parse(ruleRow["line_id"], CultureInfo.InvariantCulture),
                BaseFare = decimal.Parse(ruleRow["base_fare"], CultureInfo.InvariantCulture),
                BaseDistanceM = int.Parse(ruleRow["base_distance_m"], CultureInfo.InvariantCulture),
                StepDistanceM = int.Parse(ruleRow["step_distance_m"], CultureInfo.InvariantCulture),
                StepFare = decimal.Parse(ruleRow["step_fare"], CultureInfo.InvariantCulture),
                MaxFare = string.IsNullOrWhiteSpace(ruleRow["max_fare"])
                    ? (decimal?)null
                    : decimal.Parse(ruleRow["max_fare"], CultureInfo.InvariantCulture),
                EffectiveFrom = ruleRow["effective_from"],
                EffectiveTo = ruleRow["effective_to"]
            };

            return Create(stations, rule);
        }

        /// <summary>
        /// 从 PostgreSQL 读取：station（站序、站名）、station_distance（相邻距离）、fare_rule（当前生效规则）。
        /// 相邻距离会在内存中累加成累计里程，后续计算与 CSV 版完全一致。
        /// </summary>
        public static FareTable FromDatabase(string connectionString)
        {
            List<StationItem> stations = new List<StationItem>();
            Dictionary<int, int> segmentByFrom = new Dictionary<int, int>();
            FareRule rule = null;

            using (NpgsqlConnection conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT seq, name FROM station ORDER BY seq", conn))
                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        stations.Add(new StationItem
                        {
                            Seq = reader.GetInt32(0),
                            StationName = reader.GetString(1)
                        });
                    }
                }

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT seq_from, distance_m FROM station_distance ORDER BY seq_from", conn))
                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        segmentByFrom[reader.GetInt32(0)] = reader.GetInt32(1);
                    }
                }

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT base_fare, base_distance_m, step_distance_m, step_fare, max_fare, effective_from::text " +
                    "FROM fare_rule " +
                    "WHERE effective_from <= CURRENT_DATE AND (effective_to IS NULL OR effective_to >= CURRENT_DATE) " +
                    "ORDER BY effective_from DESC LIMIT 1", conn))
                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        rule = new FareRule
                        {
                            BaseFare = reader.GetDecimal(0),
                            BaseDistanceM = reader.GetInt32(1),
                            StepDistanceM = reader.GetInt32(2),
                            StepFare = reader.GetDecimal(3),
                            MaxFare = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4),
                            EffectiveFrom = reader.GetString(5)
                        };
                    }
                }
            }

            if (rule == null)
            {
                throw new InvalidDataException("fare_rule 中没有当前生效的计价规则");
            }

            for (int i = 0; i < stations.Count - 1; i++)
            {
                if (stations[i + 1].Seq != stations[i].Seq + 1)
                {
                    throw new InvalidDataException("station 表的站序不连续，缺少 " + (stations[i].Seq + 1));
                }

                int distance;
                if (!segmentByFrom.TryGetValue(stations[i].Seq, out distance))
                {
                    throw new InvalidDataException(
                        "station_distance 缺少相邻区间：" + stations[i].Seq + " -> " + stations[i + 1].Seq);
                }
            }

            int cumulative = 0;
            foreach (StationItem station in stations)
            {
                station.CumDistanceM = cumulative;

                int distance;
                if (segmentByFrom.TryGetValue(station.Seq, out distance))
                {
                    cumulative += distance;
                }
            }

            return Create(stations, rule);
        }

        private static FareTable Create(List<StationItem> stations, FareRule rule)
        {
            if (stations.Count < 2)
            {
                throw new InvalidDataException("站点表至少需要 2 条记录");
            }

            FareTable table = new FareTable();

            foreach (StationItem station in stations)
            {
                if (string.IsNullOrWhiteSpace(station.StationName))
                {
                    throw new InvalidDataException("存在空的站名，站序 " + station.Seq);
                }
                if (table.bySeq.ContainsKey(station.Seq))
                {
                    throw new InvalidDataException("站序重复：" + station.Seq);
                }
                table.bySeq.Add(station.Seq, station);
            }

            for (int i = 1; i < stations.Count; i++)
            {
                if (stations[i].CumDistanceM < stations[i - 1].CumDistanceM)
                {
                    throw new InvalidDataException("累计里程必须随站序递增，问题站序：" + stations[i].Seq);
                }
            }

            if (rule.StepDistanceM <= 0)
            {
                throw new InvalidDataException("step_distance_m 必须大于 0");
            }

            table.Stations = stations;
            table.Rule = rule;
            return table;
        }

        public string GetStationName(int seq)
        {
            StationItem station;
            return bySeq.TryGetValue(seq, out station) ? station.StationName : null;
        }

        public int GetDistanceM(int fromSeq, int toSeq)
        {
            StationItem from;
            StationItem to;
            if (!bySeq.TryGetValue(fromSeq, out from))
            {
                throw new ArgumentException("上车站序不存在：" + fromSeq);
            }
            if (!bySeq.TryGetValue(toSeq, out to))
            {
                throw new ArgumentException("下车站序不存在：" + toSeq);
            }
            return Math.Abs(to.CumDistanceM - from.CumDistanceM);
        }

        /// <summary>
        /// 票价 = 起步价 + ceil(max(0, 里程 - 起步里程) / 每档里程) * 每档加价，超过封顶价时取封顶价。
        /// </summary>
        public decimal GetFare(int fromSeq, int toSeq)
        {
            if (fromSeq == toSeq)
            {
                return 0m;
            }

            int lo = Math.Min(fromSeq, toSeq);
            int hi = Math.Max(fromSeq, toSeq);
            Tuple<int, int> key = Tuple.Create(lo, hi);

            decimal fare;
            if (cache.TryGetValue(key, out fare))
            {
                return fare;
            }

            int distance = GetDistanceM(lo, hi);
            fare = Rule.BaseFare;

            if (distance > Rule.BaseDistanceM)
            {
                int extra = distance - Rule.BaseDistanceM;
                int steps = (int)Math.Ceiling(extra / (double)Rule.StepDistanceM);
                fare += steps * Rule.StepFare;
            }

            if (Rule.MaxFare.HasValue && fare > Rule.MaxFare.Value)
            {
                fare = Rule.MaxFare.Value;
            }

            cache[key] = fare;
            return fare;
        }

        /// <summary>
        /// 展开成完整的 OD 价目表，可直接绑定 DataGridView。
        /// </summary>
        public DataTable ToDataTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("上车站序", typeof(int));
            dt.Columns.Add("上车站", typeof(string));
            dt.Columns.Add("下车站序", typeof(int));
            dt.Columns.Add("下车站", typeof(string));
            dt.Columns.Add("里程(km)", typeof(decimal));
            dt.Columns.Add("票价(元)", typeof(decimal));
            dt.Columns.Add("计算式", typeof(string));

            foreach (StationItem from in Stations)
            {
                foreach (StationItem to in Stations)
                {
                    if (from.Seq >= to.Seq)
                    {
                        continue;
                    }
                    dt.Rows.Add(
                        from.Seq,
                        from.StationName,
                        to.Seq,
                        to.StationName,
                        Math.Round(GetDistanceM(from.Seq, to.Seq) / 1000m, 2),
                        GetFare(from.Seq, to.Seq),
                        GetFareExpression(from.Seq, to.Seq));
                }
            }
            return dt;
        }

        /// <summary>
        /// 返回某段行程的票价计算过程，例如：
        /// 2.00 + 向上取整((12.80 - 10.00) / 5.00) × 1.00 = 3.00
        /// </summary>
        public string GetFareExpression(int fromSeq, int toSeq)
        {
            if (fromSeq == toSeq)
            {
                return "0.00";
            }

            int distance = GetDistanceM(fromSeq, toSeq);
            decimal fare = GetFare(fromSeq, toSeq);

            if (distance <= Rule.BaseDistanceM)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:F2} = {1:F2}", Rule.BaseFare, fare);
            }

            int steps = (int)Math.Ceiling((distance - Rule.BaseDistanceM) / (double)Rule.StepDistanceM);
            decimal rawFare = Rule.BaseFare + steps * Rule.StepFare;

            string expression = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F2} + 向上取整(({1:F2} - {2:F2}) / {3:F2}) × {4:F2} = {5:F2}",
                Rule.BaseFare,
                distance / 1000m,
                Rule.BaseDistanceM / 1000m,
                Rule.StepDistanceM / 1000m,
                Rule.StepFare,
                fare);

            if (rawFare > fare)
            {
                expression += "（封顶）";
            }
            return expression;
        }

        /// <summary>
        /// 生成“99 乘法表”式的梯形矩阵：行是上车站，列是下车站，
        /// 只填 to &gt; from 的右上三角，单元格为票价。
        /// </summary>
        public DataTable ToMatrixTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("上车站", typeof(string));

            foreach (StationItem station in Stations)
            {
                dt.Columns.Add(
                    station.Seq.ToString(CultureInfo.InvariantCulture),
                    typeof(string));
            }

            foreach (StationItem from in Stations)
            {
                DataRow row = dt.NewRow();
                row[0] = from.Seq + " " + from.StationName;

                foreach (StationItem to in Stations)
                {
                    string column = to.Seq.ToString(CultureInfo.InvariantCulture);
                    row[column] = to.Seq > from.Seq
                        ? GetFare(from.Seq, to.Seq).ToString("F2", CultureInfo.InvariantCulture)
                        : "";
                }

                dt.Rows.Add(row);
            }

            return dt;
        }

        /// <summary>
        /// 把当前数据回写到 Data 目录下的 CSV，作为下次启动的本地缓存。
        /// 站点按累计里程写回，计价规则写回一行。
        /// </summary>
        public void SaveToCsv(string directory)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StringBuilder stationsCsv = new StringBuilder();
            stationsCsv.AppendLine("line_id,seq,station_name,cum_distance_m");

            foreach (StationItem station in Stations)
            {
                stationsCsv.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3}",
                    station.LineId == 0 ? 1 : station.LineId,
                    station.Seq,
                    EscapeCsv(station.StationName),
                    station.CumDistanceM));
            }

            WriteTextWithBom(Path.Combine(directory, "stations.csv"), stationsCsv.ToString());

            StringBuilder rulesCsv = new StringBuilder();
            rulesCsv.AppendLine("line_id,base_fare,base_distance_m,step_distance_m,step_fare,max_fare,effective_from,effective_to");
            rulesCsv.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1:F2},{2},{3},{4:F2},{5},{6},{7}",
                Stations[0].LineId == 0 ? 1 : Stations[0].LineId,
                Rule.BaseFare,
                Rule.BaseDistanceM,
                Rule.StepDistanceM,
                Rule.StepFare,
                Rule.MaxFare.HasValue
                    ? Rule.MaxFare.Value.ToString("F2", CultureInfo.InvariantCulture)
                    : "",
                Rule.EffectiveFrom ?? "",
                Rule.EffectiveTo ?? ""));

            WriteTextWithBom(Path.Combine(directory, "fare_rules.csv"), rulesCsv.ToString());
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private static void WriteTextWithBom(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(true));

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        /// <summary>
        /// 把当前计价规则拼成一行说明文字，用于显示在表格上方。
        /// </summary>
        public string GetRuleText()
        {
            string text = string.Format(
                CultureInfo.InvariantCulture,
                "计价规则：起步价 {0:F2} 元（含 {1:F2} km），超出后每 {2:F2} km 加 {3:F2} 元；" +
                "票价 = 起步价 + 向上取整((里程 - 起步里程) / 每档里程) × 每档加价",
                Rule.BaseFare,
                Rule.BaseDistanceM / 1000m,
                Rule.StepDistanceM / 1000m,
                Rule.StepFare);

            if (Rule.MaxFare.HasValue)
            {
                text += string.Format(CultureInfo.InvariantCulture, "；封顶 {0:F2} 元", Rule.MaxFare.Value);
            }
            if (!string.IsNullOrWhiteSpace(Rule.EffectiveFrom))
            {
                text += "；生效日期 " + Rule.EffectiveFrom;
            }
            return text;
        }

        private static List<Dictionary<string, string>> ReadCsv(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到 CSV 文件：" + path, path);
            }

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0)
            {
                throw new InvalidDataException("CSV 文件为空：" + path);
            }

            string[] header = SplitCsvLine(lines[0])
                .Select(h => h.Trim().TrimStart('\uFEFF'))
                .ToArray();

            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] cells = SplitCsvLine(lines[i]);
                Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Length; c++)
                {
                    row[header[c]] = c < cells.Length ? cells[c].Trim() : "";
                }
                rows.Add(row);
            }
            return rows;
        }

        private static string[] SplitCsvLine(string line)
        {
            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                }
                else if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                }
                else
                {
                    cell.Append(ch);
                }
            }

            cells.Add(cell.ToString());
            return cells.ToArray();
        }
    }
}
