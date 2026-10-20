using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using UnityEngine;

namespace VcAdvisor
{
    [BepInPlugin(AdvisorPlugin.PluginGuid, "Vampire Crawlers 打牌助手", "1.0.0")]
    public class AdvisorPlugin : BasePlugin
    {
        public const string PluginGuid = "vampirecrawlers.advisor";

        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = base.Log;
            Logger.LogInfo("Vampire Crawlers 打牌助手 v1.0.0 已加载（只读模式，不修改任何游戏数值）");
            AddComponent<AdvisorBehaviour>();
        }
    }

    public class AdvisorBehaviour : MonoBehaviour
    {
        public AdvisorBehaviour(IntPtr ptr) : base(ptr) { }

        // —— 状态 ——
        private float _nextScan;
        private Snapshot _snap;
        private bool _show;              // 游戏内叠加层：默认隐藏，F7 可切换（后备方案）
        private bool _showAdvanced = true;
        private string _fatal;

        // —— 伴侣窗口 ——
        private bool _companionStarted;
        private string _lastContentSig;
        private bool _lastAdv = true;

        internal class Line
        {
            public string Text;
            public int Kind; // 0 标题 1 副标题 2 普通 3 高亮 4 提示
        }

        private readonly List<Line> _lines = new List<Line>();

        // —— 样式 ——
        private Font _font;
        private bool _stylesReady;
        private bool _cjkOk;
        private GUIStyle _panel, _title, _sub, _row, _best, _hint, _btn;
        private Texture2D _bg;

        private string T(string zh, string en)
        {
            return _cjkOk ? zh : en;
        }

        void Update()
        {
            // 游戏内叠加层仅作后备，F7 切换
            try { if (Input.GetKeyDown(KeyCode.F7)) _show = !_show; } catch { }

            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.25f;

            try
            {
                var snap = GameState.Read();
                Scoring.Evaluate(snap);
                _snap = snap;
                RebuildLines();
                _fatal = null;
                SelfCheck(snap);
                PlanLog();
                EnemyLog(snap);
                BootCheck();
                Heartbeat();
                PushCompanion();
            }
            catch (Exception e)
            {
                _fatal = e.Message;
            }
        }

        private static void R(List<CompanionWindow.Row> rows, string text, int kind)
        {
            rows.Add(new CompanionWindow.Row { Text = text, Kind = kind });
        }

        /// <summary>追加一行并返回它，便于继续挂上色条 / 进度条 / 底色等视觉信息。</summary>
        private static CompanionWindow.Row Add(List<CompanionWindow.Row> rows, string text, int kind)
        {
            var row = new CompanionWindow.Row { Text = text, Kind = kind };
            rows.Add(row);
            return row;
        }

        /// <summary>把当前局势推送给独立的置顶伴侣窗口（仅在内容变化时重绘）。</summary>
        private void PushCompanion()
        {
            if (!_companionStarted)
            {
                _companionStarted = true;
                CompanionWindow.Start();
            }

            bool adv = CompanionWindow.AdvancedMode;
            List<CompanionWindow.Row> rows = BuildCompanionRows(adv);

            var sig = new StringBuilder();
            sig.Append(adv ? '1' : '0').Append('|');
            foreach (var r in rows)
                sig.Append(r.Kind).Append(':').Append(r.Indent).Append(':').Append(r.Text).Append('\n');
            string key = sig.ToString();

            if (key == _lastContentSig && adv == _lastAdv) return;
            _lastContentSig = key;
            _lastAdv = adv;

            CompanionWindow.SetContent(rows, "打牌助手");
        }

        /// <summary>构建伴侣窗口的四块常显信息：出牌顺序 / 敌方威胁 / 我方增益减益 / 牌库构成。</summary>
        private List<CompanionWindow.Row> BuildCompanionRows(bool adv)
        {
            var rows = new List<CompanionWindow.Row>();
            var s = _snap;

            if (s == null) { R(rows, "正在读取游戏状态…", 4); return rows; }
            if (!s.Valid) { R(rows, s.Message, 4); return rows; }

            // —— 顶部状态（纯文字，不画任何线条）——
            float hpDenom = (s.HpMax > 0f && s.HpMax < 100000f) ? s.HpMax : s.HpPeak;

            var head = new StringBuilder();
            head.Append("回合 ").Append(s.TurnDisplay >= 0 ? s.TurnDisplay : s.Turn);
            if (s.Combo > 1) head.Append("   连击 x").Append(s.Combo);
            head.Append("   法力 ").Append(s.Mana >= 0f ? s.Mana.ToString("0.##") : "?");
            Add(rows, head.ToString(), 1);

            var line2 = new StringBuilder();
            bool hpLow = s.Hp >= 0f && hpDenom > 0f && s.Hp <= hpDenom * 0.35f;
            if (s.Hp >= 0f)
            {
                if (hpLow) line2.Append("⚠ ");
                line2.Append("生命 ").Append(s.Hp.ToString("0"));
                if (hpDenom > 0f) line2.Append('/').Append(hpDenom.ToString("0"));
            }
            if (s.Armor > 0f) { if (line2.Length > 0) line2.Append("   "); line2.Append("护甲 ").Append(s.Armor.ToString("0")); }
            if (s.HasEnemy) { if (line2.Length > 0) line2.Append("   "); line2.Append("敌人 ").Append(s.EnemyCount).Append(" 只"); }
            if (line2.Length == 0) line2.Append("状态读取中…");
            Add(rows, line2.ToString(), hpLow ? 2 : 1);

            // —— ① 出牌顺序（核心）——
            R(rows, "", 2);
            if (s.Hand.Count == 0)
            {
                R(rows, s.Message.Length > 0 ? s.Message : "当前没有手牌", 4);
            }
            else
            {
                string title;
                if (s.PlanCount > 0)
                    title = string.Format("★ 出牌顺序（本回合可打 {0} 张）", s.PlanCount);
                else if (s.IsTurnEnding)
                    title = "★ 出牌顺序（本回合已结束）";
                else if (!s.CanPlayCard)
                    title = "★ 出牌顺序（等待你的回合…）";
                else
                    title = "★ 出牌顺序（本回合法力不足）";
                R(rows, title, 0);

                // 连击阶梯提示：直接取自游戏的 PreviousComboCardCost，比逐张试探更直观
                if (s.Combo > 1 && s.NextComboCost >= 0)
                    R(rows, string.Format("   续连击需 {0} 费（{1}阶梯）",
                        s.NextComboCost, s.ReverseCombo ? "递减" : "递增"), 4);

                if (s.PlanCount > 0)
                {
                    int n = Math.Min(adv ? 10 : 6, s.Hand.Count);
                    for (int i = 0; i < n; i++)
                    {
                        var c = s.Hand[i];
                        string mark = c.PlanOrder > 0 ? c.PlanOrder.ToString() : "·";
                        string costTxt = c.ManaGain > 0
                            ? string.Format("费{0} 回{1}", c.Cost, c.ManaGain)
                            : string.Format("费{0}", c.Cost);

                        var sb1 = new StringBuilder();
                        sb1.Append(mark).Append(". ").Append(c.Name)
                           .Append("   [").Append(CardDb.RoleName(c.Role)).Append(" · ").Append(costTxt).Append(']')
                           .Append("   ").Append(c.Score);
                        if (c.Wild) sb1.Append("  ★万能");
                        if (c.Breakable && c.CrackStage == 1) sb1.Append("  ⚠开裂");
                        if (c.Breakable && c.CrackStage == 2) sb1.Append("  ✖已碎");

                        Add(rows, sb1.ToString(), i == 0 && c.PlanOrder > 0 ? 3 : 2);

                        if (adv && c.Reasons.Count > 0)
                        {
                            var rr = Add(rows, "     · " + string.Join("；", c.Reasons.ToArray()), 4);
                            rr.Indent = 6;
                        }
                    }
                    if (s.Hand.Count > n)
                        R(rows, string.Format("… 另有 {0} 张未列出", s.Hand.Count - n), 4);

                    string tail = s.PlanManaGain > 0 ? string.Format("（含回蓝 +{0:0.##}）", s.PlanManaGain) : "";
                    R(rows, string.Format("⇢ 消耗法力 {0:0.##}，剩余 {1:0.##}{2}",
                        s.PlanManaUsed, s.PlanRemaining, tail), 1);
                }
                // 打不出任何牌时不再罗列负分卡表，只给出状态说明（避免出现无意义的“推荐”）
                else if (s.IsTurnEnding)
                {
                    R(rows, "⇢ 本回合已结束，等待下一回合恢复法力", 1);
                }
                else if (!s.CanPlayCard)
                {
                    R(rows, "⇢ 现在还不是你的出牌时机", 1);
                }
                else if (s.ManaKnown)
                {
                    R(rows, string.Format("⇢ 当前法力 {0:0.##}，打不出任何手牌", s.Mana), 1);
                }
                else
                {
                    R(rows, "⇢ 暂无可出的牌", 1);
                }
            }

            // —— ② 敌方威胁 ——
            if (s.HasEnemy)
            {
                R(rows, "", 2);
                if (s.IncomingDamage > 0)
                {
                    string warn = s.Hp > 0.5f && s.IncomingDamage >= s.Hp * 0.55f
                        ? string.Format("⚠ 本回合预计受到 {0} 点伤害（约生命 {1:0}%）",
                            s.IncomingDamage, s.IncomingDamage / s.Hp * 100f)
                        : string.Format("敌方威胁：本回合预计受到 {0} 点伤害", s.IncomingDamage);
                    R(rows, warn, 3);
                }
                else
                {
                    R(rows, "敌方威胁：本回合暂无攻击意图", 1);
                }

                // 同种敌人合并显示（蝙蝠群往往十几只）
                var groups = GroupEnemies(s.Enemies);
                int total = 0;
                foreach (var g in groups) total += g.Count;
                R(rows, string.Format("共 {0} 只（{1} 种）", total, groups.Count), 1);

                int m = Math.Min(adv ? 8 : 4, groups.Count);
                for (int i = 0; i < m; i++)
                {
                    var g = groups[i];
                    var sb = new StringBuilder();
                    sb.Append("· ").Append(g.Name);
                    if (g.Count > 1) sb.Append(" ×").Append(g.Count);
                    sb.Append("   HP ").Append(g.Hp.ToString("0"));
                    if (g.HpMax > 0) sb.Append('/').Append(g.HpMax.ToString("0"));
                    if (g.Armor > 0.5f) sb.Append("   甲 ").Append(g.Armor.ToString("0"));
                    // 会打人的敌人在名称后加标记，纯辅助型不加
                    if (g.Dmg > 0) sb.Append("   ⚔造成伤害");
                    Add(rows, sb.ToString(), 2);

                    var it = new StringBuilder();
                    foreach (var kv in g.Intents)
                    {
                        if (it.Length > 0) it.Append("  ");
                        it.Append(kv.Key);
                        if (kv.Value > 1) it.Append("×").Append(kv.Value);
                    }
                    var ir = Add(rows, "     意图：" + (it.Length > 0 ? it.ToString() : "无"), 4);
                    ir.Indent = 6;
                }
                if (groups.Count > m)
                    R(rows, string.Format("… 另有 {0} 种未列出", groups.Count - m), 4);
            }

            // —— ③ 我方增益 / 减益（与游戏左上/右上角图标同源）——
            R(rows, "", 2);
            if (s.Stats.Count == 0)
            {
                R(rows, "我方属性：无加成", 1);
            }
            else
            {
                var good = new StringBuilder();
                var bad = new StringBuilder();
                foreach (var st in s.Stats)
                {
                    string one = string.Format("{0} {1}", st.Name, FormatStat(st));
                    if (st.Good)
                    {
                        if (good.Length > 0) good.Append("   ");
                        good.Append(one);
                    }
                    else
                    {
                        if (bad.Length > 0) bad.Append("   ");
                        bad.Append(one);
                    }
                }
                if (good.Length > 0) R(rows, "增益：" + good, 3);
                if (bad.Length > 0) R(rows, "减益：" + bad, 4);
            }

            // —— ④ 牌库构成 ——
            R(rows, "", 2);
            R(rows, string.Format("牌库：抽牌 {0} · 弃牌 {1} · 销毁 {2} · 连击 {3}",
                s.DrawCount, s.DiscardCount, s.DestroyCount, s.ComboPileCount), 1);

            if (s.DrawComposition.Count > 0)
            {
                int m = Math.Min(adv ? 10 : 5, s.DrawComposition.Count);
                for (int i = 0; i < m; i++)
                {
                    var kv = s.DrawComposition[i];
                    float p = s.DrawCount > 0 ? kv.Value * 100f / s.DrawCount : 0f;
                    R(rows, string.Format("     {0} ×{1}   ({2:0.#}%)", kv.Key, kv.Value, p), 2);
                }
                if (s.DrawComposition.Count > m)
                    R(rows, string.Format("     … 另有 {0} 类牌", s.DrawComposition.Count - m), 4);
            }

            return rows;
        }

        /// <summary>百分比类属性显示成 “%”，其余显示成数量（与游戏角标一致）。</summary>
        private static string FormatStat(StatInfo st)
        {
            switch (st.Name)
            {
                case "力量":
                case "范围":
                case "成长":
                case "贪婪":
                case "幸运":
                case "诅咒":
                case "风险":
                    return st.Value.ToString("0.#") + "%";
                default:
                    return st.Value.ToString("0.#");
            }
        }

        private class EnemyGroup
        {
            public string Name;
            public int Count;
            public float Hp, HpMax, Armor;
            public int Dmg;
            public List<KeyValuePair<string, int>> Intents = new List<KeyValuePair<string, int>>();
        }

        /// <summary>按名称合并同种敌人，并按威胁（预计伤害→血量）排序。</summary>
        private static List<EnemyGroup> GroupEnemies(List<EnemyInfo> list)
        {
            var groups = new List<EnemyGroup>();
            var index = new Dictionary<string, EnemyGroup>();
            foreach (var e in list)
            {
                EnemyGroup g;
                if (!index.TryGetValue(e.Name, out g))
                {
                    g = new EnemyGroup { Name = e.Name };
                    index[e.Name] = g;
                    groups.Add(g);
                }
                g.Count++;
                g.Hp += e.Hp;
                g.HpMax += e.HpMax;
                g.Armor += e.Armor;
                g.Dmg += e.IncomingDamage;
                foreach (var it in e.Intents)
                {
                    bool found = false;
                    for (int i = 0; i < g.Intents.Count; i++)
                    {
                        if (g.Intents[i].Key == it)
                        {
                            g.Intents[i] = new KeyValuePair<string, int>(it, g.Intents[i].Value + 1);
                            found = true;
                            break;
                        }
                    }
                    if (!found) g.Intents.Add(new KeyValuePair<string, int>(it, 1));
                }
            }
            groups.Sort((a, b) =>
            {
                int c = b.Dmg.CompareTo(a.Dmg);
                if (c != 0) return c;
                c = b.HpMax.CompareTo(a.HpMax);
                if (c != 0) return c;
                return b.Count.CompareTo(a.Count);
            });
            return groups;
        }

        private bool _loggedBoot;

        private float _nextBeat;

        /// <summary>每 3 秒记录一次心跳，用于判断 OnGUI/Update 是否仍在被调用。</summary>
        private void Heartbeat()
        {
            if (Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + 3f;
            try
            {
                AdvisorPlugin.Logger.LogInfo(string.Format(
                    "[心跳] OnGUI调用={0} 已到绘制={1} 面板非空={2} 可见={3} 行数={4} 错误={5}",
                    _guiCalls, _drawReached, _panel != null, _show, _lines.Count,
                    _guiErr == null ? "无" : _guiErr));
            }
            catch { }
        }

        /// <summary>启动数秒后记录一次读取状态，便于排查“面板空白”。</summary>
        private void BootCheck()
        {
            if (_loggedBoot || Time.unscaledTime < 3f) return;
            _loggedBoot = true;
            try
            {
                var s0 = GameState.Read();
                AdvisorPlugin.Logger.LogInfo(string.Format(
                    "[自检] 状态读取 Valid={0} 手牌={1} 在战斗中={2} 信息={3}",
                    s0.Valid, s0.Hand.Count, s0.InEncounter, s0.Message));
            }
            catch (Exception e)
            {
                AdvisorPlugin.Logger.LogWarning("[自检] 状态读取异常: " + e.Message);
            }
        }

        private bool _loggedHand;

        /// <summary>首次读到手牌时在日志里打一条自检信息，便于确认读取链路是否正常。</summary>
        private void SelfCheck(Snapshot s)
        {
            if (_loggedHand || s == null || !s.Valid || s.Hand.Count == 0) return;
            _loggedHand = true;
            try
            {
                var sb = new StringBuilder();
                sb.Append("[自检] 手牌 ").Append(s.Hand.Count).Append(" 张：");
                foreach (var c in s.Hand)
                {
                    sb.Append(c.Name).Append('(').Append(c.Key)
                      .Append(" 费").Append(c.Cost);
                    if (c.ManaGain > 0) sb.Append(" 回蓝").Append(c.ManaGain);
                    sb.Append(" 类型=").Append(c.TypeId)
                      .Append(" 组=").Append(c.GroupId)
                      .Append(") ");
                }
                sb.Append("｜抽牌堆 ").Append(s.DrawCount).Append(" 张")
                  .Append("｜弃牌 ").Append(s.DiscardCount)
                  .Append("｜法力 ").Append(s.Mana.ToString("0.##"))
                  .Append("｜生命 ").Append(s.Hp.ToString("0"))
                  .Append("｜敌人 ").Append(s.EnemyCount)
                  .Append(" HP ").Append(s.EnemyHp.ToString("0"));
                AdvisorPlugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private string _lastPlanSig;

        /// <summary>当局势/手牌/法力发生实质变化时，把“出牌计划”写进日志，便于无截图核对算法。</summary>
        private void PlanLog()
        {
            var s = _snap;
            if (s == null || !s.Valid || s.Hand.Count == 0) return;
            try
            {
                var sig = new StringBuilder();
                sig.Append(s.Turn).Append('|').Append(s.Mana.ToString("0.##")).Append('|').Append(s.ReverseCombo ? 'R' : 'F').Append('|');
                foreach (var c in s.Hand) sig.Append(c.Key).Append(':').Append(c.Cost).Append(':').Append(c.InCombo ? '1' : '0').Append(':').Append(c.PlanOrder).Append(',');
                string key = sig.ToString();
                if (key == _lastPlanSig) return;
                _lastPlanSig = key;

                var sb = new StringBuilder();
                sb.Append("[计划] 回合 ").Append(s.TurnDisplay)
                  .Append("(原始").Append(s.Turn).Append(" 全局").Append(s.TotalTurn).Append(')')
                  .Append(" 法力 ").Append(s.Mana.ToString("0.##"))
                  .Append(" 连击方向=").Append(s.ReverseCombo ? "逆向递减" : "正向递增")
                  .Append(" 可出牌=").Append(s.CanPlayCard ? "是" : "否")
                  .Append(" 回合结束中=").Append(s.IsTurnEnding ? "是" : "否")
                  .Append(" 可打 ").Append(s.PlanCount).Append(" 张")
                  .Append(" 毛耗 ").Append(s.PlanManaUsed.ToString("0.##"))
                  .Append(" 回蓝 ").Append(s.PlanManaGain.ToString("0.##"))
                  .Append(" 剩余 ").Append(s.PlanRemaining.ToString("0.##"))
                  .Append(" ｜顺序：");
                bool first = true;
                foreach (var c in s.Hand)
                {
                    if (c.PlanOrder <= 0) continue;
                    if (!first) sb.Append(" → ");
                    first = false;
                    sb.Append(c.PlanOrder).Append('.').Append(c.Name)
                      .Append("(费").Append(c.Cost);
                    if (c.ManaGain > 0) sb.Append(" 回").Append(c.ManaGain);
                    sb.Append(" 分").Append(c.Score).Append(')');
                }
                if (first) sb.Append("（无）");

                sb.Append(" ｜备选：");
                first = true;
                foreach (var c in s.Hand)
                {
                    if (c.PlanOrder > 0) continue;
                    if (!first) sb.Append(" / ");
                    first = false;
                    sb.Append(c.Name).Append("(费").Append(c.Cost)
                      .Append(c.InCombo ? " 可续连击" : "").Append(" 分").Append(c.Score).Append(')');
                }
                if (first) sb.Append("（无）");

                AdvisorPlugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private string _lastEnemySig;

        /// <summary>敌人意图/预计伤害/我方增益变化时写入日志，便于无截图核对数据读取。</summary>
        private void EnemyLog(Snapshot s)
        {
            if (s == null || !s.Valid || !s.HasEnemy) return;
            try
            {
                var sig = new StringBuilder();
                sig.Append(s.Turn).Append('|');
                foreach (var e in s.Enemies)
                    sig.Append(e.Name).Append(':').Append(e.Hp.ToString("0")).Append(':')
                       .Append(e.IncomingDamage).Append(':').Append(string.Join(",", e.Intents.ToArray())).Append(';');
                foreach (var st in s.Stats) sig.Append(st.Name).Append(st.Value.ToString("0.#")).Append(',');
                string key = sig.ToString();
                if (key == _lastEnemySig) return;
                _lastEnemySig = key;

                var sb = new StringBuilder();
                sb.Append("[敌情] 回合 ").Append(s.Turn)
                  .Append(" 敌人 ").Append(s.Enemies.Count).Append(" 只")
                  .Append(" 本回合预计受伤 ").Append(s.IncomingDamage).Append("｜");
                foreach (var e in s.Enemies)
                {
                    sb.Append(e.Name).Append(" HP ").Append(e.Hp.ToString("0")).Append('/').Append(e.HpMax.ToString("0"));
                    if (e.Armor > 0.5f) sb.Append(" 甲").Append(e.Armor.ToString("0"));
                    if (e.Freeze > 0) sb.Append(" 冻结").Append(e.Freeze);
                    sb.Append(" [").Append(string.Join(" / ", e.Intents.ToArray())).Append("] ");
                }
                sb.Append("｜我方增益：");
                if (s.Stats.Count == 0) sb.Append("无");
                foreach (var st in s.Stats)
                    sb.Append(st.Name).Append(st.Value.ToString("+0.##;-0.##")).Append(' ');
                AdvisorPlugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private void RebuildLines()
        {
            _lines.Clear();
            var s = _snap;
            if (s == null) { Add(T("正在读取游戏状态…", "Reading game state..."), 4); return; }

            if (!s.Valid)
            {
                Add(s.Message, 4);
                return;
            }

            // 状态行
            var sb = new StringBuilder();
            sb.Append(T("回合 ", "Turn ")).Append(s.TurnDisplay >= 0 ? s.TurnDisplay : s.Turn);
            if (s.Combo > 0) sb.Append(T("  连击 ", "  Combo ")).Append(s.Combo);
            Add(sb.ToString(), 1);

            var sb2 = new StringBuilder();
            sb2.Append(T("法力 ", "Mana "));
            sb2.Append(s.Mana >= 0f ? s.Mana.ToString("0.##") : "?");
            if (s.Hp >= 0f) sb2.Append(T("   生命 ", "   HP ")).Append(s.Hp.ToString("0"));
            if (s.Armor > 0f) sb2.Append(T("   护甲 ", "   Armor ")).Append(s.Armor.ToString("0"));
            Add(sb2.ToString(), 1);

            if (s.HasEnemy)
            {
                Add(string.Format(T("敌人 {0} 只   血量 {1:0}/{2:0}   护甲 {3:0}",
                                    "Enemies {0}   HP {1:0}/{2:0}   Armor {3:0}"),
                                  s.EnemyCount, s.EnemyHp, s.EnemyHpMax, s.EnemyArmor), 1);
            }

            // 推荐
            if (s.Hand.Count > 0)
            {
                Add("", 2);
                if (s.PlanCount > 0)
                    Add(string.Format(T("★ 出牌顺序（本回合可打 {0} 张）", "★ Play order ({0} this turn)"), s.PlanCount), 0);
                else if (s.IsTurnEnding)
                    Add(T("★ 出牌顺序（本回合已结束）", "★ Play order (turn ended)"), 0);
                else if (!s.CanPlayCard)
                    Add(T("★ 出牌顺序（等待你的回合…）", "★ Play order (waiting for your turn)"), 0);
                else
                    Add(T("★ 出牌顺序（本回合法力不足）", "★ Play order (not enough mana)"), 0);

                if (s.Combo > 1 && s.NextComboCost >= 0)
                    Add(string.Format(T("   续连击需 {0} 费（{1}阶梯）", "   needs {0} cost to continue ({1})"),
                        s.NextComboCost, s.ReverseCombo ? T("递减", "down") : T("递增", "up")), 4);

                if (s.PlanCount > 0)
                {
                    int n = Math.Min(6, s.Hand.Count);
                    for (int i = 0; i < n; i++)
                    {
                        var c = s.Hand[i];
                        string mark = c.PlanOrder > 0 ? c.PlanOrder.ToString() : "·";
                        string costTxt = c.ManaGain > 0
                            ? string.Format("{0}{1}+{2}{3}", T("费", "c"), c.Cost, T("回", "m"), c.ManaGain)
                            : string.Format("{0}{1}", T("费", "c"), c.Cost);
                        string tag = "";
                        if (c.Wild) tag += T("  ★万能", "  ★Wild");
                        if (c.Breakable && c.CrackStage == 1) tag += T("  ⚠开裂", "  ⚠Cracked");
                        if (c.Breakable && c.CrackStage == 2) tag += T("  ✖已碎", "  ✖Broken");
                        string head = string.Format("{0}. {1}  [{2} · {3}]  {4}{5}",
                            mark, c.Name, CardDb.RoleName(c.Role), costTxt, c.Score, tag);
                        Add(head, i == 0 ? 3 : 2);
                        if (c.Reasons.Count > 0)
                            Add("    · " + string.Join("；", c.Reasons.ToArray()), 4);
                    }
                    if (s.Hand.Count > n)
                        Add(string.Format(T("… 还有 {0} 张未列出", "... {0} more"), s.Hand.Count - n), 4);

                    string tail = s.PlanManaGain > 0
                        ? string.Format(T("（含回蓝 +{0:0.##}）", " (incl. +{0:0.##} gain)"), s.PlanManaGain)
                        : "";
                    Add(string.Format(T("⇢ 计划消耗法力 {0:0.##}，剩余 {1:0.##}{2}", "-> uses {0:0.##} mana, {1:0.##} left{2}"),
                        s.PlanManaUsed, s.PlanRemaining, tail), 1);
                }
                else if (s.IsTurnEnding)
                    Add(T("⇢ 本回合已结束，等待下一回合恢复法力", "-> turn ended, waiting for next turn"), 1);
                else if (!s.CanPlayCard)
                    Add(T("⇢ 现在还不是你的出牌时机", "-> not your turn yet"), 1);
                else if (s.ManaKnown)
                    Add(T("⇢ 当前法力打不出任何手牌", "-> no card is affordable"), 1);
                else
                    Add(T("⇢ 暂无可出的牌", "-> nothing playable"), 1);
            }
            else if (s.Message.Length > 0)
            {
                Add(s.Message, 4);
            }
            else
            {
                Add(T("当前没有手牌", "No cards in hand"), 4);
            }

            if (!_showAdvanced) return;

            // 进阶面板
            Add("", 2);
            Add(T("── 进阶信息 ──", "-- Advanced --"), 1);

            Add(string.Format(T("牌堆统计：抽牌堆 {0} · 弃牌堆 {1} · 销毁 {2} · 连击堆 {3}",
                                "Piles: draw {0} · discard {1} · destroy {2} · combo {3}"),
                              s.DrawCount, s.DiscardCount, s.DestroyCount, s.ComboPileCount), 2);

            if (s.DrawComposition.Count > 0)
            {
                Add(T("抽牌堆剩余构成（前 8）：", "Draw pile composition (top 8):"), 1);
                int m = Math.Min(8, s.DrawComposition.Count);
                for (int i = 0; i < m; i++)
                {
                    var kv = s.DrawComposition[i];
                    float p = s.DrawCount > 0 ? (kv.Value * 100f / s.DrawCount) : 0f;
                    Add(string.Format("    {0} ×{1}   ({2:0.#}%)", kv.Key, kv.Value, p), 2);
                }
            }
        }

        private void Add(string text, int kind)
        {
            _lines.Add(new Line { Text = text, Kind = kind });
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            if (Event.current != null && Event.current.type != EventType.Layout) return;

            try
            {
                string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial Unicode MS" };
                foreach (var f in candidates)
                {
                    try
                    {
                        var ft = Font.CreateDynamicFontFromOSFont(f, 14);
                        if (ft == null) continue;
                        bool hasCjk = false;
                        try { hasCjk = ft.HasCharacter('打') && ft.HasCharacter('牌'); } catch { }
                        if (_font == null) _font = ft;
                        if (hasCjk) { _font = ft; _cjkOk = true; break; }
                    }
                    catch { }
                }
            }
            catch { }
            if (_font == null) _cjkOk = false;

            try
            {
                _bg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _bg.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.09f, 0.86f));
                _bg.Apply();
            }
            catch { }

            _panel = new GUIStyle();
            if (_bg != null) _panel.normal.background = _bg;

            _title = Make(15, new Color(0.98f, 0.93f, 0.72f), FontStyle.Bold);
            _sub = Make(12, new Color(0.70f, 0.75f, 0.85f), FontStyle.Normal);
            _row = Make(13, new Color(0.90f, 0.92f, 0.96f), FontStyle.Normal);
            _best = Make(14, new Color(0.55f, 0.95f, 0.60f), FontStyle.Bold);
            _hint = Make(12, new Color(0.72f, 0.76f, 0.82f), FontStyle.Normal);
            _btn = Make(12, new Color(0.95f, 0.95f, 0.95f), FontStyle.Normal);

            _stylesReady = true;

            if (!_loggedStyles)
            {
                _loggedStyles = true;
                try
                {
                    AdvisorPlugin.Logger.LogInfo(string.Format(
                        "[自检] OnGUI 样式就绪 字体={0} 中文字体={1} 屏幕={2}x{3}",
                        _font != null ? _font.name : "(null)", _cjkOk, Screen.width, Screen.height));
                }
                catch { }
            }
        }

        private bool _loggedStyles;

        private GUIStyle Make(int size, Color col, FontStyle fs)
        {
            var st = new GUIStyle();
            if (_font != null) st.font = _font;
            st.fontSize = size;
            st.fontStyle = fs;
            st.wordWrap = true;
            st.richText = false;
            st.normal.textColor = col;
            st.padding = new RectOffset(0, 0, 0, 0);
            return st;
        }

        private bool _loggedGui;
        private int _guiCalls;
        private bool _drawReached;
        private string _guiErr;

        void OnGUI()
        {
            _guiCalls++;
            if (!_loggedGui)
            {
                _loggedGui = true;
                try { AdvisorPlugin.Logger.LogInfo("[自检] OnGUI 已被调用（叠加层开始渲染）"); } catch { }
            }
            try
            {
                EnsureStyles();

                if (!_show)
                {
                    if (GUI.Button(new Rect(10f, 10f, 96f, 24f), T("打牌助手", "Advisor"), _btn))
                        _show = true;
                    return;
                }

                const float w = 400f;
                const float padX = 12f;
                float inner = w - padX * 2f;
                const float headH = 26f;
                const float lineH = 19f;
                const float titleW = 210f;

                // 估算高度
                float height = headH + 12f;
                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    if (l.Text.Length == 0) { height += 8f; continue; }
                    int perLine = Math.Max(12, (int)(inner / 8f));
                    int rows = Math.Max(1, (l.Text.Length + perLine - 1) / perLine);
                    height += rows * lineH;
                }
                height = Math.Min(height + 8f, Screen.height - 20f);

                var rect = new Rect(10f, 10f, w, height);
                _drawReached = true;

                // 背景框（仅存在 GUIContent+GUIStyle 重载）
                GUI.Box(rect, new GUIContent(""), _panel);

                float left = rect.x + padX;
                float right = rect.xMax - padX;
                float y = rect.y + 7f;

                // 标题行 + 两个小按钮（绝对坐标）
                GUI.Label(new Rect(left, y, titleW, 22f),
                    new GUIContent(T("打牌助手", "Crawler Advisor")), _title);
                if (GUI.Button(new Rect(right - 100f, y + 2f, 48f, 18f),
                        new GUIContent(_showAdvanced ? T("简化", "Less") : T("进阶", "More")), _btn))
                    _showAdvanced = !_showAdvanced;
                if (GUI.Button(new Rect(right - 48f, y + 2f, 48f, 18f),
                        new GUIContent(T("隐藏", "Hide")), _btn))
                    _show = false;
                y += headH;

                if (_fatal != null)
                {
                    GUI.Label(new Rect(left, y, inner, lineH), new GUIContent(T("读取异常：", "Error: ") + _fatal), _hint);
                    return;
                }

                for (int i = 0; i < _lines.Count; i++)
                {
                    var l = _lines[i];
                    if (l.Text.Length == 0) { y += 8f; continue; }

                    GUIStyle st;
                    switch (l.Kind)
                    {
                        case 0: st = _title; break;
                        case 1: st = _sub; break;
                        case 3: st = _best; break;
                        case 4: st = _hint; break;
                        default: st = _row; break;
                    }

                    int perLine = Math.Max(12, (int)(inner / 8f));
                    int rows = Math.Max(1, (l.Text.Length + perLine - 1) / perLine);
                    float h = rows * lineH;
                    GUI.Label(new Rect(left, y, inner, h), new GUIContent(l.Text), st);
                    y += h;
                }
            }
            catch (Exception e)
            {
                if (_guiErr == null)
                {
                    _guiErr = e.GetType().Name + ": " + e.Message;
                    try { AdvisorPlugin.Logger.LogError("[自检] OnGUI 绘制异常: " + _guiErr); } catch { }
                }
            }
        }
    }
}
