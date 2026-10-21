using System;
using System.Collections.Generic;

namespace VcAdvisor
{
    /// <summary>
    /// 出牌优先级引擎（只读，不修改任何游戏数值）：
    /// 1) 先给每张手牌按 分类 + 局面 打分；
    /// 2) 再在“当前法力预算”内贪心排出一条本回合真正可执行的出牌序列。
    /// </summary>
    public static class Scoring
    {
        public static void Evaluate(Snapshot s)
        {
            if (s == null || !s.Valid) return;

            // —— 本回合法力 ——
            float mana = s.Mana;
            bool manaKnown = mana >= 0f;
            if (!manaKnown && s.CachedMana >= 0) { mana = s.CachedMana; manaKnown = true; }
            if (!manaKnown) mana = 0f;
            s.ManaKnown = manaKnown;

            // 没有手牌时不编排顺序，但仍要给出法力读数与计划归零，
            // 这样伴侣窗口能据此显示“等待你的回合”等状态，而不是残留上一回合的推荐。
            if (s.Hand.Count == 0)
            {
                s.PlanCount = 0;
                s.PlanManaUsed = 0f;
                s.PlanManaGain = 0f;
                s.PlanRemaining = 0f;
                return;
            }

            // —— 局面判定 ——
            // 生命比例优先用游戏给出的上限（HpMax），取不到或明显异常时才退回跨局累计的峰值
            float hpDenom = (s.HpMax > 0f && s.HpMax < 100000f) ? s.HpMax : s.HpPeak;
            bool hpKnown = s.Hp >= 0f && hpDenom > 0f;
            bool lowHp = hpKnown && s.Hp <= hpDenom * 0.35f;
            bool multiEnemy = s.EnemyCount >= 3;
            bool manyEnemy = s.EnemyCount >= 5;
            bool tightMana = manaKnown && mana <= 4f;

            // 敌方本回合预计伤害（用于防御牌排序）
            float inc = s.IncomingDamage;
            float incRatio = hpKnown && s.Hp > 0.5f ? inc / s.Hp : 0f;
            bool bigHit = incRatio >= 0.30f;
            bool lethalHit = incRatio >= 0.55f;

            // —— 第一轮：逐张打分 ——
            foreach (var c in s.Hand)
            {
                if (c == null) continue;
                c.Reasons.Clear();
                c.PlanOrder = 0;

                // 分类：能回蓝的一律视为法力牌 → 人工表 → 游戏自身 typeId → 武器标记
                var typeRole = CardDb.RoleFromType(c.TypeId);
                var role = CardDb.RoleOf(c.Key);
                if (c.ManaGain > 0) role = CardRole.Mana;
                else if (role == CardRole.Unknown) role = typeRole;
                if (role == CardRole.Unknown && c.IsWeapon) role = CardRole.Weapon;
                if (role == CardRole.Unknown && c.Wild) role = CardRole.Utility;
                // 负面牌以游戏自身类型为准，优先于人工分类表：
                // 这类牌是被塞进牌库的惩罚牌，打出只会吃亏，绝不能进推荐
                if (typeRole == CardRole.Debuff) role = CardRole.Debuff;
                c.Role = role;

                int score;
                switch (role)
                {
                    case CardRole.Mana:
                        score = 95; c.Reasons.Add("提供法力，越早打出越能滚雪球"); break;
                    case CardRole.Weapon:
                        score = 72; c.Reasons.Add("武器/持续输出，早铺早收益"); break;
                    case CardRole.Economy:
                        score = 66; c.Reasons.Add("经济类，前期收益更高"); break;
                    case CardRole.Utility:
                        score = 58; c.Reasons.Add("功能卡"); break;
                    case CardRole.Passive:
                        score = 55; c.Reasons.Add("被动增益"); break;
                    case CardRole.Defense:
                        score = 42; c.Reasons.Add("防御卡"); break;
                    case CardRole.Character:
                        score = 30; c.Reasons.Add("角色卡，战斗优先度低"); break;
                    case CardRole.Debuff:
                        score = -120; c.Reasons.Add("负面牌（被塞入牌库），打出只会吃亏，除非别无选择不要打"); break;
                    default:
                        score = 50; c.Reasons.Add("未识别类型，按费用评估"); break;
                }

                // —— 局面修正 ——
                if (role == CardRole.Defense)
                {
                    if (lowHp)
                    {
                        score += 55;
                        c.Reasons.Add(string.Format("生命偏低({0:0}/{1:0})，优先保命", s.Hp, hpDenom));
                    }
                    if (manyEnemy) { score += 10; c.Reasons.Add("敌人密集，护甲收益高"); }

                    // 敌人本回合的预计伤害越高，格挡/回复越关键
                    if (inc > 0)
                    {
                        if (lethalHit)
                        {
                            score += 60;
                            c.Reasons.Add(string.Format("本回合预计受到 {0} 伤害（约生命 {1:0}%），优先格挡保命",
                                        inc, incRatio * 100f));
                        }
                        else if (bigHit)
                        {
                            score += 30;
                            c.Reasons.Add(string.Format("本回合预计受到 {0} 伤害，建议格挡", inc));
                        }
                        else
                        {
                            score += 8;
                            c.Reasons.Add(string.Format("本回合预计受到 {0} 伤害", inc));
                        }
                    }
                }

                if (role == CardRole.Mana && tightMana)
                {
                    score += 35;
                    c.Reasons.Add("法力吃紧，优先补法力");
                }

                if (role == CardRole.Weapon)
                {
                    if (multiEnemy) { score += 15; c.Reasons.Add(string.Format("场上敌人较多({0})", s.EnemyCount)); }
                    if (s.Hand.Count >= 6) score += 5;
                    if (s.Combo > 1) { score += 8; c.Reasons.Add(string.Format("连击 x{0}，攻击收益放大", s.Combo)); }
                }

                if (s.TurnEffective <= 1 && (role == CardRole.Passive || role == CardRole.Economy))
                {
                    score += 12;
                    c.Reasons.Add("开局第 1 回合，优先铺场");
                }

                if (tightMana && c.Cost <= 2 && role != CardRole.Debuff)
                {
                    score += 6;
                    c.Reasons.Add("低费，便于串联");
                }

                // —— 回蓝：把“净消耗”纳入价值，越能回本越优先 ——
                if (c.ManaGain > 0)
                {
                    int net = c.Cost - c.ManaGain;
                    c.Reasons.Add(net <= 0
                        ? string.Format("回蓝 {0}，净消耗 {1}（近似免费）", c.ManaGain, net)
                        : string.Format("回蓝 {0}，净消耗 {1}", c.ManaGain, net));
                    if (net <= 0) score += 20;
                    else score += 8;
                }

                // —— 免费牌（0 费）：不占预算的纯收益，尽早打出（负面牌不享受此加成）——
                if (c.Cost == 0 && role != CardRole.Mana && role != CardRole.Debuff)
                {
                    score += 16;
                    c.Reasons.Add("0 费，可无成本串联");
                }

                // —— 法力利用 ——
                c.Playable = !manaKnown || c.Cost <= mana + 0.001f;
                if (c.Playable)
                {
                    score += Math.Min(24, c.Cost * 6);
                }
                else
                {
                    // 不把“当前打不出”折算进分值：分值必须真实反映牌本身的价值，
                    // 否则“先打回蓝牌 → 法力够了再打大牌”的连锁会因负分被漏掉。
                    c.Reasons.Add(string.Format("本回合法力不足（需 {0}，当前 {1:0.##}）", c.Cost, mana));
                }

                // —— 特殊卡（状态直接取自游戏，只读）——
                if (c.Wild)
                {
                    score += 10;
                    c.Reasons.Add("万能牌：费用可当任意值，续连击时最灵活");
                }
                if (c.Breakable)
                {
                    if (c.CrackStage == 2)
                    {
                        score -= 40;
                        c.Reasons.Add("已碎裂：本回合再打没有收益");
                    }
                    else
                    {
                        if (c.CrackStage == 1) c.Reasons.Add("已开裂，可打次数有限");
                        if (c.TimesPlayedThisTurn > 0)
                            c.Reasons.Add(string.Format("本回合已打 {0} 次", c.TimesPlayedThisTurn));
                    }
                }
                if (c.FreeToPlay) c.Reasons.Add("游戏判定本次免费打出");

                c.Score = score;
            }

            // —— 第二轮：本回合出牌计划 ——
            BuildPlan(s, mana, manaKnown);

            // 计划内的排前面（按出牌次序），其余按分数降序
            s.Hand.Sort((a, b) =>
            {
                int pa = a.PlanOrder == 0 ? int.MaxValue : a.PlanOrder;
                int pb = b.PlanOrder == 0 ? int.MaxValue : b.PlanOrder;
                if (pa != pb) return pa.CompareTo(pb);
                // 都不在计划内：本回合打得出的排前面，再按分值降序
                if (a.Playable != b.Playable) return a.Playable ? -1 : 1;
                return b.Score.CompareTo(a.Score);
            });
        }

