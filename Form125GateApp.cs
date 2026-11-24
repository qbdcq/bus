using Interfaces;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace _125GateApp
{
    public partial class Form125GateApp : Form
    {
        public Form125GateApp()
        {
            InitializeComponent();
        }
        private bool isDoor = false;
        private decimal currentBalance = 0;
        private string currentCardNum = "";
        private int currentStationIndex = 0; // 当前站点索引（0-15）
        private bool isUpDirection = true; // true=上行(0→15)，false=下行(15→0)
        private bool isOdDownDirection = false; // OD 分析方向：false=上行，true=下行
        private PictureBox[] stationPictureBoxes; // 站点 PictureBox 数组
        private ArrowPictureBox[] arrowPictureBoxes; // 相邻站点之间的箭头（15 个）
        private ArrowDirection[] originalArrowDirections; // 每个箭头的原始朝向（设计器里的方向）
        private FareTable fareTable; // 梯形票价表
        private OdAnalysisRepository odRepository; // 乘客 OD 分析
        private CardRepository cardRepository; // 卡余额与行程

        private enum CardReadMode { None, Board, Alight }
        private CardReadMode currentReadMode = CardReadMode.None;
        private bool cardOperationInProgress;
        private int cardLookupVersion;
        private string baseWindowTitle = "";   // 标题栏基本标题
        private long dbLatencyCheckInProgress; // 0=空闲，1=探测中，防止上一轮还没完成下一轮又发
        private System.Windows.Forms.Timer dbLatencyTimer;

        private void Form125GateApp_Load(object sender, EventArgs e)
        {
            baseWindowTitle = this.Text;
            LoadFareTable();
            InitOdControls();

            string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            path = System.IO.Path.GetDirectoryName(path);

            stationPictureBoxes = new PictureBox[] { 
                pictureBox1, pictureBox2, pictureBox3, pictureBox4, pictureBox5, 
                pictureBox6, pictureBox7, pictureBox8, pictureBox9, pictureBox10, 
                pictureBox11, pictureBox12, pictureBox13, pictureBox14, pictureBox15, pictureBox16 
            };

            arrowPictureBoxes = new ArrowPictureBox[] {
                arrowPictureBox1, arrowPictureBox2, arrowPictureBox3, arrowPictureBox4, arrowPictureBox5,
                arrowPictureBox6, arrowPictureBox7, arrowPictureBox8, arrowPictureBox9, arrowPictureBox10,
                arrowPictureBox11, arrowPictureBox12, arrowPictureBox13, arrowPictureBox14, arrowPictureBox15
            };

            // 记录每个箭头在设计器里的原始朝向，换向时在此基础上反转
            originalArrowDirections = new ArrowDirection[arrowPictureBoxes.Length];
            for (int i = 0; i < arrowPictureBoxes.Length; i++)
            {
                originalArrowDirections[i] = arrowPictureBoxes[i].Direction;
            }

            // 初始状态：上行，所有站点红色，只有首发站绿色
            ResetTripState();

            JObject o = ComHelper.Readjson();
            Init_Com(o["COM"].ToString(), int.Parse(o["BAUD"].ToString()));

            // 每 3 秒探测一次数据库延迟并显示在标题栏
            dbLatencyTimer = new System.Windows.Forms.Timer();
            dbLatencyTimer.Interval = 3000;
            dbLatencyTimer.Tick += dbLatencyTimer_Tick;
            dbLatencyTimer.Start();
        }

        private void LoadFareTable()
        {
            try
            {
                string source;
                fareTable = LoadFareTableData(out source);

                dataGridView1.AutoGenerateColumns = true;
                dataGridView1.DataSource = fareTable.ToMatrixTable();
                dataGridView1.ReadOnly = true;
                dataGridView1.AllowUserToAddRows = false;
                dataGridView1.AllowUserToDeleteRows = false;
                dataGridView1.RowHeadersVisible = false;
                dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

                // 第一列固定为上车站，其余每列对应一个下车站
                dataGridView1.Columns[0].Width = 150;
                dataGridView1.Columns[0].Frozen = true;
                dataGridView1.Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

                for (int i = 1; i < dataGridView1.Columns.Count; i++)
                {
                    dataGridView1.Columns[i].Width = 74;
                    dataGridView1.Columns[i].HeaderText = fareTable.Stations[i - 1].StationName;
                    dataGridView1.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                }

                dataGridView1.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
                dataGridView1.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

                dataGridView1.CellToolTipTextNeeded -= dataGridView1_CellToolTipTextNeeded;
                dataGridView1.CellToolTipTextNeeded += dataGridView1_CellToolTipTextNeeded;

                lblFareFormula.Text = "[" + source + "] " + fareTable.GetRuleText()
                    + "（行=上车站，列=下车站，悬停单元格看计算式）";
            }
            catch (Exception ex)
            {
                lblFareFormula.Text = "梯形票价表加载失败：" + ex.Message;
            }
        }

        private FareTable LoadFareTableData(out string source)
        {
            string mode = ConfigurationManager.AppSettings["FareSource"] ?? "Csv";

            if (string.Equals(mode, "Postgres", StringComparison.OrdinalIgnoreCase))
            {
                string connStr = ConfigurationManager.AppSettings["FareDb"];
                if (!string.IsNullOrWhiteSpace(connStr) && !connStr.Contains("请填写"))
                {
                    try
                    {
                        FareTable table = FareTable.FromDatabase(connStr);
                        TryUpdateLocalCsv(table);
                        source = "本地 SQL(已同步)";
                        return table;
                    }
                    catch
                    {
                        source = "CSV（PostgreSQL 连接失败，已回退）";
                        return FareTable.LoadDefault();
                    }
                }

                source = "CSV（未配置数据库密码）";
                return FareTable.LoadDefault();
            }

            source = "CSV";
            return FareTable.LoadDefault();
        }

        private void TryUpdateLocalCsv(FareTable table)
        {
            // 运行目录缓存
            SaveCsvQuietly(table, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"));

            // 开发环境下同时更新源码目录（bin\Debug\..\..\Data），方便直接查看
            try
            {
                string sourceDir = Path.GetFullPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\Data"));

                if (File.Exists(Path.Combine(sourceDir, "stations.csv")))
                {
                    SaveCsvQuietly(table, sourceDir);
                }
            }
            catch
            {
            }
        }

        private void SaveCsvQuietly(FareTable table, string directory)
        {
            try
            {
                table.SaveToCsv(directory);
            }
            catch
            {
                // 缓存写入失败不影响本次使用
            }
        }

        private void InitOdControls()
        {
            comboBox1.Items.Clear();
            comboBox1.Items.AddRange(new object[]
            {
                "最近 30 分钟", "最近 1 小时", "今天", "近 7 天", "全部"
            });
            comboBox1.SelectedIndex = 0;

            button4.Click -= button4_Click;
            button4.Click += button4_Click;

            button7.Click -= button7_Click;
            button7.Click += button7_Click;

            button8.Text = "换向";
            button8.Click -= button8_Click;
            button8.Click += button8_Click;
            UpdateOdDirectionLabel();

            string connStr = ConfigurationManager.AppSettings["FareDb"];
            if (!string.IsNullOrWhiteSpace(connStr) && !connStr.Contains("请填写"))
            {
                odRepository = new OdAnalysisRepository(connStr);
                cardRepository = new CardRepository(connStr);
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            RefreshOdAnalysis(true);
        }

        private void button8_Click(object sender, EventArgs e)
        {
            isOdDownDirection = !isOdDownDirection;
            UpdateOdDirectionLabel();
            RefreshOdAnalysis(false);
        }

        private void UpdateOdDirectionLabel()
        {
            if (fareTable == null || fareTable.Stations == null || fareTable.Stations.Count < 2)
            {
                label27.Text = "...";
                return;
            }

            string firstName = fareTable.Stations[0].StationName;
            string lastName = fareTable.Stations[fareTable.Stations.Count - 1].StationName;

            label27.Text = isOdDownDirection
                ? lastName + " → " + firstName
                : firstName + " → " + lastName;
        }

        private async void RefreshOdAnalysis(bool showError)
        {
            if (odRepository == null)
            {
                if (showError)
                {
                    MessageBox.Show("未配置数据库连接，无法进行 OD 分析。", "提示");
                }
                return;
            }

            DateTime now = DateTime.Now;
            DateTime from;
            DateTime to;

            // 区间从左到右依次包含：30 分钟 ⊂ 1 小时 ⊂ 今天 ⊂ 近 7 天 ⊂ 全部
            switch (comboBox1.SelectedIndex)
            {
                case 0: // 最近 30 分钟
                    from = now.AddMinutes(-30);
                    to = now;
                    break;
                case 1: // 最近 1 小时
                    from = now.AddHours(-1);
                    to = now;
                    break;
                case 3: // 近 7 天
                    from = DateTime.Today.AddDays(-6);
                    to = DateTime.Today.AddDays(1);
                    break;
                case 4: // 全部
                    from = new DateTime(2000, 1, 1);
                    to = DateTime.Today.AddDays(1);
                    break;
                default: // 今天
                    from = DateTime.Today;
                    to = DateTime.Today.AddDays(1);
                    break;
            }

            button4.Enabled = false;
            button4.Text = "加载中...";
            bool downDirection = isOdDownDirection;
            try
            {
                var result = await Task.Run(() =>
                {
                    DataTable flow = odRepository.GetFlowByTime(from, to, downDirection);
                    DataTable segments = odRepository.GetFlowBySegment(from, to, downDirection);
                    int hourlyPeak = odRepository.GetHourlyPeak();
                    int segmentPeak = odRepository.GetSegmentPeak();
                    return Tuple.Create(flow, segments, hourlyPeak, segmentPeak);
                });

                BindChart(chart1, result.Item1, "时段", "乘次", SeriesChartType.Column, "分时客流", result.Item3, null);
                BindChart(chart2, result.Item2, "全称", "人次", SeriesChartType.Bar, "断面客流", result.Item4, null);
            }
            catch (Exception ex)
            {
                if (showError)
                {
                    MessageBox.Show("OD 分析数据加载失败：" + ex.Message, "提示");
                }
            }
            finally
            {
                button4.Enabled = true;
                button4.Text = "查询";
            }
        }

        private void BindChart(Chart chart, DataTable table, string categoryColumn, string valueColumn,
                               SeriesChartType chartType, string title, int fixedMax, string tooltipColumn)
        {
            chart.Series[0].Points.Clear();
            chart.Series[0].ChartType = chartType;
            chart.Series[0].Name = title;
            chart.Series[0].IsValueShownAsLabel = false;

            if (chart.Legends.Count > 0)
            {
                chart.Legends[0].Enabled = true;
            }

            // 用全量峰值固定数值轴；个别区间聚合后超过峰值时，至少保证不截断
            int maxValue = 0;
            foreach (DataRow row in table.Rows)
            {
                maxValue = Math.Max(maxValue, Convert.ToInt32(row[valueColumn]));
            }

            double baseMax = Math.Max(fixedMax, maxValue);
            double axisMax = baseMax <= 0 ? 1 : Math.Ceiling(baseMax * 1.15);
            ChartArea area = chart.ChartAreas[0];

            if (chartType == SeriesChartType.Bar)
            {
                // MS Chart 的 Bar 类型：分类轴是 AxisX（垂直），数值轴是 AxisY（水平）
                area.AxisX.Interval = 1;
                area.AxisX.IsReversed = false;
                area.AxisX.LabelStyle.IsStaggered = false;
                area.AxisY.Minimum = 0;
                area.AxisY.Maximum = axisMax;
                area.AxisY.Interval = Math.Max(1, Math.Ceiling(axisMax / 5));
            }
            else
            {
                // 柱状图：分类在 X 轴，数值在 Y 轴；每小时一个刻度
                area.AxisY.Minimum = 0;
                area.AxisY.Maximum = axisMax;
                area.AxisY.Interval = Math.Max(1, Math.Ceiling(axisMax / 5));
                area.AxisX.Interval = 1;
                area.AxisX.LabelStyle.Angle = -90;
                area.AxisX.LabelStyle.Font = new Font("宋体", 8f);
                area.AxisX.IsLabelAutoFit = false;
                area.AxisX.Title = "小时";
            }

            int total = 0;
            foreach (DataRow row in table.Rows)
            {
                int value = Convert.ToInt32(row[valueColumn]);
                total += value;

                int pointIndex = chart.Series[0].Points.AddXY(row[categoryColumn].ToString(), value);

                // 非零柱子在末端显示数值（柱状图在顶部，条形图在右端）
                if (value > 0
                    && (chartType == SeriesChartType.Column || chartType == SeriesChartType.Bar))
                {
                    chart.Series[0].Points[pointIndex].Label = value.ToString();
                }

                if (!string.IsNullOrEmpty(tooltipColumn) && table.Columns.Contains(tooltipColumn))
                {
                    chart.Series[0].Points[pointIndex].ToolTip =
                        row[tooltipColumn] + "：" + value + " 人次";
                }
            }

            chart.Titles.Clear();
            chart.Titles.Add(total == 0
                ? title + "（暂无数据，请先完成几笔刷卡）"
                : title);
        }

        private async void SettleOpenTripsAtLastStation()
        {
            if (cardRepository == null || fareTable == null || fareTable.Stations.Count == 0)
            {
                return;
            }

            int terminusSeq = isUpDirection
                ? fareTable.Stations[fareTable.Stations.Count - 1].Seq
                : fareTable.Stations[0].Seq;

            AppendRideLog("终点站结算处理中...");
            try
            {
                int count = await Task.Run(() => cardRepository.SettleOpenTripsAtLastStation(terminusSeq, DateTime.Now));
                if (count > 0)
                {
                    textBox1.AppendText("已到达终点站 " + GetStationName(currentStationIndex)
                        + "，自动结算 " + count + " 名未刷卡下车乘客（按全程票价）"
                        + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                textBox1.AppendText("终点站结算失败：" + ex.Message + Environment.NewLine);
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            if (stationPictureBoxes == null || arrowPictureBoxes == null)
            {
                return;
            }

            // 首发站和终点站都可以换向
            bool atOrigin = currentStationIndex == 0;
            bool atTerminus = currentStationIndex == stationPictureBoxes.Length - 1;

            if (!atOrigin && !atTerminus)
            {
                textBox1.AppendText("换向只能在首发站或终点站操作" + Environment.NewLine);
                return;
            }

            isUpDirection = !isUpDirection;
            ResetTripState();

            textBox1.AppendText("已换向：" + (isUpDirection ? "上行" : "下行")
                + "，起点 " + GetStationName(currentStationIndex) + Environment.NewLine);
        }

        private decimal GetMaxFare()
        {
            if (fareTable == null || fareTable.Stations == null || fareTable.Stations.Count < 2)
            {
                return 0m;
            }

            int firstSeq = fareTable.Stations[0].Seq;
            int lastSeq = fareTable.Stations[fareTable.Stations.Count - 1].Seq;
            return fareTable.GetFare(firstSeq, lastSeq);
        }

        /// <summary>
        /// 返回卡属性的折扣说明，用于刷卡日志；普通卡/未设置属性返回空串。
        /// </summary>
        private string GetPropertyDiscountText(string cardProperty)
        {
            if (string.IsNullOrEmpty(cardProperty)
                || fareTable == null || fareTable.Rule == null)
            {
                return "";
            }

            decimal multiplier = fareTable.Rule.GetDiscount(cardProperty);
            if (multiplier >= 1.00m)
            {
                return "";
            }
            if (multiplier <= 0m)
            {
                return "，" + cardProperty + "（免费）";
            }
            return "，" + cardProperty + "（" + (multiplier * 10m).ToString("0.##") + " 折）";
        }

        private void dataGridView1_CellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (fareTable == null || e.RowIndex < 0 || e.ColumnIndex <= 0)
            {
                return;
            }
            if (e.RowIndex >= fareTable.Stations.Count || e.ColumnIndex - 1 >= fareTable.Stations.Count)
            {
                return;
            }

            int fromSeq = fareTable.Stations[e.RowIndex].Seq;
            int toSeq = fareTable.Stations[e.ColumnIndex - 1].Seq;

            if (toSeq > fromSeq)
            {
                e.ToolTipText = fareTable.GetFareExpression(fromSeq, toSeq);
            }
        }

        public void Init_Com(string name, int baud)
        {

            if (ComHelper.openPort(name, baud))
            {
                //MessageBox.Show("串口打开成功");
                ComHelper.sp.DataReceived += new SerialDataReceivedEventHandler(sp_DataReceived);
            }
        }

        private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                Thread.Sleep(50);
                int len = ComHelper.sp.BytesToRead;
                if (len > 0)
                {
                    byte[] recbuf = new byte[len];
                    ComHelper.sp.Read(recbuf, 0, len);
                    List<byte[]> list = Protocol.bytesHandle(recbuf, len);
                    foreach (byte[] buf in list)
                    {
                        ProtocolModel p = Protocol.byteToProtocolModel(buf, buf.Length);
                        if (p != null)
                        {
                            if (p.OrderCode[0] == Protocol.SingleCard_ACK[0] && p.OrderCode[1] == Protocol.SingleCard_ACK[1])
                            {
                                updateValues(p);
                            }
                            updateMsgsBox(Protocol.byteToHexString(buf));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendRideLog("刷卡处理异常：" + ex.Message);
            }
        }


        delegate void upateValue(ProtocolModel p);
        delegate void updateMsgBox(string hexbuf);
        
        private void AppendRideLog(string text)
        {
            if (textBox1.InvokeRequired)
            {
                textBox1.BeginInvoke(new Action(delegate
                {
                    textBox1.AppendText(text + Environment.NewLine);
                }));
            }
            else
            {
                textBox1.AppendText(text + Environment.NewLine);
            }
        }

        // 每 5 秒主动探测一次数据库延迟并刷新标题栏
        private void dbLatencyTimer_Tick(object sender, EventArgs e)
        {
            if (IsDisposed || cardRepository == null
                || Interlocked.Exchange(ref dbLatencyCheckInProgress, 1) != 0)
            {
                return;
            }

            CardRepository repo = cardRepository;
            Task.Run(() =>
            {
                long ms = -1;
                try
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    repo.Ping();
                    ms = sw.ElapsedMilliseconds;
                }
                catch
                {
                    ms = -1;
                }

                string newText = ms >= 0
                    ? baseWindowTitle + " " + ms + "ms"
                    : baseWindowTitle + "　[DB 连接失败]";
                BeginInvoke(new Action(delegate
                {
                    if (!IsDisposed)
                    {
                        Text = newText;
                    }
                }));
                Interlocked.Exchange(ref dbLatencyCheckInProgress, 0);
            });
        }
        
        private void updateMsgsBox(string hexbuf)
        {
            tbMsg.Invoke(new updateMsgBox((msg) =>
            {
                if (tbMsg.Lines.GetUpperBound(0) >= 200)
                    tbMsg.Text = "";
                tbMsg.AppendText(msg + "\n");
            }), hexbuf);
        }

        private void updateValues(ProtocolModel p)
        {
            panel1.Invoke(new upateValue((msg) =>
            {
                byte[] content = msg.Content;

                if (p.OrderCode[0] == Protocol.SingleCard_ACK[0] && p.OrderCode[1] == Protocol.SingleCard_ACK[1])
                {
                    if (isDoor)
                    {
                        currentCardNum = Protocol.BytesToString(content);
                        lbDoorCard.Text = currentCardNum;
                        label9.Text = "查询中...";
                        label26.Text = "查询中...";
                        LoadCardInfoAsync(currentCardNum, Interlocked.Increment(ref cardLookupVersion));
                        return;
                    }

                    if (isDoor == false)
                    {
                        // tabPage4 刷卡乘车页面
                        string cardNum = Protocol.BytesToString(content);

                        if (cardRepository == null)
                        {
                            textBox1.AppendText("卡号：" + cardNum + " - 数据库未配置，无法刷卡" + Environment.NewLine);
                            currentReadMode = CardReadMode.None;
                            return;
                        }

                        // 数据库请求在后台线程执行，串口和界面线程无需等待 Supabase 返回。
                        CardReadMode readMode = currentReadMode;
                        if (readMode == CardReadMode.Board || readMode == CardReadMode.Alight)
                        {
                            if (cardOperationInProgress)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 上一笔刷卡仍在处理中" + Environment.NewLine);
                                return;
                            }

                            currentReadMode = CardReadMode.None;
                            cardOperationInProgress = true;
                            textBox1.AppendText("卡号：" + cardNum + " - 处理中..." + Environment.NewLine);
                            ProcessRideCardAsync(cardNum, readMode, currentStationIndex);
                            return;
                        }

                        if (currentReadMode == CardReadMode.Board)
                        {
                            // 上车：预扣全程票价（按里程计价），并按卡属性折算折扣
                            if (fareTable == null || GetMaxFare() <= 0)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 票价表未加载，无法上车" + Environment.NewLine);
                                currentReadMode = CardReadMode.None;
                                return;
                            }

                            string cardProperty;
                            decimal preDeduct;
                            decimal? forgottenFare;
                            StartTripResult result;

                            try
                            {
                                cardProperty = cardRepository.GetCardProperty(cardNum);
                                preDeduct = fareTable != null
                                    ? fareTable.ApplyDiscount(GetMaxFare(), cardProperty)
                                    : GetMaxFare();

                                // 上次忘记下车：按全程票价结算
                                forgottenFare = cardRepository.CloseForgottenTrip(cardNum);

                                result = cardRepository.StartTrip(
                                    cardNum, currentStationIndex + 1, DateTime.Now, preDeduct);
                            }
                            catch (Exception ex)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 上车失败："
                                    + ex.Message + Environment.NewLine);
                                currentReadMode = CardReadMode.None;
                                return;
                            }

                            if (forgottenFare.HasValue)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 上次未刷卡下车，已按全程票价 "
                                    + forgottenFare.Value.ToString("F2") + " 元结算" + Environment.NewLine);
                            }

                            if (result.Status == StartTripStatus.CardNotFound)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 该卡未注册，请先充值" + Environment.NewLine);
                            }
                            else if (result.Status == StartTripStatus.InsufficientBalance)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 余额不足，无法上车（需全程票价 "
                                    + preDeduct.ToString("F2") + " 元，当前"
                                    + result.Balance.ToString("F2") + "元）" + Environment.NewLine);
                            }
                            else if (result.Status == StartTripStatus.AlreadyOnBoard)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 您已在车上，请先刷下车卡" + Environment.NewLine);
                            }
                            else
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 上车成功（站点："
                                    + GetStationName(currentStationIndex) + "，预扣全程票价 "
                                    + preDeduct.ToString("F2") + " 元"
                                    + GetPropertyDiscountText(cardProperty)
                                    + "，余额 "
                                    + result.Balance.ToString("F2") + " 元）" + Environment.NewLine);
                            }
                        }
                        else if (currentReadMode == CardReadMode.Alight)
                        {
                            // 下车：按上车站到本站的里程计价，退还差额
                            OpenTripInfo openTrip = cardRepository.GetOpenTrip(cardNum);
                            if (openTrip == null)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 未上车！请先刷卡上车" + Environment.NewLine);
                                currentReadMode = CardReadMode.None;
                                return;
                            }

                            int alightSeq = currentStationIndex + 1;
                            string cardProperty = cardRepository.GetCardProperty(cardNum);
                            int distanceM = fareTable != null
                                ? fareTable.GetDistanceM(openTrip.BoardSeq, alightSeq)
                                : 0;
                            decimal actualFare = fareTable != null
                                ? fareTable.GetFare(openTrip.BoardSeq, alightSeq, cardProperty)
                                : 0m;

                            EndTripResult endResult = cardRepository.EndTrip(
                                cardNum, alightSeq, DateTime.Now, distanceM, actualFare);

                            if (endResult.Status != EndTripStatus.Success)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 下车失败，请重试" + Environment.NewLine);
                            }
                            else
                            {
                                int stations = Math.Abs(alightSeq - openTrip.BoardSeq);
                                textBox1.AppendText("卡号：" + cardNum + " - 下车成功（"
                                    + GetStationName(openTrip.BoardSeq - 1) + " → "
                                    + GetStationName(currentStationIndex) + "，" + stations + " 站，"
                                    + (distanceM / 1000.0).ToString("F2") + " km，实扣 "
                                    + actualFare.ToString("F2") + " 元"
                                    + GetPropertyDiscountText(cardProperty)
                                    + "，退还 "
                                    + endResult.Refund.ToString("F2") + " 元，余额 "
                                    + endResult.Balance.ToString("F2") + " 元）" + Environment.NewLine);
                            }
                        }

                        currentReadMode = CardReadMode.None;
                    }
                    else
                    {
                        currentCardNum = Protocol.BytesToString(content);
                        lbDoorCard.Text = currentCardNum;

                        decimal? balance = cardRepository != null
                            ? cardRepository.GetBalance(currentCardNum) : null;

                        if (balance.HasValue)
                        {
                            currentBalance = balance.Value;
                            label9.Text = currentBalance.ToString("F2") + "元";
                        }
                        else
                        {
                            label9.Text = "该卡未注册，请先充值";
                        }

                        // 刷新卡属性显示，并将下拉框预选到当前属性，避免误改
                        label26.Text = "...";
                        if (cardRepository != null)
                        {
                            string property = cardRepository.GetCardProperty(currentCardNum);
                            label26.Text = string.IsNullOrEmpty(property) ? "未设置" : property;

                            int index = comboBox2.Items.IndexOf(property);
                            comboBox2.SelectedIndex = index >= 0 ? index : 0;
                        }
                    }
                }
            }), p);
        }

        private async void LoadCardInfoAsync(string cardNo, int version)
        {
            try
            {
                var info = await Task.Run(() => Tuple.Create(
                    cardRepository.GetBalance(cardNo),
                    cardRepository.GetCardProperty(cardNo)));

                if (IsDisposed || version != Volatile.Read(ref cardLookupVersion) || cardNo != currentCardNum)
                    return;

                if (info.Item1.HasValue)
                {
                    currentBalance = info.Item1.Value;
                    label9.Text = currentBalance.ToString("F2") + "元";
                }
                else
                {
                    label9.Text = "该卡未注册，请先充值";
                }

                label26.Text = string.IsNullOrEmpty(info.Item2) ? "未设置" : info.Item2;
                int index = comboBox2.Items.IndexOf(info.Item2);
                comboBox2.SelectedIndex = index >= 0 ? index : 0;
            }
            catch (Exception ex)
            {
                if (!IsDisposed && version == Volatile.Read(ref cardLookupVersion))
                {
                    label9.Text = "查询失败";
                    label26.Text = "查询失败";
                    AppendRideLog("读卡信息查询失败：" + ex.Message);
                }
            }
        }

        private async void ProcessRideCardAsync(string cardNo, CardReadMode mode, int stationIndex)
        {
            try
            {
                if (mode == CardReadMode.Board)
                {
                    if (fareTable == null || GetMaxFare() <= 0)
                    {
                        AppendRideLog("卡号：" + cardNo + " - 票价表未加载，无法上车");
                        return;
                    }

                    decimal maxFare = GetMaxFare();
                    var result = await Task.Run(() =>
                    {
                        string property = cardRepository.GetCardProperty(cardNo);
                        decimal preDeduct = fareTable.ApplyDiscount(maxFare, property);
                        decimal? forgotten = cardRepository.CloseForgottenTrip(cardNo);
                        StartTripResult start = cardRepository.StartTrip(cardNo, stationIndex + 1, DateTime.Now, preDeduct);
                        return Tuple.Create(property, preDeduct, forgotten, start);
                    });

                    if (result.Item3.HasValue)
                        AppendRideLog("卡号：" + cardNo + " - 上次未刷卡下车，已按全程票价 " + result.Item3.Value.ToString("F2") + " 元结算");

                    if (result.Item4.Status == StartTripStatus.Success)
                        AppendRideLog("卡号：" + cardNo + " - 上车成功，余额 " + result.Item4.Balance.ToString("F2") + " 元");
                    else if (result.Item4.Status == StartTripStatus.CardNotFound)
                        AppendRideLog("卡号：" + cardNo + " - 该卡未注册，请先充值");
                    else if (result.Item4.Status == StartTripStatus.InsufficientBalance)
                        AppendRideLog("卡号：" + cardNo + " - 余额不足，无法上车");
                    else
                        AppendRideLog("卡号：" + cardNo + " - 您已在车上，请先刷下车卡");
                }
                else
                {
                    var result = await Task.Run(() =>
                    {
                        OpenTripInfo openTrip = cardRepository.GetOpenTrip(cardNo);
                        if (openTrip == null) return Tuple.Create<OpenTripInfo, string, int, decimal, EndTripResult>(null, null, 0, 0m, null);
                        string property = cardRepository.GetCardProperty(cardNo);
                        int alightSeq = stationIndex + 1;
                        int distance = fareTable.GetDistanceM(openTrip.BoardSeq, alightSeq);
                        decimal fare = fareTable.GetFare(openTrip.BoardSeq, alightSeq, property);
                        EndTripResult end = cardRepository.EndTrip(cardNo, alightSeq, DateTime.Now, distance, fare);
                        return Tuple.Create(openTrip, property, distance, fare, end);
                    });

                    if (result.Item1 == null)
                        AppendRideLog("卡号：" + cardNo + " - 未上车，请先刷卡上车");
                    else if (result.Item5.Status != EndTripStatus.Success)
                        AppendRideLog("卡号：" + cardNo + " - 下车失败，请重试");
                    else
                        AppendRideLog("卡号：" + cardNo + " - 下车成功，实扣 " + result.Item4.ToString("F2") + " 元，退还 " + result.Item5.Refund.ToString("F2") + " 元，余额 " + result.Item5.Balance.ToString("F2") + " 元");
                }
            }
            catch (Exception ex)
            {
                AppendRideLog("卡号：" + cardNo + " - 数据库操作失败：" + ex.Message);
            }
            finally
            {
                cardOperationInProgress = false;
            }
        }

        private string GetStationName(int index)
        {
            string[] stations = {
                "天洋城总站", "天洋城", "燕郊富地广场", "紫竹园小区",
                "燕郊中美医院", "华北科技学院北门", "中赵甫村", "防灾科技学院",
                "交通干部管理学院", "潮白人家小区", "欧逸丽庭", "通州白庙村",
                "白庙新村", "北京胸科医院", "北京物资学院南站", "大北窑南"
            };
            if (index >= 0 && index < stations.Length)
                return stations[index];
            return "未知站点";
        }

        private void tabControl2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl2.SelectedIndex == 0)
                isDoor = false;
            else
                isDoor = true;

            if (tabControl2.SelectedTab == tabPage2)
            {
                RefreshOdAnalysis(false);
            }
        }
        
        private async void btnZC_Click(object sender, EventArgs e)
        {
            lbZCState.Text = "....";

            if (cardRepository == null)
            {
                lbZCState.Text = "数据库未配置，无法充值";
                return;
            }

            if (lbDoorCard.Text.Equals("...") || lbDoorCard.Text.Equals(""))
            {
                lbZCState.Text = "请先获取卡号";
                return;
            }

            decimal rechargeAmount;
            if (tbNum.Text == "" || !decimal.TryParse(tbNum.Text, out rechargeAmount) || rechargeAmount <= 0)
            {
                lbZCState.Text = "请输入有效金额";
                return;
            }

            string cardNo = lbDoorCard.Text;
            btnZC.Enabled = false;
            lbZCState.Text = "充值处理中...";
            try
            {
                decimal oldBalance = await Task.Run(() => cardRepository.GetBalance(cardNo) ?? 0m);
                decimal newBalance = await Task.Run(() => cardRepository.Recharge(cardNo, rechargeAmount));

                currentBalance = newBalance;
                label9.Text = newBalance.ToString("F2") + "元";
                lbZCState.Text = "充值成功（原：" + oldBalance.ToString("F2") + "元，充："
                    + rechargeAmount.ToString("F2") + "元，现：" + newBalance.ToString("F2") + "元）";
            }
            catch (Exception ex)
            {
                lbZCState.Text = "充值失败：" + ex.Message;
            }
            finally
            {
                btnZC.Enabled = true;
            }
        }

        private async void button5_Click(object sender, EventArgs e)
        {
            lbZCState.Text = "....";

            if (cardRepository == null)
            {
                lbZCState.Text = "数据库未配置，无法保存属性";
                return;
            }

            if (lbDoorCard.Text.Equals("...") || lbDoorCard.Text.Equals(""))
            {
                lbZCState.Text = "请先获取卡号";
                return;
            }

            string selectedProperty = comboBox2.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedProperty))
            {
                lbZCState.Text = "请选择卡属性";
                return;
            }

            string cardNo = lbDoorCard.Text;
            button5.Enabled = false;
            lbZCState.Text = "属性保存中...";
            try
            {
                if (await Task.Run(() => cardRepository.UpdateCardProperty(cardNo, selectedProperty)))
                {
                    label26.Text = selectedProperty;
                    lbZCState.Text = "属性保存成功：" + selectedProperty;
                }
                else
                {
                    lbZCState.Text = "该卡未注册，请先充值";
                }
            }
            catch (Exception ex)
            {
                lbZCState.Text = "属性保存失败：" + ex.Message;
            }
            finally
            {
                button5.Enabled = true;
            }
        }

        private void llSendClear_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            tbSend.Text = "";
        }

        private void llRecClear_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            tbMsg.Text = "";
        }

        private void llDoorGetCard_Click(object sender, EventArgs e)
        {
            lbDoorCard.Text = "...";
            label9.Text = "...";
            Random r = new Random();
            byte comTpye = ConsumeType.getType(ConsumeType.RS485, ConsumeType.WIRED);
            byte order = Protocol.getOrder(true, false, false, 2, false);
            byte[] local = new byte[4];
            byte[] target = { 0x00, 0x00, 0x03, 0x01 };
            byte[] content = { 0x00 };
            byte[] buf = Protocol.sendMsg(comTpye, order, (byte)r.Next(100), local, target, Protocol.SingleCard_REQ, content);
            tbSend.AppendText(Protocol.byteToHexString(buf) + "\n");
            ComHelper.sp.Write(buf, 0, buf.Length);
        }

        private void llZCGetCard_Click(object sender, EventArgs e)
        {
            lbDoorCard.Text = "...";
            label9.Text = "...";
            Random r = new Random();
            byte comTpye = ConsumeType.getType(ConsumeType.RS485, ConsumeType.WIRED);
            byte order = Protocol.getOrder(true, false, false, 2, false);
            byte[] local = new byte[4];
            byte[] target = { 0x00, 0x00, 0x03, 0x01 };
            byte[] content = { 0x00 };
            byte[] buf = Protocol.sendMsg(comTpye, order, (byte)r.Next(100), local, target, Protocol.SingleCard_REQ, content);
            tbSend.AppendText(Protocol.byteToHexString(buf) + "\n");
            ComHelper.sp.Write(buf, 0, buf.Length);
        }

        private void button3_Click(object sender, EventArgs e)
        {
            int stationCount = stationPictureBoxes.Length;

            // 按当前方向移动：上行 +1，下行 -1，到端点后循环
            if (isUpDirection)
            {
                currentStationIndex = (currentStationIndex + 1) % stationCount;
            }
            else
            {
                currentStationIndex = (currentStationIndex - 1 + stationCount) % stationCount;
            }

            // 到达当前方向的终点站：结算所有未刷卡下车的乘客
            if (IsAtTerminus())
            {
                SettleOpenTripsAtLastStation();
            }

            RefreshStationHighlight();
        }

        /// <summary>
        /// 当前方向的终点站索引：上行 15，下行 0。
        /// </summary>
        private int GetTerminusIndex()
        {
            return isUpDirection ? stationPictureBoxes.Length - 1 : 0;
        }

        private bool IsAtTerminus()
        {
            return stationPictureBoxes != null
                && currentStationIndex == GetTerminusIndex();
        }

        /// <summary>
        /// 按当前方向刷新站点和箭头颜色：走过的变绿，未走的恢复红色。
        /// </summary>
        private void RefreshStationHighlight()
        {
            for (int i = 0; i < stationPictureBoxes.Length; i++)
            {
                bool passed = isUpDirection ? i <= currentStationIndex : i >= currentStationIndex;
                stationPictureBoxes[i].BackColor = passed ? Color.Lime : Color.Red;
            }

            for (int i = 0; i < arrowPictureBoxes.Length; i++)
            {
                // 第 i 个箭头连接第 i 站与第 i+1 站
                bool passed = isUpDirection ? i < currentStationIndex : i >= currentStationIndex;
                arrowPictureBoxes[i].ArrowColor = passed ? Color.Lime : Color.Red;
            }
        }

        /// <summary>
        /// 重置为一段新行程的初始状态：所有站点红色，只有当前方向的起点为绿色，箭头按方向复位。
        /// </summary>
        private void ResetTripState()
        {
            if (stationPictureBoxes == null || arrowPictureBoxes == null)
            {
                return;
            }

            foreach (PictureBox station in stationPictureBoxes)
            {
                station.BackColor = Color.Red;
            }

            for (int i = 0; i < arrowPictureBoxes.Length; i++)
            {
                arrowPictureBoxes[i].ArrowColor = Color.Red;

                ArrowDirection original = (originalArrowDirections != null && i < originalArrowDirections.Length)
                    ? originalArrowDirections[i]
                    : ArrowDirection.Right;

                arrowPictureBoxes[i].Direction = isUpDirection
                    ? original
                    : ReverseDirection(original);
            }

            currentStationIndex = isUpDirection ? 0 : stationPictureBoxes.Length - 1;
            stationPictureBoxes[currentStationIndex].BackColor = Color.Lime;
        }

        /// <summary>
        /// 箭头反向：左右互换、上下互换。
        /// </summary>
        private static ArrowDirection ReverseDirection(ArrowDirection direction)
        {
            switch (direction)
            {
                case ArrowDirection.Right: return ArrowDirection.Left;
                case ArrowDirection.Left: return ArrowDirection.Right;
                case ArrowDirection.Up: return ArrowDirection.Down;
                case ArrowDirection.Down: return ArrowDirection.Up;
                default: return direction;
            }
        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void label21_Click(object sender, EventArgs e)
        {

        }

        private void label22_Click(object sender, EventArgs e)
        {

        }

        private void pictureBox20_Click(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            // 下车按钮
            currentReadMode = CardReadMode.Alight;
            Random r = new Random();
            byte comTpye = ConsumeType.getType(ConsumeType.RS485, ConsumeType.WIRED);
            byte order = Protocol.getOrder(true, false, false, 2, false);
            byte[] local = new byte[4];
            byte[] target = { 0x00, 0x00, 0x03, 0x01 };
            byte[] content = { 0x00 };
            byte[] buf = Protocol.sendMsg(comTpye, order, (byte)r.Next(100), local, target, Protocol.SingleCard_REQ, content);
            tbSend.AppendText(Protocol.byteToHexString(buf) + "\n");
            ComHelper.sp.Write(buf, 0, buf.Length);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            // 上车按钮
            currentReadMode = CardReadMode.Board;
            Random r = new Random();
            byte comTpye = ConsumeType.getType(ConsumeType.RS485, ConsumeType.WIRED);
            byte order = Protocol.getOrder(true, false, false, 2, false);
            byte[] local = new byte[4];
            byte[] target = { 0x00, 0x00, 0x03, 0x01 };
            byte[] content = { 0x00 };
            byte[] buf = Protocol.sendMsg(comTpye, order, (byte)r.Next(100), local, target, Protocol.SingleCard_REQ, content);
            tbSend.AppendText(Protocol.byteToHexString(buf) + "\n");
            ComHelper.sp.Write(buf, 0, buf.Length);
        }
    }
}