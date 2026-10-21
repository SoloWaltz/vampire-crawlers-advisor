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

        // —— 案例库：回合级「局面 → 出牌 → 结果」——
        private sealed class PendingCase
        {
            public float HpRatio, ManaRatio, IncomingRatio;
            public int Combo, HandCount;
            public string Key, Name;
            public float PredictedDamage;
            public float PredictedManaDelta;
            public string HeroSig;
            public int EvoReady;
        }

        private static PendingCase _pending;
        private static float _prevHp = -1f, _prevMana = -1f, _prevEnemyHp = -1f;
        private static int _prevCombo = -1;
        private static float _fHp, _fMana, _fInc;
        private static int _fCombo, _fHand;
        private static string _fHeroSig = "";
        private static int _fEvoReady;
        private static string _gameVersion;

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
            CaseLibrary.EnsureLoaded();

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

            // 案例库基准值
            _pending = null;
            _prevHp = s.Hp;
            _prevMana = s.Mana;
            _prevEnemyHp = EnemyHpTotal(s);
            _prevCombo = s.Combo;
            _fHp = HpRatio(s);
            _fMana = ManaRatio(s);
            _fInc = IncomingRatio(s);
            _fCombo = s.Combo;
            _fHand = s.Hand.Count;
            _fHeroSig = s.Heroes != null ? string.Join(" / ", s.Heroes.ToArray()) : "";
            _fEvoReady = s.EvoReady != null ? s.EvoReady.Count : 0;

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
            _pending = null;
            _prevHp = -1f;
            _prevMana = -1f;
            _prevEnemyHp = -1f;
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

            // 建立待结算案例：使用「出牌前」的局面特征（由上一次 state 记录保存）
            _pending = new PendingCase
            {
                HpRatio = _fHp,
                ManaRatio = _fMana,
                IncomingRatio = _fInc,
                Combo = _fCombo,
                HandCount = _fHand,
                Key = key,
                Name = ci != null ? ci.Name : key,
                PredictedDamage = ci != null ? ci.DealtDamage : -1f,
                PredictedManaDelta = ci != null ? (ci.ManaGain - ci.Cost) : 0f,
                HeroSig = _fHeroSig,
                EvoReady = _fEvoReady
            };

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

            // 结算上一张牌的案例（此时 s 是“出牌后”的状态）
            FinalizePending(s);

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
              .Append(",\"schema\":").Append(CaseLibrary.Schema)
              .Append(",\"gameVersion\":\"").Append(Esc(GameVersion())).Append('"')
              .Append(",\"turn\":").Append(s.TurnEffective)
              .Append(",\"hp\":").Append(F(s.Hp))
              .Append(",\"hpMax\":").Append(F(HpDenom(s)))
              .Append(",\"hpRatio\":").Append(F(HpRatio(s)))
              .Append(",\"manaRatio\":").Append(F(ManaRatio(s)))
              .Append(",\"incomingRatio\":").Append(F(IncomingRatio(s)))
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
                  .Append(",\"projHp\":").Append(F(e.ProjectedHp))
                  .Append(",\"projArmor\":").Append(F(e.ProjectedArmor))
                  .Append(",\"frozen\":").Append(e.Frozen ? "true" : "false")
                  .Append(",\"phase\":").Append(e.Phase)
                  .Append(",\"res\":[").Append(e.KnockbackRes).Append(',').Append(e.DisarmRes).Append(',').Append(e.FreezeRes).Append(']')
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

            sb.Append("],\"heroes\":\"").Append(Esc(string.Join(" / ", s.Heroes.ToArray()))).Append('"')
              .Append(",\"arcanas\":\"").Append(Esc(string.Join(" / ", s.Arcanas.ToArray()))).Append('"')
              .Append(",\"heroSkills\":\"").Append(Esc(string.Join(" / ", s.HeroSkills.ToArray()))).Append('"')
              .Append(",\"evoReady\":\"").Append(Esc(string.Join(" / ", s.EvoReady.ToArray()))).Append('"')
              .Append(",\"evoNear\":\"").Append(Esc(string.Join(" / ", s.EvoNear.ToArray()))).Append('"')
              .Append(",\"build\":\"").Append(Esc(s.BuildTitle ?? "")).Append('"')
              .Append(",\"buildMatch\":").Append(F(s.BuildMatch))
              .Append(",\"coins\":").Append(s.Coins)
              .Append(",\"level\":").Append(s.PlayerLevel)
              .Append(",\"xp\":").Append(s.PlayerXp)
              .Append(",\"maxXp\":").Append(s.MaxXp)
              .Append(",\"drawOrder\":\"").Append(Esc(string.Join(" > ", s.DrawOrder.ToArray()))).Append('"')
              .Append(",\"hand\":[");
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
                  .Append(",\"costRatio\":").Append(F(CostRatio(s, c)))
                  .Append(",\"dmgRatio\":").Append(F(DmgRatio(s, c)))
                  .Append(",\"role\":\"").Append(CardDb.RoleName(c.Role)).Append("\"}");
            }

            sb.Append("],\"sandbox\":").Append(_sandbox ? "true" : "false");
            sb.Append('}');
            Write(sb.ToString());

            // 更新下一条案例的基准值
            _prevHp = s.Hp;
            _prevMana = s.Mana;
            _prevEnemyHp = EnemyHpTotal(s);
            _prevCombo = s.Combo;
            _fHp = HpRatio(s);
            _fMana = ManaRatio(s);
            _fInc = IncomingRatio(s);
            _fCombo = s.Combo;
            _fHand = s.Hand.Count;
            _fHeroSig = s.Heroes != null ? string.Join(" / ", s.Heroes.ToArray()) : "";
            _fEvoReady = s.EvoReady != null ? s.EvoReady.Count : 0;
        }

        #endregion

        #region 工具

        /// <summary>结算上一张牌的案例：用「出牌前 → 出牌后」的差值作为即时收益。</summary>
        private static void FinalizePending(Snapshot s)
        {
            var p = _pending;
            if (p == null) return;
            _pending = null;
            try
            {
                float enemyNow = EnemyHpTotal(s);
                float dealt = (_prevEnemyHp >= 0f && enemyNow >= 0f) ? Math.Max(0f, _prevEnemyHp - enemyNow) : 0f;
                float taken = (_prevHp >= 0f && s.Hp >= 0f) ? Math.Max(0f, _prevHp - s.Hp) : 0f;
                float manaDelta = (_prevMana >= 0f && s.Mana >= 0f) ? s.Mana - _prevMana : 0f;
                bool kept = _prevCombo >= 0 && s.Combo > _prevCombo;

                var c = new CaseLibrary.Case
                {
                    HpRatio = p.HpRatio,
                    ManaRatio = p.ManaRatio,
                    IncomingRatio = p.IncomingRatio,
                    Combo = p.Combo,
                    HandCount = p.HandCount,
                    Key = p.Key,
                    Name = p.Name,
                    DamageDealt = dealt,
                    DamageTaken = taken,
                    ManaDelta = manaDelta,
                    ComboKept = kept,
                    Win = false,   // 单回合案例不依赖整局胜负
                    Ticks = DateTime.UtcNow.Ticks,
                    HeroSig = p.HeroSig,
                    EvoReady = p.EvoReady
                };
                CaseLibrary.Add(c);

                // 机制偏差检测：只做宽松判断，避免正常波动误报
                if (p.PredictedDamage >= 20f && dealt <= 0.5f)
                    CaseLibrary.NoteDrift(string.Format("{0} 预期伤害 {1:0}，实际 0", p.Name, p.PredictedDamage));
                else if (p.PredictedDamage > 0f && dealt > p.PredictedDamage * 2.5f + 20f)
                    CaseLibrary.NoteDrift(string.Format("{0} 预期伤害 {1:0}，实际 {2:0}", p.Name, p.PredictedDamage, dealt));
                if (Math.Abs(manaDelta - p.PredictedManaDelta) >= 3f)
                    CaseLibrary.NoteDrift(string.Format("{0} 预期法力变化 {1:0.#}，实际 {2:0.#}",
                        p.Name, p.PredictedManaDelta, manaDelta));
            }
            catch { }
        }

        private static string GameVersion()
        {
            if (!string.IsNullOrEmpty(_gameVersion)) return _gameVersion;
            try { _gameVersion = UnityEngine.Application.version; } catch { }
            if (string.IsNullOrEmpty(_gameVersion)) _gameVersion = "unknown";
            return _gameVersion;
        }

        private static float EnemyHpTotal(Snapshot s)
        {
            float t = 0f;
            for (int i = 0; i < s.Enemies.Count; i++)
            {
                if (s.Enemies[i].Hp > 0f) t += s.Enemies[i].Hp;
            }
            return t;
        }

        private static float HpRatio(Snapshot s)
        {
            float d = HpDenom(s);
            if (d <= 0f || s.Hp < 0f) return 0f;
            return s.Hp / d;
        }

        private static float ManaRatio(Snapshot s)
        {
            if (s.Mana < 0f) return 0f;
            float r = s.Mana / 10f;          // 10 视为常见法力规模
            return r > 1.5f ? 1.5f : r;
        }

        private static float IncomingRatio(Snapshot s)
        {
            if (s.Hp <= 0.5f) return 0f;
            return (float)s.IncomingDamage / s.Hp;
        }

        private static float CostRatio(Snapshot s, CardInfo c)
        {
            float m = s.Mana > 1f ? s.Mana : 1f;
            return c.Cost / m;
        }

        private static float DmgRatio(Snapshot s, CardInfo c)
        {
            if (c.DealtDamage <= 0) return 0f;
            float t = EnemyHpTotal(s);
            if (t < 1f) t = 1f;
            return c.DealtDamage / t;
        }

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

        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
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