        /// <summary>
        /// 编排本回合出牌顺序。
        ///
        /// 连击规则由游戏本身决定，这里不再自己猜“单调”，而是：
        /// 1) 先用 PlayerModel.IsCardInCombo 问游戏：手上哪些牌能「接上当前连击」。
        ///    只要存在这样的牌，首张就必须从它们里选，否则连击当场断掉
        ///    （这正是「2 费打完却被建议打 0 费」的根因：旧算法只看费用单调，完全不知道连击走到哪了）。
        /// 2) 之后按连击阶梯逐张续接：下一张费用必须比上一张正好 +1（逆向连击则 -1）。
        /// 3) 如果当前没有能续连击的牌，就从头起一段新连击：枚举起点费用，模拟阶梯取总分最高的一段。
        /// </summary>
        private static void BuildPlan(Snapshot s, float mana, bool manaKnown)
        {
            s.PlanCount = 0;
            s.PlanManaUsed = 0f;
            s.PlanManaGain = 0f;
            s.PlanRemaining = 0f;

            if (!manaKnown || s.Hand.Count == 0) return; // 法力未知时不编排顺序

            var order = BuildComboOrder(s, mana);
            if (order.Count == 0) return;

            float used = 0f, gained = 0f;
            for (int k = 0; k < order.Count; k++)
            {
                var c = order[k];
                c.PlanOrder = k + 1;
                used += c.Cost;
                gained += c.ManaGain;
            }

            s.PlanCount = order.Count;
            s.PlanManaUsed = used;
            s.PlanManaGain = gained;
            s.PlanRemaining = Math.Max(0f, mana + gained - used);
        }

