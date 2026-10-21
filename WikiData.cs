using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace VcAdvisor
{
    /// <summary>
    /// 内置 Wiki 资料（来源：Vampire Crawlers 中文 Wiki，1.5 资料）。
    /// 只保存纯文本：角色技能、卡牌描述、奥术、宝石、进化配方、关键词解释。
    /// 不含任何图片或游戏素材。
    ///
    /// 用途：
    /// 1) 界面展示英雄技能与卡牌说明；
    /// 2) 为对局日志与案例库提供额外特征（英雄技能、可进化配方）；
    /// 3) 后续可用于校验评分规则，不直接替代游戏运行时数据。
    /// </summary>
    public static class WikiData
    {
        public sealed class WikiChar
        {
            public string Name;
            public string Skill;        // 登场 / 在场触发
            public string Stat;         // 主要探索者属性加成
            public string Starting;     // 初始卡牌
            public string Duration;
        }

        public sealed class WikiCard
        {
            public string Name;
            public string Category;     // 攻击牌 / 防御牌 / 法力牌 / 属性牌 / 万能牌 / 临时牌 / 进化牌
            public string Cost;
            public string Desc;
        }

        public sealed class Recipe
        {
            public List<List<string>> Inputs = new List<List<string>>();
            public string Output;
        }

        private static bool _loaded;
        private static readonly Dictionary<string, WikiChar> _chars = new Dictionary<string, WikiChar>(StringComparer.Ordinal);
        private static readonly Dictionary<string, WikiCard> _cards = new Dictionary<string, WikiCard>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _arcanas = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _gems = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _effects = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Recipe> _recipes = new List<Recipe>();

        public static int CharacterCount { get { return _chars.Count; } }
        public static int CardCount { get { return _cards.Count; } }
        public static int RecipeCount { get { return _recipes.Count; } }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                // 资源名在不同加载方式下可能带前缀，按后缀查找更稳（与 CardDb 的做法一致）
                string resName = null;
                var names = asm.GetManifestResourceNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].EndsWith("wiki.tsv", StringComparison.OrdinalIgnoreCase)) { resName = names[i]; break; }
                }
                if (resName == null)
                {
                    try { AdvisorPlugin.Logger.LogWarning("[Wiki] 未找到内嵌 wiki.tsv 资源"); } catch { }
                    return;
                }

                using (var stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream == null) return;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string section = null;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            if (line[0] == '[')
                            {
                                section = line.Trim('[', ']');
                                continue;
                            }
                            var p = line.Split('\t');
                            switch (section)
                            {
                                case "CHARS":
                                    if (p.Length >= 2)
                                        _chars[p[0]] = new WikiChar
                                        {
                                            Name = p[0],
                                            Skill = p[1],
                                            Stat = p.Length > 2 ? p[2] : "",
                                            Starting = p.Length > 3 ? p[3] : "",
                                            Duration = p.Length > 4 ? p[4] : ""
                                        };
                                    break;
                                case "CARDS":
                                    if (p.Length >= 4)
                                        _cards[p[0]] = new WikiCard { Name = p[0], Category = p[1], Cost = p[2], Desc = p[3] };
                                    break;
                                case "ARCANAS":
                                    if (p.Length >= 3)
                                        _arcanas[p[0]] = p[2];
                                    break;
                                case "GEMS":
                                    if (p.Length >= 2)
                                        _gems[p[0]] = p[1];
                                    break;
                                case "RECIPES":
                                    if (p.Length >= 2)
                                    {
                                        var recipe = new Recipe { Output = p[1] };
                                        var groups = p[0].Split(';');
                                        for (int i = 0; i < groups.Length; i++)
                                        {
                                            var alts = new List<string>();
                                            var parts = groups[i].Split('|');
                                            for (int k = 0; k < parts.Length; k++)
                                                if (!string.IsNullOrEmpty(parts[k])) alts.Add(parts[k]);
                                            if (alts.Count > 0) recipe.Inputs.Add(alts);
                                        }
                                        _recipes.Add(recipe);
                                    }
                                    break;
                                case "EFFECTS":
                                    if (p.Length >= 2)
                                        _effects[p[0]] = p[1];
                                    break;
                            }
                        }
                    }
                }
                try
                {
                    AdvisorPlugin.Logger.LogInfo(string.Format(
                        "[Wiki] 已加载：角色 {0}、卡牌 {1}、奥术 {2}、宝石 {3}、进化配方 {4}、关键词 {5}",
                        _chars.Count, _cards.Count, _arcanas.Count, _gems.Count, _recipes.Count, _effects.Count));
                }
                catch { }
            }
            catch { }
        }

        /// <summary>按英雄名查找（支持“加洛”这类短名与 wiki 全名互相包含）。</summary>
        public static WikiChar Character(string heroName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(heroName)) return null;
            WikiChar c;
            if (_chars.TryGetValue(heroName, out c)) return c;
            foreach (var kv in _chars)
            {
                if (kv.Key.IndexOf(heroName, StringComparison.Ordinal) >= 0) return kv.Value;
            }
            return null;
        }

        public static WikiCard Card(string cardName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(cardName)) return null;
            WikiCard c;
            if (_cards.TryGetValue(cardName, out c)) return c;
            // 卡名可能带阶级后缀，退化到“包含”匹配
            foreach (var kv in _cards)
            {
                if (kv.Key.IndexOf(cardName, StringComparison.Ordinal) >= 0
                    || cardName.IndexOf(kv.Key, StringComparison.Ordinal) >= 0)
                    return kv.Value;
            }
            return null;
        }

        public static string CardDesc(string cardName)
        {
            var c = Card(cardName);
            return c != null ? c.Desc : null;
        }

        public static string ArcanaDesc(string arcanaName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(arcanaName)) return null;
            string s;
            if (_arcanas.TryGetValue(arcanaName, out s)) return s;
            foreach (var kv in _arcanas)
                if (kv.Key.IndexOf(arcanaName, StringComparison.Ordinal) >= 0) return kv.Value;
            return null;
        }

        public static string EffectDesc(string effectName)
        {
            EnsureLoaded();
            string s;
            return _effects.TryGetValue(effectName, out s) ? s : null;
        }

        /// <summary>
        /// 根据当前手牌判断进化配方状态：
        /// ready = 所有材料都在手上；near = 至少有一组材料在手上。
        /// </summary>
        public static void RecipesInHand(List<CardInfo> hand, List<string> ready, List<string> near, int limit)
        {
            EnsureLoaded();
            if (hand == null || _recipes.Count == 0) return;

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i] != null && !string.IsNullOrEmpty(hand[i].Name)) names.Add(hand[i].Name);
            }

            for (int r = 0; r < _recipes.Count; r++)
            {
                var recipe = _recipes[r];
                if (recipe.Inputs.Count == 0) continue;

                bool all = true, any = false;
                for (int g = 0; g < recipe.Inputs.Count; g++)
                {
                    bool hit = false;
                    var group = recipe.Inputs[g];
                    for (int a = 0; a < group.Count; a++)
                    {
                        if (names.Contains(group[a])) { hit = true; break; }
                    }
                    if (hit) any = true; else all = false;
                }

                if (all && ready != null && ready.Count < limit) ready.Add(recipe.Output);
                else if (!all && any && near != null && near.Count < limit) near.Add(recipe.Output);
            }
        }
    }
}
