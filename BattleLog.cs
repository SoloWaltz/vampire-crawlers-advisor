using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace VcAdvisor
{
    /// <summary>
    /// 对局记录（只写自己的日志文件，绝不改动游戏）：
    /// 把每个「可决策局面」、助手的建议、玩家的实际出牌、战斗结果写成 JSONL，
    /// 供后续离线分析 / 校准评分权重使用。
    ///
    /// 设计要点：
    /// 1) 只记录「游戏已算好的有效值」（实际费用、运行时伤害、W 标记），不记录静态卡牌库；
    /// 2) 出牌序列取自游戏自己的 TurnHistory，比手牌差分可靠；
    /// 3) 显式沙盒模式 + 自动可疑局判定，避免「故意输」污染后续训练数据。
    /// </summary>
    public static class BattleLog
    {
        private static bool _sandbox;

        private static string _file;
        private static string _day;
        private static int _records;

        private static bool _inEncounter;
        private static int _encSeq;
        private static int _lastTurn = -1;
        private static int _lastTurnPlayed;
        private static int _handStart;
        private static int _playsThisTurn;
        private static int _passTurns;
        private static bool _hadPlayableThisTurn;
        private static bool _sawAlive;

        private static string _lastStateSig;
        private static DateTime _lastStateTime = DateTime.MinValue;
        private static DateTime _lastTurnEndTime = DateTime.MinValue;
        private static Dictionary<string, CardInfo> _lastHand = new Dictionary<string, CardInfo>(StringComparer.Ordinal);
        private static Dictionary<string, int> _lastRank = new Dictionary<string, int>(StringComparer.Ordinal);
        private static string _lastTopKey;

        public static bool SandboxMode { get { return _sandbox; } }
        public static int Records { get { return _records; } }
        public static string FilePath { get { return _file; } }

        /// <summary>训练模式 / 沙盒模式切换：沙盒局只存档，不参与后续训练。</summary>
        public static void ToggleSandbox()
        {
            _sandbox = !_sandbox;
            try
            {
                AdvisorPlugin.Logger.LogInfo(_sandbox
                    ? "[记录] 已切换为沙盒模式：本局只存档，不参与训练"
                    : "[记录] 已切换为训练模式：本局数据可用于学习");
            }
            catch { }
        }

        /// <summary>每次扫描后调用（约 4 次/秒）。</summary>
        public static void Tick(Snapshot s)
        {
            if (s == null) return;

            if (!s.Valid)
            {
                if (_inEncounter) EndEncounter(s, "unknown");
                return;
            }

            // 遭遇开始 / 结束
            // 注意：敌人全灭后 EnemyEncounterModel 仍可能存在（HasEnemy 仍为 true），
            // 因此“结束”必须同时看 EnemyCount，并确认之前确实见过活着的敌人。
            if (s.EnemyCount > 0) _sawAlive = true;
            bool ended = _inEncounter && (!s.HasEnemy || (s.EnemyCount <= 0 && _sawAlive));
            if (ended) EndEncounter(s, s.Hp > 0.5f ? "win" : "lose");

            if (s.HasEnemy && s.EnemyCount > 0 && !_inEncounter) StartEncounter(s);

            if (!_inEncounter) return;

            // 回合切换：结算上一回合的统计
            // 防抖：若 500ms 内回合号反复跳变，说明是采样抖动，只对齐不写记录
            if (_lastTurn >= 0 && s.TurnEffective != _lastTurn)
            {
                if ((DateTime.UtcNow - _lastTurnEndTime).TotalMilliseconds >= 500)
                {
                    WriteTurnEnd(s);
                }
                else
                {
                    _lastTurn = s.TurnEffective;
                }
            }

            // 出牌：以游戏自己的 TurnHistory 为准
            int played = s.TurnPlayed != null ? s.TurnPlayed.Count : 0;
            if (played > _lastTurnPlayed)
            {
                for (int i = _lastTurnPlayed; i < played; i++) WritePlay(s.TurnPlayed[i]);
            }
            _lastTurnPlayed = played;

            // 局面记录（去重 + 节流）
            WriteStateIfChanged(s);

            if (HasPlayable(s)) _hadPlayableThisTurn = true;

            if (_lastTurn < 0)
            {
                _lastTurn = s.TurnEffective;
                _handStart = s.Hand.Count;
                _playsThisTurn = 0;
                _hadPlayableThisTurn = false;
                _lastTurnPlayed = played;
            }
        }

        #region 事件

        private static void StartEncounter(Snapshot s)
        {
            _inEncounter = true;
            _encSeq++;
            _lastTurn = -1;
            _lastTurnPlayed = 0;
            _playsThisTurn = 0;
            _passTurns = 0;
            _hadPlayableThisTurn = false;
            _handStart = s.Hand.Count;
            _lastStateSig = null;
            _sawAlive = false;
            _lastTurnEndTime = DateTime.MinValue;
            _lastHand.Clear();
            _lastRank.Clear();
            _lastTopKey = null;

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"start\",\"ts\":").Append(Ts())
              .Append(",\"enc\":").Append(_encSeq)
              .Append(",\"turn\":").Append(s.TurnEffective)
              .Append(",\"hp\":").Append(F(s.Hp))
              .Append(",\"hpMax\":").Append(F(HpDenom(s)))
              .Append(",\"mana\":").Append(F(s.Mana))
              .Append(",\"enemies\":").Append(EnemySummary(s))
              .Append(",\"sandbox\":").Append(_sandbox ? "true" : "false")
              .Append('}');
            Write(sb.ToString());
        }

        private static void WriteTurnEnd(Snapshot s)
        {
            bool pass = _playsThisTurn == 0 && _hadPlayableThisTurn;
            if (pass) _passTurns++;

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"turn_end\",\"ts\":").Append(Ts())
              .Append(",\"enc\":").Append(_encSeq)
              .Append(",\"turn\":").Append(_lastTurn)
              .Append(",\"plays\":").Append(_playsThisTurn)
              .Append(",\"handStart\":").Append(_handStart)
              .Append(",\"pass\":").Append(pass ? "true" : "false")
              .Append(",\"sandbox\":").Append(_sandbox ? "true" : "false")
              .Append('}');
            Write(sb.ToString());

            _lastTurn = s.TurnEffective;
            _lastTurnEndTime = DateTime.UtcNow;
            _handStart = s.Hand.Count;
            _playsThisTurn = 0;
            _hadPlayableThisTurn = false;
        }

        private static void EndEncounter(Snapshot s, string result)
        {
            int turns = _lastTurn >= 0 ? _lastTurn + 1 : 0;
            bool sabotage = turns >= 3 && _passTurns >= Math.Max(2, turns / 2);

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"end\",\"ts\":").Append(Ts())
              .Append(",\"enc\":").Append(_encSeq)
              .Append(",\"result\":\"").Append(result).Append('"')
              .Append(",\"turns\":").Append(turns)
              .Append(",\"passTurns\":").Append(_passTurns)
              .Append(",\"hp\":").Append(F(s.Hp))
              .Append(",\"hpMax\":").Append(F(HpDenom(s)))
              .Append(",\"sandbox\":").Append(_sandbox ? "true" : "false")
              .Append(",\"sabotage\":").Append(sabotage ? "true" : "false")
              .Append('}');
            Write(sb.ToString());

            _inEncounter = false;
            _sawAlive = false;
            _lastTurn = -1;
            _lastStateSig = null;
            _lastHand.Clear();
            _lastRank.Clear();
        }

        private static void WritePlay(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _playsThisTurn++;

            CardInfo ci;
            _lastHand.TryGetValue(key, out ci);

            int rank = 0;
            _lastRank.TryGetValue(key, out rank);
            bool top = !string.IsNullOrEmpty(_lastTopKey) && _lastTopKey == key;

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"play\",\"ts\":").Append(Ts())
              .Append(",\"enc\":").Append(_encSeq)
              .Append(",\"turn\":").Append(_lastTurn)
              .Append(",\"k\":\"").Append(Esc(key)).Append('"');
            if (ci != null)
            {
                sb.Append(",\"name\":\"").Append(Esc(ci.Name)).Append('"')
                  .Append(",\"cost\":").Append(ci.Cost)
                  .Append(",\"base\":").Append(ci.BaseCost)
                  .Append(",\"dmg\":").Append(ci.DealtDamage)
                  .Append(",\"manaGain\":").Append(ci.ManaGain)
                  .Append(",\"wild\":").Append(ci.Wild ? "true" : "false")
                  .Append(",\"role\":\"").Append(CardDb.RoleName(ci.Role)).Append('"');
            }
            sb.Append(",\"rank\":").Append(rank)
              .Append(",\"top\":").Append(top ? "true" : "false")
              .Append('}');
            Write(sb.ToString());
        }

        private static void WriteStateIfChanged(Snapshot s)
        {
            var sig = new StringBuilder();
            sig.Append(s.TurnEffective).Append('|').Append(F(s.Mana)).Append('|').Append(F(s.Hp)).Append('|');
            for (int i = 0; i < s.Hand.Count; i++)
            {
                var c = s.Hand[i];
                sig.Append(c.Key).Append('#').Append(c.InstanceId).Append('@').Append(c.Cost).Append(',');
            }
            sig.Append('|');
            for (int i = 0; i < s.Enemies.Count; i++)
            {
                var e = s.Enemies[i];
                sig.Append(e.Name).Append('@').Append(F(e.Hp)).Append(',');
            }
            string key = sig.ToString();
            if (key == _lastStateSig) return;
            if ((DateTime.UtcNow - _lastStateTime).TotalMilliseconds < 250) return;
            _lastStateSig = key;
            _lastStateTime = DateTime.UtcNow;

            // 记录「出牌前」的建议排名，供后续判断玩家是否采纳
            _lastHand.Clear();
            _lastRank.Clear();
            _lastTopKey = null;
            for (int i = 0; i < s.Hand.Count; i++)
            {
                var c = s.Hand[i];
                if (c == null || string.IsNullOrEmpty(c.Key)) continue;
                if (!_lastHand.ContainsKey(c.Key)) _lastHand[c.Key] = c;
                if (c.PlanOrder > 0)
                {
                    int old;
                    if (!_lastRank.TryGetValue(c.Key, out old) || c.PlanOrder < old)
                        _lastRank[c.Key] = c.PlanOrder;
                    if (c.PlanOrder == 1) _lastTopKey = c.Key;
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"t\":\"state\",\"ts\":").Append(Ts())
              .Append(",\"enc\":").Append(_encSeq)
              .Append(",\"turn\":").Append(s.TurnEffective)
              .Append(",\"hp\":").Append(F(s.Hp))
              .Append(",\"hpMax\":").Append(F(HpDenom(s)))
              .Append(",\"armor\":").Append(F(s.Armor))
              .Append(",\"mana\":").Append(F(s.Mana))
              .Append(",\"combo\":").Append(s.Combo)
              .Append(",\"nextCombo\":").Append(s.NextComboCost)
              .Append(",\"reverse\":").Append(s.ReverseCombo ? "true" : "false")
              .Append(",\"incoming\":").Append(s.IncomingDamage)
              .Append(",\"enemies\":[");

            for (int i = 0; i < s.Enemies.Count; i++)
            {
                var e = s.Enemies[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Esc(e.Name)).Append('"')
                  .Append(",\"hp\":").Append(F(e.Hp))
                  .Append(",\"max\":").Append(F(e.HpMax))
                  .Append(",\"armor\":").Append(F(e.Armor))
                  .Append(",\"dmg\":").Append(e.IncomingDamage)
                  .Append(",\"intent\":\"").Append(Esc(string.Join(" / ", e.Intents.ToArray()))).Append("\"}");
            }

            sb.Append("],\"stats\":[");
            for (int i = 0; i < s.Stats.Count; i++)
            {
                var st = s.Stats[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Esc(st.Name)).Append('"')
                  .Append(",\"v\":").Append(F(st.Value))
                  .Append(",\"good\":").Append(st.Good ? "true" : "false").Append('}');
            }

            sb.Append("],\"hand\":[");
            for (int i = 0; i < s.Hand.Count; i++)
            {
                var c = s.Hand[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"k\":\"").Append(Esc(c.Key)).Append('"')
                  .Append(",\"n\":\"").Append(Esc(c.Name)).Append('"')
                  .Append(",\"cost\":").Append(c.Cost)
                  .Append(",\"base\":").Append(c.BaseCost)
                  .Append(",\"dmg\":").Append(c.DealtDamage)
                  .Append(",\"mana\":").Append(c.ManaGain)
                  .Append(",\"wild\":").Append(c.Wild ? "true" : "false")
                  .Append(",\"inCombo\":").Append(c.InCombo ? "true" : "false")
                  .Append(",\"playable\":").Append(c.Playable ? "true" : "false")
                  .Append(",\"plan\":").Append(c.PlanOrder)
                  .Append(",\"score\":").Append(c.Score)
                  .Append(",\"role\":\"").Append(CardDb.RoleName(c.Role)).Append("\"}");
            }

            sb.Append("],\"sandbox\":").Append(_sandbox ? "true" : "false");
            sb.Append('}');
            Write(sb.ToString());
        }

        #endregion

        #region 工具

        private static bool HasPlayable(Snapshot s)
        {
            for (int i = 0; i < s.Hand.Count; i++)
            {
                var c = s.Hand[i];
                // 只要「当前法力打得出来」就算有过出牌机会（不要求已进入推荐计划）
                if (c != null && c.Playable) return true;
            }
            return false;
        }

        private static float HpDenom(Snapshot s)
        {
            if (s.HpMax > 0f && s.HpMax < 100000f) return s.HpMax;
            return s.HpPeak;
        }

        private static string EnemySummary(Snapshot s)
        {
            var parts = new List<string>();
            for (int i = 0; i < s.Enemies.Count; i++)
            {
                var e = s.Enemies[i];
                if (i >= 8) { parts.Add("…"); break; }
                parts.Add(e.Name);
            }
            return "\"" + Esc(string.Join(" / ", parts.ToArray())) + "\"";
        }

        private static void Write(string line)
        {
            try
            {
                EnsureFile();
                if (string.IsNullOrEmpty(_file)) return;
                File.AppendAllText(_file, line + "\n", new UTF8Encoding(false));
                _records++;
            }
            catch { }
        }

        private static void EnsureFile()
        {
            string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_file != null && day == _day) return;

            string root = null;
            try { root = Paths.BepInExRootPath; } catch { }
            if (string.IsNullOrEmpty(root)) root = AppDomain.CurrentDomain.BaseDirectory;

            string dir = Path.Combine(root, "VcAdvisorLogs");
            Directory.CreateDirectory(dir);

            _day = day;
            _file = Path.Combine(dir, "battles-" + day + ".jsonl");
            try { AdvisorPlugin.Logger.LogInfo("[记录] 对局日志：" + _file); } catch { }
        }

        /// <summary>精确到毫秒的本地时间字符串（便于和其它日志对齐）。</summary>
        private static string Ts()
        {
            return "\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "\"";
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion
    }
}
