using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace _125GateApp
{
    public class JsonFormat
    {
        /// <summary>
        /// 读取所有卡片信息
        /// </summary>
        public static JArray ReadSensorJson()
        {
            string jsonfile = System.Environment.CurrentDirectory + "/plugin/125GateApp/db.json";
            using (System.IO.StreamReader file = System.IO.File.OpenText(jsonfile))
            {
                using (JsonTextReader reader = new JsonTextReader(file))
                {
                    JArray o = (JArray)JToken.ReadFrom(reader);
                    reader.Close();
                    return o;
                }
            }
        }

        /// <summary>
        /// 根据卡号查询卡片信息
        /// </summary>
        public static JObject FindCard(string cardnum)
        {
            JArray ja = ReadSensorJson();
            foreach (JObject j in ja)
            {
                if (j["cardnum"].ToString().Equals(cardnum))
                {
                    return j;
                }
            }
            return null;
        }

        /// <summary>
        /// 添加或更新卡片余额信息（在原有金额基础上追加）
        /// </summary>
        public static bool AddJson(string cardnum, decimal balance)
        {
            bool OK = true;
            try
            {
                JArray ja = null;
                bool b = false;
                string jsonfile = System.Environment.CurrentDirectory + "/plugin/125GateApp/db.json";
                string json = File.ReadAllText(jsonfile);
                if (!json.Equals(""))
                    ja = JArray.Parse(json);
                else
                    ja = new JArray();

                foreach (JObject j in ja)
                {
                    if (j["cardnum"].ToString().Equals(cardnum))
                    {
                        decimal currentBalance = j["balance"]?.Value<decimal>() ?? 0;
                        j["balance"] = currentBalance + balance;
                        b = true;
                        break;
                    }
                }
                if (!b)
                {
                    JObject card = new JObject();
                    card["cardnum"] = cardnum;
                    card["balance"] = balance;
                    ja.Add(card);
                }
                File.WriteAllText(jsonfile, JsonConvert.SerializeObject(ja));
            }
            catch
            {
                OK = false;
            }
            return OK;
        }

        /// <summary>
        /// 更新卡片余额（扣费或充值）
        /// </summary>
        public static bool UpdateBalance(string cardnum, decimal amount)
        {
            bool OK = true;
            try
            {
                string jsonfile = System.Environment.CurrentDirectory + "/plugin/125GateApp/db.json";
                string json = File.ReadAllText(jsonfile);
                JArray ja = null;
                
                if (!json.Equals(""))
                    ja = JArray.Parse(json);
                else
                    return false;

                foreach (JObject j in ja)
                {
                    if (j["cardnum"].ToString().Equals(cardnum))
                    {
                        decimal currentBalance = j["balance"]?.Value<decimal>() ?? 0;
                        j["balance"] = currentBalance + amount;
                        File.WriteAllText(jsonfile, JsonConvert.SerializeObject(ja));
                        return true;
                    }
                }
                OK = false;
            }
            catch
            {
                OK = false;
            }
            return OK;
        }
    }
}
