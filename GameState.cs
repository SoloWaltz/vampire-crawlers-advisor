using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Nosebleed.Pancake.GameCommands;
using Nosebleed.Pancake.GameLogic;
using Nosebleed.Pancake.Models;
using UnityEngine;
using GameCardConfig = Nosebleed.Pancake.GameConfig.CardConfig;

namespace VcAdvisor
{
    public class CardInfo
    {
        public string Key;            // CardConfig.AssetId，如 MAGIC_WAND
        public string Name;           // 中文名
        public int Cost;              // 当前实际费用
        public string TypeId;         // 游戏内卡牌类型 id
        public string GroupId;        // 卡牌分组 id
        public bool IsWeapon;
        public CardRole Role;         // 归一化后的打法分类
        public int ManaGain;          // 打出后回复的法力（0 = 无回蓝）
        public bool Playable;         // 当前法力是否够
        public bool InCombo;          // 游戏判定：打出它能否接上当前连击（PlayerModel.IsCardInCombo）
        public int PlanOrder;         // 本回合计划中的出牌次序（0 = 不在计划内）
        public int Score;

        // —— 特殊卡与费用对照（用于展示与“费用主路径是否切换”的评估）——
        public bool Wild;             // 万能牌：费用类型为 WildCostType，可当任意费用续连击
        public bool Breakable;        // 碎裂卡
        public int CrackStage = -1;   // 0 完好 / 1 开裂 / 2 破碎（-1 = 非碎裂卡）
        public int TimesPlayedThisTurn;
        public bool FreeToPlay;       // 游戏判定“本次免费打出”
        public List<string> Reasons = new List<string>();
    }

    /// <summary>单个敌人的可读信息（含本回合意图与预计伤害）。</summary>
    public class EnemyInfo
    {
        public string Name = "敌人";
        public float Hp;
        public float HpMax;
        public float Armor;
        public bool Dead;
        public bool Targetable = true;
        public int Freeze;
        public int IncomingDamage;                    // 本回合对玩家造成的预计伤害
        public List<string> Intents = new List<string>();
    }

    /// <summary>我方的一项增益/减益（来自 PlayerStats 的各 SimpleStat）。</summary>
    public class StatInfo
    {
        public string Name;
        public float Value;
        public bool Good = true;   // true=增益 false=减益
    }

    public class Snapshot
    {
        public bool Valid;
        public string Message = "";

        public bool InEncounter;
        public int Turn;                          // 游戏原始值（0 基，首回合为 0）
        public int TurnDisplay = -1;              // 展示用回合数（游戏值 +1；未知时为 -1）
        public int TotalTurn = -1;                // 全局回合计数（诊断用）
        public int NextComboCost = -1;            // 连击阶梯上“下一张”应有的费用（-1 = 未知）
        public int Combo;
        public bool ReverseCombo;         // 逆向连击（费用递减）
        public bool CanPlayCard = true;   // 游戏判定“现在能不能出牌”
        public bool IsTurnEnding;         // 本回合正在结束/结算中

        public float Mana = -1f;
        public int CachedMana = -1;
        public float ManaPeak;

        // 本回合的出牌计划（由 Scoring 计算）
        public bool ManaKnown;
        public int PlanCount;         // 计划内可打出的张数
        public float PlanManaUsed;    // 计划消耗的法力（毛消耗）
        public float PlanManaGain;    // 计划中法力牌回复的法力
        public float PlanRemaining;   // 计划执行后剩余法力（含回蓝）

        public float Hp = -1f;
        public float HpPeak;
        public float HpMax = -1f;      // 游戏给出的生命上限（SimpleStat.GetMaxValue）；-1 = 未取到
        public float ManaMax = -1f;    // 游戏给出的法力上限；-1 = 未取到
        public float Armor = -1f;

        public List<CardInfo> Hand = new List<CardInfo>();

        public int DrawCount;
        public int DiscardCount;
        public int DestroyCount;
        public int ComboPileCount;
        public List<KeyValuePair<string, int>> DrawComposition = new List<KeyValuePair<string, int>>();