        /// <summary>连击方向：正向每张 +1 费，逆向（ReverseCombo）每张 -1 费。</summary>
        private static List<CardInfo> BuildComboOrder(Snapshot s, float mana)
        {
            int step = s.ReverseCombo ? -1 : 1;
            var best = new List<CardInfo>();
            int bestScore = int.MinValue;

            // —— 优先续上当前连击：首张必须是游戏认可的连击牌 ——
            bool anyContinuation = false;
            foreach (var c in s.Hand) if (c.InCombo) { anyContinuation = true; break; }

            if (anyContinuation)
            {
                foreach (var first in s.Hand)
                {
                    if (!first.InCombo) continue;
                    if (first.Cost > mana + 0.001f) continue;
                    int sc;
                    var seq = ChainFrom(s, first, mana, step, out sc);
                    if (sc > bestScore) { bestScore = sc; best = seq; }
                }
                return best;
            }

            // —— 连击已断：从每个可能的起点费用各模拟一段阶梯，取总分最高 ——
            var costs = new List<int>();
            foreach (var c in s.Hand)
                if (c.Cost <= mana + 0.001f && !costs.Contains(c.Cost)) costs.Add(c.Cost);

            foreach (var k in costs)
            {
                CardInfo start = null;
                foreach (var c in s.Hand)
                {
                    if (c.Cost != k || c.Cost > mana + 0.001f) continue;
                    if (start == null || c.Score > start.Score) start = c;
                }
                if (start == null) continue;
                int sc;
                var seq = ChainFrom(s, start, mana, step, out sc);
                if (sc > bestScore) { bestScore = sc; best = seq; }
            }
            return best;
        }

        /// <summary>
        /// 从 <paramref name="first"/> 起按连击阶梯续接（每张比上一张正好 step 费），法力不足即停止。
        /// 每步都取该费用下评分最高的那张牌。
        /// </summary>
        private static List<CardInfo> ChainFrom(Snapshot s, CardInfo first, float mana, int step, out int totalScore)
        {
            var seq = new List<CardInfo>();
            var taken = new HashSet<CardInfo>();
            float rem = mana;
            var cur = first;

            while (cur != null && cur.Cost <= rem + 0.001f)
            {
                rem = rem + cur.ManaGain - cur.Cost;
                seq.Add(cur);
                taken.Add(cur);

                int want = cur.Cost + step;
                CardInfo next = null;
                if (want >= 0)
                {
                    foreach (var c in s.Hand)
                    {
                        if (taken.Contains(c)) continue;
                        if (c.Cost != want) continue;
                        if (c.Cost > rem + 0.001f) continue;
                        if (next == null || c.Score > next.Score) next = c;
                    }
                }
                cur = next;
            }

            totalScore = 0;
            foreach (var c in seq) totalScore += c.Score;
            return seq;
        }

    }
}
