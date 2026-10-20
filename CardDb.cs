using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VcAdvisor
{
    public enum CardRole
    {
        Unknown = 0,
        Character,
        Weapon,
        Passive,
        Defense,
        Mana,
        Economy,
        Utility,
        Debuff,     // 负面牌：由敌人塞进牌库，打出只会吃亏
    }

    /// <summary>
    /// 卡牌静态数据库：内嵌的 中文名 表 + 人工校准的卡牌分类表。
    /// </summary>
    public static class CardDb
    {
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, CardRole> Roles =
            new Dictionary<string, CardRole>(StringComparer.OrdinalIgnoreCase);

        static CardDb()
        {
            try { LoadNames(); } catch { }
            try { LoadRoles(); } catch { }
        }

        private static void LoadNames()
        {
            var asm = Assembly.GetExecutingAssembly();
            string resName = null;
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith("cards.tsv", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
            }
            if (resName == null) return;

            using (var s = asm.GetManifestResourceStream(resName))
            using (var r = new StreamReader(s, System.Text.Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    int idx = line.IndexOf('\t');
                    if (idx <= 0) continue;
                    string k = line.Substring(0, idx).Trim();
                    string v = line.Substring(idx + 1).Trim();
                    if (k.Length > 0 && v.Length > 0) Names[k] = v;
                }
            }
        }

        private static void Add(CardRole role, params string[] keys)
        {
            foreach (var k in keys) Roles[k] = role;
        }

        private static void LoadRoles()
        {
            // 提供法力的卡：越早打越能滚雪球
            Add(CardRole.Mana, "RAW_MANA", "KAROMAS_MANA", "SONG_OF_MANA",
                "EMPTY_TOME0", "EMPTY_TOME1", "EMPTY_TOME2", "EMPTY_TOME3");

            // 经济 / 金币
            Add(CardRole.Economy, "BAG_OF_COINS", "LARGE_BAG_OF_COINS", "COIN_PURSE",
                "GOLDRING", "SILVERRING", "STONE_MASK");

            // 防御 / 生命类
            Add(CardRole.Defense, "ARMOR0", "ARMOR1", "ARMOR2", "ARMOR3",
                "HOLLOW_HEART1", "HOLLOW_HEART2", "PUMMAROLA1", "PUMMAROLA2",
                "LITTLE_HEART", "PARM_AEGIS");

            // 被动增益类
            Add(CardRole.Passive, "SPINACH0", "SPINACH1", "SPINACH2",
                "CLOVER", "LITTLE_CLOVER", "CLOVER_PETAL",
                "DUPLICATOR0", "DUPLICATOR1", "DUPLICATOR2",
                "CANDELABRADOR0", "CANDELABRADOR1", "CANDELABRADOR2",
                "BRACER", "VACUUM", "ATTRACTORB", "OROLOGION", "TIRAJISU",
                "SKULLOMANIAC", "AMBROJOE", "SPELLBINDER", "ROSARY",
                "FRIENDSHIP_AMULET", "WINGS", "CROWN1", "CROWN2");

            // 角色卡
            Add(CardRole.Character, "ANTONIO", "ARCA", "CAVALLO", "CHRISTINE", "CLERICI",
                "CONCETTA", "DIVANO", "DOMMARIO", "GALLO", "GENNARO", "GIOVANNA", "IMELDA",
                "KROCHI", "LAMA", "MORTACCIO", "OSOLE", "PASQUALINA", "POE", "POPPEA",
                "PORTA", "PUGNALA", "RAMBA", "ZIASSUNTA");

            // 功能 / 特殊
            Add(CardRole.Utility, "TROUBLER", "TROUSER",
                "WILD_COMBO", "WILD_ATTACK", "WILD_DEFENSE", "CELESTIAL_DUSTING",
                "BRACELET");

            // 负面牌（官方 CardDatabase 中 cardType = CardType_Debuff）
            Add(CardRole.Debuff, "JUNK", "CONFUSE", "MUDDLE", "MANA_BOMB",
                "ANGELO_SPIETATO", "CURSED_LANCET", "SHATTER");
        }

        /// <summary>
        /// 把游戏资源名归一化成中文表的键。
        /// 例：Card_M_3_SongOfMana → SONG_OF_MANA ；FCC_Gallo → GALLO
        /// </summary>
        private static string Normalize(string raw)
        {
            int tier;
            return Normalize(raw, out tier);
        }

        /// <summary>
        /// 归一化的同时取出资源名里的阶级编号（Card_B_2_EmptyTome → 2）。
        /// 该编号决定升级后的卡名，不能丢：EMPTY_TOME0=空白之书、EMPTY_TOME2=厚重之书。
        /// </summary>
        private static string Normalize(string raw, out int tier)
        {
            tier = -1;
            if (string.IsNullOrEmpty(raw)) return raw;
            string s = raw.Trim();

            if (s.StartsWith("FCC_", StringComparison.Ordinal))
            {
                s = s.Substring(4);
            }
            else if (s.StartsWith("Card_", StringComparison.Ordinal))
            {
                s = s.Substring(5);
                // 去掉形如 "B_2_" / "M_3_" 的分类+阶级前缀，但保住阶级号
                var m = Regex.Match(s, @"^[A-Za-z]+_(\d+)_");
                if (m.Success)
                {
                    int t;
                    if (int.TryParse(m.Groups[1].Value, out t)) tier = t;
                    s = s.Substring(m.Length);
                }
            }

            // PascalCase → UPPER_SNAKE
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool isUpper = c >= 'A' && c <= 'Z';
                bool prevLower = i > 0 && s[i - 1] >= 'a' && s[i - 1] <= 'z';
                if (isUpper && prevLower) sb.Append('_');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>归一化后匹配中文表；升级卡按资源名里的阶级号取对应名称，再回退到 0..4 级。</summary>
        private static string Resolve(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int tier;
            string n = Normalize(raw, out tier);
            // 阶级号优先：Card_B_2_EmptyTome → EMPTY_TOME2（厚重之书），而不是 EMPTY_TOME0（空白之书）
            if (tier >= 0 && Names.ContainsKey(n + tier)) return n + tier;
            if (Names.ContainsKey(n)) return n;
            for (int d = 0; d <= 4; d++)
            {
                string cand = n + d;
                if (Names.ContainsKey(cand)) return cand;
            }
            return n;
        }

        public static string Name(string key)
        {
            if (string.IsNullOrEmpty(key)) return "?";
            string r = Resolve(key);
            string n;
            if (Names.TryGetValue(r, out n)) return n;
            return r;
        }

        /// <summary>内嵌中文名表是否成功读取。</summary>
        public static int NameCount { get { return Names.Count; } }

        public static CardRole RoleOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return CardRole.Unknown;
            CardRole r;
            if (Roles.TryGetValue(Resolve(key), out r)) return r;
            if (Roles.TryGetValue(Normalize(key), out r)) return r;
            return CardRole.Unknown;
        }

        /// <summary>
        /// 人工分类表没覆盖时，用游戏自身的卡牌类型兜底。
        /// 官方 CardType 共 7 种：Attack / Mana / Defense / Character / Special / Void / Debuff。
        /// </summary>
        public static CardRole RoleFromType(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return CardRole.Unknown;
            // 兼容两种写法：资源名 CardType_Debuff 与 typeId Debuff
            string t = typeId.StartsWith("CardType_", StringComparison.Ordinal) ? typeId.Substring(9) : typeId;
            switch (t)
            {
                case "Attack": return CardRole.Weapon;
                case "Mana": return CardRole.Mana;
                case "Defense": return CardRole.Defense;
                case "Character": return CardRole.Character;
                case "Special": return CardRole.Utility;
                case "Debuff": return CardRole.Debuff;
                // Void 是金币袋/三叶草/念珠/吸尘器这类拾取增益牌，与人工表的归为一类
                case "Void": return CardRole.Passive;
                default: return CardRole.Unknown;
            }
        }

        public static string RoleName(CardRole r)
        {
            switch (r)
            {
                case CardRole.Character: return "角色卡";
                case CardRole.Weapon: return "武器";
                case CardRole.Passive: return "被动";
                case CardRole.Defense: return "防御";
                case CardRole.Mana: return "法力";
                case CardRole.Economy: return "经济";
                case CardRole.Utility: return "功能";
                case CardRole.Debuff: return "负面";
                default: return "未知";
            }
        }
    }
}