        public bool HasEnemy;
        public float EnemyHp;
        public float EnemyHpMax;
        public float EnemyArmor;
        public int EnemyCount;

        public List<EnemyInfo> Enemies = new List<EnemyInfo>();
        public int IncomingDamage;      // 本回合预计受到的伤害合计
        public List<StatInfo> Stats = new List<StatInfo>();   // 我方增益/减益
    }

    /// <summary>
    /// 只读读取游戏运行时状态。严禁写入任何字段。
    /// </summary>
    public static class GameState
    {
        private static float _hpPeak;
        private static float _manaPeak;

        public static float HpPeak { get { return _hpPeak; } }
        public static float ManaPeak { get { return _manaPeak; } }

        public static void ResetPeaks()
        {
            _hpPeak = 0f;
            _manaPeak = 0f;
        }

        private static T FindOne<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectOfType<T>(); }
            catch { return null; }
        }

        public static Snapshot Read()
        {
            var s = new Snapshot();
            try
            {
                var player = FindOne<PlayerModel>();
                if (player == null)
                {
                    s.Message = "未找到玩家对象（请先进入一局对战）";
                    return s;
                }
                s.Valid = true;

                try { s.InEncounter = player.IsInEncounter; } catch { }
                try { s.Turn = player.EncounterTurnCount; } catch { }
                try { if (player.TotalTurnCount != null) s.TotalTurn = player.TotalTurnCount.Value; } catch { }
                try { s.Combo = player.ComboMultiplier; } catch { }
                try { s.ReverseCombo = player.ReverseCombo; } catch { }
                try { s.CachedMana = player.CachedMana; } catch { }
                try { s.CanPlayCard = player.CanPlayCard; } catch { }
                try { s.IsTurnEnding = player.IsTurnEnding; } catch { }

                // 回合展示值：游戏内部按 0 基计数（首回合 = 0），+1 后与游戏界面体感一致
                s.TurnDisplay = s.Turn + 1;

                // 连击阶梯：游戏把“上一张连击牌的费用”放在 PreviousComboCardCost，
                // 正向连击下一张必须 +1 费，逆向（ReverseCombo）则 -1 费。
                // 这是阶梯的唯一权威来源，比逐张试探 IsCardInCombo 更直接。
                try
                {
                    var prevCost = player.PreviousComboCardCost;
                    if (prevCost != null)
                    {
                        int v = prevCost.Value;
                        if (v >= 0) s.NextComboCost = s.ReverseCombo ? v - 1 : v + 1;
                    }
                }
                catch { }

                // 玩家数值
                try
                {
                    var st = player.PlayerStats;
                    if (st != null)
                    {
                        if (st.Mana != null) s.Mana = st.Mana.Value;
                        if (st.Health != null) s.Hp = st.Health.Value;
                        if (st.Armor != null) s.Armor = st.Armor.Value;
                        // 上限由游戏自己给出，比跨局累计的峰值更适合做比例分母（进度条）
                        try { if (st.Health != null) s.HpMax = st.Health.GetMaxValue(); } catch { }
                        try { if (st.Mana != null) s.ManaMax = st.Mana.GetMaxValue(); } catch { }
                        DiagStatsOnce(st, s);
                    }
                }
                catch { }

                if (s.Hp > _hpPeak) _hpPeak = s.Hp;
                if (s.Mana > _manaPeak) _manaPeak = s.Mana;
                s.HpPeak = _hpPeak;
                s.ManaPeak = _manaPeak;

                // 手牌
                try
                {
                    var handPile = player.HandPile;
                    var pile = handPile != null ? handPile.CardPile : null;
                    var cards = pile != null ? pile._cards : null;
                    if (cards != null)
                    {
                        for (int i = 0; i < cards.Count; i++)
                        {
                            var ci = ReadCard(cards[i]);
                            if (ci == null) continue;
                            // 直接问游戏：这张牌能否接上当前连击（连击判定的唯一权威来源）
                            try { ci.InCombo = player.IsCardInCombo(cards[i].TryCast<ICardModel>(), false); } catch { }
                            s.Hand.Add(ci);
                        }
                    }
                }
                catch { }

                // 抽牌堆构成
                try
                {
                    var draw = player.DrawPile;
                    var pile = draw != null ? draw.CardPileModel : null;
                    var dcards = pile != null ? pile._cards : null;
                    if (dcards != null)
                    {
                        s.DrawCount = dcards.Count;
                        var dict = new Dictionary<string, int>();
                        for (int i = 0; i < dcards.Count; i++)
                        {
                            var ci = ReadCard(dcards[i]);
                            if (ci == null) continue;
                            int c;
                            dict.TryGetValue(ci.Name, out c);
                            dict[ci.Name] = c + 1;
                        }
                        foreach (var kv in dict) s.DrawComposition.Add(kv);
                        s.DrawComposition.Sort((a, b) => b.Value.CompareTo(a.Value));
                    }
                }
                catch { }

                try
                {
                    var dis = player.DiscardPile;
                    var pile = dis != null ? dis.CardPile : null;
                    if (pile != null && pile._cards != null) s.DiscardCount = pile._cards.Count;
                }
                catch { }
                try
                {
                    var des = player.DestroyPile;
                    var pile = des != null ? des.CardPile : null;
                    if (pile != null && pile._cards != null) s.DestroyCount = pile._cards.Count;
                }
                catch { }
                try
                {
                    var cp = player.ComboPile;
                    var pile = cp != null ? cp.CardPile : null;
                    if (pile != null && pile._cards != null) s.ComboPileCount = pile._cards.Count;
                }
                catch { }

                ReadEnemy(s, player);
                ReadStats(s, player);

                if (!s.InEncounter && s.Hand.Count == 0)
                    s.Message = "当前不在战斗中，暂无手牌";
            }
            catch (Exception e)
            {
                s.Valid = false;
                s.Message = "读取失败: " + e.Message;
            }
            return s;
        }

        private static void ReadEnemy(Snapshot s, PlayerModel player)
        {
            try
            {
                var enc = FindOne<EnemyEncounterModel>();
                if (enc != null)
                {
                    s.HasEnemy = true;
                    s.EnemyHp = (float)enc._currentHealth;
                    s.EnemyHpMax = (float)enc._maxHealth;
                    s.EnemyArmor = (float)enc._currentArmor;
                    var alive = enc._aliveEnemies;
                    s.EnemyCount = alive != null ? alive.Count : 0;
                    ReadEnemyList(s, alive, player, s.EnemyCount);
                    return;
                }
            }
            catch { }

            try
            {
                var g = FindOne<GuardianEncounterModel>();
                if (g != null)
                {
                    s.HasEnemy = true;
                    s.EnemyHp = (float)g._currentHealth;
                    s.EnemyHpMax = (float)g._maxHealth;
                    s.EnemyArmor = (float)g._currentArmor;
                    var alive = g._aliveGuardians;
                    s.EnemyCount = alive != null ? alive.Count : 1;
                    ReadEnemyList(s, alive, player, s.EnemyCount);
                }
            }
            catch { }
        }

        /// <summary>逐个读取敌人血量/护甲/冻结与“本回合意图 + 预计伤害”。</summary>
        private static void ReadEnemyList(Snapshot s, Il2CppSystem.Collections.Generic.List<EnemyModel> alive,
            PlayerModel player, int count)
        {
            if (alive == null) return;
            int total = 0;
            for (int i = 0; i < alive.Count; i++)
            {
                EnemyModel em = null;
                try { em = alive[i]; } catch { }
                if (em == null) continue;

                var info = new EnemyInfo();
                try { info.Name = CleanEnemyName(em.IntentHolderName); } catch { }
                if (string.IsNullOrEmpty(info.Name)) info.Name = "敌人" + (i + 1);
                try { info.Hp = (float)em.Health; } catch { }
                try { info.HpMax = (float)em.MaxHealth; } catch { }
                try { info.Armor = (float)em.Armor; } catch { }
                try { info.Dead = em.IsDead; } catch { }
                try { info.Targetable = em.IsTargetable; } catch { }
                try { info.Freeze = em.FreezeStacks; } catch { }

                // 意图 → 每条的预计伤害
                try
                {
                    var intent = em.CurrentIntent;
                    var acts = intent != null ? intent.actions : null;
                    if (acts != null)
                    {
                        for (int a = 0; a < acts.Length; a++)
                        {
                            var act = acts[a];
                            if (act == null) continue;
                            int dmg = 0;
                            string label = IntentLabel(act, em, player, out dmg);
                            info.IncomingDamage += dmg;
                            if (!string.IsNullOrEmpty(label)) info.Intents.Add(label);
                        }
                    }
                }
                catch { }

                total += info.IncomingDamage;
                s.Enemies.Add(info);
            }
            s.IncomingDamage = total;
        }

        /// <summary>把游戏内部的 IntentHolderName（如 "Bat : Row 2 - 3"）清理成可读名称。</summary>
        private static string CleanEnemyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string n = raw;
            int c = n.IndexOf(':');
            if (c > 0) n = n.Substring(0, c);
            n = n.Trim();
            switch (n)
            {
                case "Bat": return "蝙蝠";
                case "Bat_Elite": return "精英蝙蝠";
                case "Rat": return "老鼠";
                case "Rat_Elite": return "精英老鼠";
                case "Zombie": return "僵尸";
                case "Zombie_Elite": return "精英僵尸";
                case "Ghost": return "幽灵";
                case "Ghost_Elite": return "精英幽灵";
                case "Skeleton": return "骷髅";
                case "Skeleton_Elite": return "精英骷髅";
                case "Slime": return "史莱姆";
                case "Slime_Elite": return "精英史莱姆";
                case "Wolf": return "狼";
                case "Wolf_Elite": return "精英狼";
                case "Boss": return "首领";
            }
            return n;
        }

        /// <summary>把一个敌人动作翻译成便于阅读的意图文本，并给出其预计伤害。</summary>
        private static string IntentLabel(EnemyAction act, EnemyModel em, PlayerModel player, out int damage)
        {
            damage = 0;
            try
            {
                var d = act.TryCast<EnemyActionDamage>();
                if (d != null)
                {
                    try { damage = d.GetProjectedDamage(em, player); } catch { }
                    if (damage <= 0) { try { damage = d.GetValue(player, 1f); } catch { } }
                    return "攻击 " + damage;
                }

                var md = act.TryCast<EnemyActionMultiDamage>();
                if (md != null)
                {
                    try { damage = md.GetProjectedDamage(em, player); } catch { }
                    if (damage <= 0) { try { damage = md.GetValue(player, 1f); } catch { } }
                    return "连击 " + damage;
                }

                var ar = act.TryCast<EnemyActionArmor>();
                if (ar != null)
                {
                    int v = 0;
                    try { v = ar.GetValue(player, 1f); } catch { }
                    return "格挡 +" + v;
                }

                if (act.TryCast<EnemyActionAddPlayerStat>() != null) return "施加状态";
                if (act.TryCast<EnemyActionSpawnNewEnemy>() != null) return "召唤";
                if (act.TryCast<EnemyActionAddCardToPile>() != null) return "塞牌";
                if (act.TryCast<EnemyActionWait>() != null) return "蓄力";

                try { return act.GetType().Name.Replace("EnemyAction", ""); } catch { }
            }
            catch { }
            return "未知";
        }

        /// <summary>读取我方增益/减益（PlayerStats 里非默认的 SimpleStat）。</summary>
        private static void ReadStats(Snapshot s, PlayerModel player)
        {
            try
            {
                var st = player.PlayerStats;
                if (st == null) return;

                AddStat(s, "力量", st.Might);
                AddStat(s, "数量", st.Amount);
                AddStat(s, "范围", st.Area);
                AddStat(s, "成长", st.Growth);
                AddStat(s, "贪婪", st.Greed);
                AddStat(s, "幸运", st.Luck);
                AddStat(s, "回复", st.Recovery);
                AddStat(s, "磁力", st.Magnet);
                AddStat(s, "持续", st.Duration);
                AddStat(s, "复活充能", st.RevivalCharge);
                AddStat(s, "复活次数", st.RevivalStacks);
                AddStat(s, "跳过", st.CardSkips);
                AddStat(s, "重掷", st.Rerolls);
                AddStat(s, "放逐", st.Banishes);
                AddStat(s, "拾荒", st.Scavenge);
                AddStat(s, "魅惑", st.Charm);
                AddStatBad(s, "诅咒", st.Curse);
                AddStatBad(s, "风险", st.Risk);
            }
            catch { }
        }

        private static bool _statsDiaged;

        /// <summary>
        /// 记录一次生命 / 法力的原始读数（当前值、上限、下限）。
        /// 用于核对「游戏界面显示的生命值」与「工具读到的 Health.Value」是否一致。
        /// </summary>
        private static void DiagStatsOnce(PlayerStats st, Snapshot s)
        {
            if (_statsDiaged) return;
            _statsDiaged = true;
            try
            {
                float min = -1f, max = -1f;
                try { if (st.Health != null) min = st.Health.Min.Value; } catch { }
                try { if (st.Health != null) max = st.Health.Max.Value; } catch { }
                AdvisorPlugin.Logger.LogInfo(string.Format(
                    "[数值] 生命 当前={0} 上限(GetMaxValue)={1} Min={2} Max={3} ｜法力 当前={4} 上限={5}",
                    s.Hp, s.HpMax, min, max, s.Mana, s.ManaMax));
            }
            catch { }
        }

        private static void AddStat(Snapshot s, string name, SimpleStat stat)
        {
            try
            {
                if (stat == null) return;
                float v = stat.Value;
                if (Math.Abs(v) < 0.001f) return;
                s.Stats.Add(new StatInfo { Name = name, Value = v, Good = true });
            }
            catch { }
        }

        private static void AddStatBad(Snapshot s, string name, SimpleStat stat)
        {
            try
            {
                if (stat == null) return;
                float v = stat.Value;
                if (Math.Abs(v) < 0.001f) return;
                s.Stats.Add(new StatInfo { Name = name, Value = v, Good = false });
            }
            catch { }
        }

        private static readonly Dictionary<string, string> _nameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _nameDiag = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 卡名解析：优先向游戏自身索取名称（CardConfig.cardName 的本地化文本 → CardModel.Name），
        /// 保证与游戏界面逐字一致；两条途径都拿不到时才退回内嵌 cards.tsv。
        /// </summary>
        private static string ResolveName(CardModel cm, GameCardConfig cfg, string assetId)
        {
            string cached;
            if (!string.IsNullOrEmpty(assetId) && _nameCache.TryGetValue(assetId, out cached)) return cached;

            string loc = null, model = null;
            try { if (cfg != null && cfg.cardName != null) loc = cfg.cardName.GetLocalizedString(); } catch { }
            try { if (cm != null) model = cm.Name; } catch { }

            bool fromGame = !string.IsNullOrEmpty(loc) || !string.IsNullOrEmpty(model);
            string pick = !string.IsNullOrEmpty(loc) ? loc : model;
            if (string.IsNullOrEmpty(pick) || pick.StartsWith("Card_", StringComparison.Ordinal))
                pick = CardDb.Name(assetId);

            // 诊断：每个 AssetId 只记一次，便于核对「本地化名 / 模型名 / 内嵌表名」的差异
            if (!string.IsNullOrEmpty(assetId) && _nameDiag.Add(assetId))
            {
                try
                {
                    AdvisorPlugin.Logger.LogInfo(string.Format(
                        "[卡名] {0} ｜本地化={1} ｜模型名={2} ｜内嵌表={3}",
                        assetId, loc ?? "(空)", model ?? "(空)", CardDb.Name(assetId)));
                }
                catch { }
            }

            // 只有拿到游戏提供的名字才缓存，避免本地化尚未就绪时把回退值钉死
            if (fromGame && !string.IsNullOrEmpty(assetId)) _nameCache[assetId] = pick;
            return pick;
        }

        public static CardInfo ReadCard(CardModel cm)
        {
            if (cm == null) return null;
            var ci = new CardInfo();
            try
            {
                GameCardConfig cfg = null;
                try { cfg = cm.CardConfig; } catch { }

                ci.Key = cfg != null ? cfg.AssetId : null;
                ci.Name = ResolveName(cm, cfg, ci.Key);

                int cost = 0;
                if (cfg != null)
                {
                    try { cost = cfg.manaCost; } catch { }
                }
                try { cost += cm.ManaCostModifier; } catch { }
                try { cost += cm.TempManaCostModifier; } catch { }
                try { cost += cm.ConfuseManaCostModifier; } catch { }
                try { cost -= cm.ReducedCostModifier; } catch { }
                try { if (cm.IsTemporaryFree) cost = 0; } catch { }
                if (cost < 0) cost = 0;
                ci.Cost = cost;

                // 费用一律以「配置基础费用 + 各项修正」手工累加为准。
                // 游戏原生接口 GetCardCostTypeManaCost / GetCardComboCost 返回的并不是「当前手牌实际费用」：
                // 实测 烛台(S_2)=0、时钟柳叶刀(D_3)=3、轻薄之书(B_1)=0，与官方 CardDatabase.json 的 2/2/1 不符。
                // 因此这里不再参考原生费用值，避免被误导。仅保留 FreeToPlay 用于展示。
                try { ci.FreeToPlay = cm.IsCardFreeToPlay(); } catch { }

                try { if (cfg != null && cfg.cardType != null) ci.TypeId = cfg.cardType.typeId; } catch { }
                try { if (cfg != null && cfg.cardGroup != null) ci.IsWeapon = cfg.cardGroup.IsWeapon; } catch { }
                try { ci.GroupId = cm.GetCardGroupAssetID(); } catch { }

                // 特殊卡：碎裂卡（有剩余可打次数）与万能牌（费用类型可当任意费用）
                try
                {
                    var bc = cm.BreakableCard;
                    if (bc != null)
                    {
                        ci.Breakable = true;
                        try { ci.CrackStage = (int)bc.CrackState; } catch { }
                        try { ci.TimesPlayedThisTurn = bc.TimesPlayedThisTurn; } catch { }
                    }
                }
                catch { }
                try
                {
                    var ct = cm.CardCostType;
                    if (ct != null && ct.TryCast<WildCostType>() != null) ci.Wild = true;
                }
                catch { }

                try { ci.ManaGain = ReadManaGain(cfg, ci.Cost); } catch { }
            }
            catch { }
            return ci;
        }

        /// <summary>
        /// 读取“打出该牌回复多少法力”：遍历 OnPlayEffects，累计所有回蓝效果。
        /// 除固定回蓝（GainManaEffect）外，还包含“回复等于费用”的效果
        /// （GainManaEqualToCostEffect，打出后净消耗为 0，必须计入回蓝连锁）。
        /// 只读，任何异常一律按 0 处理（不影响主流程）。
        /// </summary>
        private static int ReadManaGain(GameCardConfig cfg, int cost)
        {
            if (cfg == null) return 0;
            int gain = 0;
            try
            {
                var arr = cfg.onPlayEffect;
                if (arr == null) return 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    var e = arr[i];
                    if (e == null) continue;
                    try
                    {
                        var gm = e.TryCast<GainManaEffect>();
                        if (gm != null)
                        {
                            try { gain += gm._gainAmount; } catch { }
                            continue;
                        }
                        if (e.TryCast<GainManaEqualToCostEffect>() != null) gain += cost;
                    }
                    catch { }
                }
            }
            catch { }
            return gain;
        }
    }
}
