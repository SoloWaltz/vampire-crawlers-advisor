using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace VcAdvisor
{
    /// <summary>
    /// 本地案例库（自我进化的第一层）：
    /// 每一回合记录「归一化局面 → 实际出牌 → 即时结果」，遇到新局面时检索相似案例，
    /// 给出历史平均效果与常见选择。
    ///
    /// 设计原则：
    /// 1) 只用比例特征（生命比、法力比、威胁比），游戏改数值后旧案例仍有参考价值；
    /// 2) 原始数据与派生结论分离：本文件是派生数据，可随时删除重建；
    /// 3) 旧案例按时间衰减加权，近期对局影响更大，自动跟上机制变化；
    /// 4) 只读游戏，不向游戏写入任何内容。
    /// </summary>
    public static class CaseLibrary
    {
        public const int Schema = 2;
        private const int MaxCases = 4000;

        public sealed class Case
        {
            public int SchemaVersion;
            public float HpRatio;
            public float ManaRatio;
            public float IncomingRatio;
            public int Combo;
            public int HandCount;
            public string Key;
            public string Name;
            public float DamageDealt;
            public float DamageTaken;
            public float ManaDelta;
            public bool ComboKept;
            public bool Win;
            public long Ticks;
        }

        public sealed class Result
        {
            public int Samples;
            public float WinRate;
            public float AvgDamageDealt;
            public float AvgDamageTaken;
            public float AvgManaDelta;
            public string CommonChoice;
            public float CommonChoiceRate;
        }

        private static readonly List<Case> _cases = new List<Case>();
        private static bool _loaded;
        private static string _file;
        private static int _drift;

        public static int Count { get { return _cases.Count; } }
        public static int DriftCount { get { return _drift; } }
        public static string FilePath { get { return _file; } }

        /// <summary>首次使用时从本地派生文件加载最近案例（最多 4000 条）。</summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                _file = Path.Combine(LogDir(), "cases-v2.tsv");
                if (!File.Exists(_file)) return;

                var lines = File.ReadAllLines(_file);
                int start = Math.Max(0, lines.Length - MaxCases);
                for (int i = start; i < lines.Length; i++)
                {
                    var c = Parse(lines[i]);
                    if (c != null) _cases.Add(c);
                }
                try { AdvisorPlugin.Logger.LogInfo("[案例] 已加载 " + _cases.Count + " 条历史案例"); } catch { }
            }
            catch { }
        }

        public static void Add(Case c)
        {
            if (c == null || string.IsNullOrEmpty(c.Key)) return;
            c.SchemaVersion = Schema;
            _cases.Add(c);
            if (_cases.Count > MaxCases) _cases.RemoveRange(0, _cases.Count - MaxCases);
            Persist(c);
        }

        /// <summary>相似案例检索：返回最近的 K 条案例的加权统计。</summary>
        public static Result Query(Snapshot s, int k)
        {
            var r = new Result();
            if (s == null || _cases.Count == 0) return r;
            if (k <= 0) k = 40;

            float hp = Ratio(s.Hp, HpDenom(s));
            float mana = s.Mana >= 0f ? s.Mana / 10f : 0f;   // 10 作为常见法力规模
            if (mana > 1.5f) mana = 1.5f;
            float inc = s.Hp > 0.5f ? s.IncomingDamage / s.Hp : 0f;
            int hand = s.Hand != null ? s.Hand.Count : 0;

            var best = new List<KeyValuePair<double, Case>>();
            for (int i = 0; i < _cases.Count; i++)
            {
                var c = _cases[i];
                if (c.SchemaVersion != Schema) continue;

                double d = 2.0 * Math.Abs(c.HpRatio - hp)
                         + 1.2 * Math.Abs(c.ManaRatio - mana)
                         + 2.0 * Math.Abs(c.IncomingRatio - inc)
                         + 0.35 * Math.Min(4, Math.Abs(c.Combo - s.Combo)) / 4.0
                         + 0.25 * Math.Min(10, Math.Abs(c.HandCount - hand)) / 10.0;
                best.Add(new KeyValuePair<double, Case>(d, c));
            }
            if (best.Count == 0) return r;
            best.Sort((a, b) => a.Key.CompareTo(b.Key));

            int n = Math.Min(k, best.Count);
            double wSum = 0, dealt = 0, taken = 0, manaDelta = 0, win = 0;
            var choices = new Dictionary<string, double>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < n; i++)
            {
                var c = best[i].Value;
                double w = Weight(c);
                // 距离越近权重越高
                w /= (1.0 + best[i].Key);
                wSum += w;
                dealt += c.DamageDealt * w;
                taken += c.DamageTaken * w;
                manaDelta += c.ManaDelta * w;
                if (c.Win) win += w;

                double old;
                choices.TryGetValue(c.Key, out old);
                choices[c.Key] = old + w;
                if (!names.ContainsKey(c.Key) && !string.IsNullOrEmpty(c.Name)) names[c.Key] = c.Name;
            }

            if (wSum <= 0) return r;
            r.Samples = n;
            r.AvgDamageDealt = (float)(dealt / wSum);
            r.AvgDamageTaken = (float)(taken / wSum);
            r.AvgManaDelta = (float)(manaDelta / wSum);
            r.WinRate = (float)(win / wSum);

            string topKey = null;
            double topW = -1;
            foreach (var kv in choices)
            {
                if (kv.Value > topW) { topW = kv.Value; topKey = kv.Key; }
            }
            if (!string.IsNullOrEmpty(topKey))
            {
                r.CommonChoice = names.ContainsKey(topKey) ? names[topKey] : topKey;
                r.CommonChoiceRate = (float)(topW / wSum);
            }
            return r;
        }

        /// <summary>记录一次“预测 vs 实际”的偏差，用于识别游戏机制变化。</summary>
        public static void NoteDrift(string what)
        {
            _drift++;
            if (_drift == 1 || _drift % 10 == 0)
            {
                try
                {
                    AdvisorPlugin.Logger.LogInfo(string.Format(
                        "[机制] 检测到第 {0} 次数值偏差（{1}），旧案例权重将自动衰减", _drift, what));
                }
                catch { }
            }
        }

        #region 存取

        private static void Persist(Case c)
        {
            try
            {
                if (string.IsNullOrEmpty(_file)) _file = Path.Combine(LogDir(), "cases-v2.tsv");
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var sb = new StringBuilder();
                sb.Append(Schema).Append('\t')
                  .Append(F(c.HpRatio)).Append('\t')
                  .Append(F(c.ManaRatio)).Append('\t')
                  .Append(F(c.IncomingRatio)).Append('\t')
                  .Append(c.Combo).Append('\t')
                  .Append(c.HandCount).Append('\t')
                  .Append(Clean(c.Key)).Append('\t')
                  .Append(Clean(c.Name)).Append('\t')
                  .Append(F(c.DamageDealt)).Append('\t')
                  .Append(F(c.DamageTaken)).Append('\t')
                  .Append(F(c.ManaDelta)).Append('\t')
                  .Append(c.ComboKept ? '1' : '0').Append('\t')
                  .Append(c.Win ? '1' : '0').Append('\t')
                  .Append(c.Ticks);
                File.AppendAllText(_file, sb.ToString() + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static Case Parse(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(line)) return null;
                var p = line.Split('\t');
                if (p.Length < 14) return null;
                int schema = I(p[0]);
                if (schema != Schema) return null;

                var c = new Case();
                c.SchemaVersion = schema;
                c.HpRatio = Fl(p[1]);
                c.ManaRatio = Fl(p[2]);
                c.IncomingRatio = Fl(p[3]);
                c.Combo = I(p[4]);
                c.HandCount = I(p[5]);
                c.Key = p[6];
                c.Name = p[7];
                c.DamageDealt = Fl(p[8]);
                c.DamageTaken = Fl(p[9]);
                c.ManaDelta = Fl(p[10]);
                c.ComboKept = p[11] == "1";
                c.Win = p[12] == "1";
                c.Ticks = long.Parse(p[13], CultureInfo.InvariantCulture);
                return c;
            }
            catch { return null; }
        }

        private static double Weight(Case c)
        {
            try
            {
                var t = new DateTime(c.Ticks, DateTimeKind.Utc);
                double days = (DateTime.UtcNow - t).TotalDays;
                if (days < 0) days = 0;
                return 1.0 / (1.0 + days / 30.0);   // 30 天半衰
            }
            catch { return 1.0; }
        }

        private static string LogDir()
        {
            string root = null;
            try { root = Paths.BepInExRootPath; } catch { }
            if (string.IsNullOrEmpty(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(root, "VcAdvisorLogs");
        }

        private static float HpDenom(Snapshot s)
        {
            if (s.HpMax > 0f && s.HpMax < 100000f) return s.HpMax;
            return s.HpPeak;
        }

        private static float Ratio(float v, float denom)
        {
            if (v < 0f || denom <= 0f) return 0f;
            return v / denom;
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static float Fl(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static int I(string s)
        {
            int v;
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return v;
        }

        #endregion
    }
}
