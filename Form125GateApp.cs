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
        private PictureBox[] stationPictureBoxes; // 站点 PictureBox 数组
        private ArrowPictureBox[] arrowPictureBoxes; // 相邻站点之间的箭头（15 个）
        private FareTable fareTable; // 梯形票价表
        private OdAnalysisRepository odRepository; // 乘客 OD 分析
        private CardRepository cardRepository; // 卡余额与行程

        private enum CardReadMode { None, Board, Alight }
        private CardReadMode currentReadMode = CardReadMode.None;

        private void Form125GateApp_Load(object sender, EventArgs e)
        {
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

            JObject o = ComHelper.Readjson();
            Init_Com(o["COM"].ToString(), int.Parse(o["BAUD"].ToString()));
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

        private void RefreshOdAnalysis(bool showError)
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

            try
            {
                DataTable flow = odRepository.GetFlowByTime(from, to);
                DataTable segments = odRepository.GetFlowBySegment(from, to);

                // 峰值取自全量数据，保证切换区间时坐标轴刻度不变
                int hourlyPeak = odRepository.GetHourlyPeak();
                int segmentPeak = odRepository.GetSegmentPeak();

                BindChart(chart1, flow, "时段", "乘次", SeriesChartType.Column, "分时客流", hourlyPeak, null);
                BindChart(chart2, segments, "全称", "人次", SeriesChartType.Bar, "断面客流", segmentPeak, null);
            }
            catch (Exception ex)
            {
                if (showError)
                {
                    MessageBox.Show("OD 分析数据加载失败：" + ex.Message, "提示");
                }
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

                // 零值不显示数字，避免 24 个小时的 "0" 挤在基线处
                if (chartType == SeriesChartType.Column && value > 0)
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
            catch
            { }
        }


        delegate void upateValue(ProtocolModel p);
        delegate void updateMsgBox(string hexbuf);
        
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

                        if (currentReadMode == CardReadMode.Board)
                        {
                            // 上车：预扣全程票价（按里程计价）
                            decimal preDeduct = GetMaxFare();
                            if (preDeduct <= 0)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 票价表未加载，无法上车" + Environment.NewLine);
                                currentReadMode = CardReadMode.None;
                                return;
                            }

                            // 上次忘记下车：按全程票价结算
                            decimal? forgottenFare = cardRepository.CloseForgottenTrip(cardNum);
                            if (forgottenFare.HasValue)
                            {
                                textBox1.AppendText("卡号：" + cardNum + " - 上次未刷卡下车，已按全程票价 "
                                    + forgottenFare.Value.ToString("F2") + " 元结算" + Environment.NewLine);
                            }

                            StartTripResult result = cardRepository.StartTrip(
                                cardNum, currentStationIndex + 1, DateTime.Now, preDeduct);

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
                                    + preDeduct.ToString("F2") + " 元，余额 "
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
                            int distanceM = fareTable != null
                                ? fareTable.GetDistanceM(openTrip.BoardSeq, alightSeq)
                                : 0;
                            decimal actualFare = fareTable != null
                                ? fareTable.GetFare(openTrip.BoardSeq, alightSeq)
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
                                    + actualFare.ToString("F2") + " 元，退还 "
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
                    }
                }
            }), p);
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
        
        private void btnZC_Click(object sender, EventArgs e)
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

            try
            {
                decimal oldBalance = cardRepository.GetBalance(lbDoorCard.Text) ?? 0m;
                decimal newBalance = cardRepository.Recharge(lbDoorCard.Text, rechargeAmount);

                currentBalance = newBalance;
                label9.Text = newBalance.ToString("F2") + "元";
                lbZCState.Text = "充值成功（原：" + oldBalance.ToString("F2") + "元，充："
                    + rechargeAmount.ToString("F2") + "元，现：" + newBalance.ToString("F2") + "元）";
            }
            catch (Exception ex)
            {
                lbZCState.Text = "充值失败：" + ex.Message;
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
            // 移动到下一站
            currentStationIndex++;
            if (currentStationIndex >= stationPictureBoxes.Length)
            {
                currentStationIndex = 0; // 循环回到起点
            }

            // 将当前及之前的所有站点都设为绿色
            for (int i = 0; i <= currentStationIndex; i++)
            {
                stationPictureBoxes[i].BackColor = Color.Lime;

                // 第 i 站变绿时，它与上一站（i-1）之间的箭头一起变绿；第 0 站没有前一个箭头
                if (i > 0 && arrowPictureBoxes != null && i - 1 < arrowPictureBoxes.Length)
                {
                    arrowPictureBoxes[i - 1].ArrowColor = Color.Lime;
                }
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
