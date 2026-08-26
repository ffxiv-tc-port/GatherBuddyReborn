using Dalamud.Game;
using GatherBuddy.Classes;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Plugin;

public class Identificator
{
    public const int MaxDistance = 4;

    private readonly GameData                               _data;
    private readonly FrozenDictionary<string, Gatherable>[] _gatherableFromLanguage;
    private readonly FrozenDictionary<string, Fish>[]       _fishFromLanguage;

    public Identificator()
    {
        _data = GatherBuddy.GameData;
        var languages = new[]
        {
            GatherBuddy.Language,
            ClientLanguage.English,
            ClientLanguage.German,
            ClientLanguage.French,
            ClientLanguage.Japanese,
        }.Distinct().ToArray();

        // 部分用戶端(例如台服)Lumina 對任何請求語言都會回傳同一份資料,
        // 使下面各語言建出來的字典內容其實逐條完全相同。這裡不假設「台服」這個特例,
        // 而是實際比對取得的名稱是否相同,相同就直接複用主要語言(languages[0])已建好的
        // 字典,不重複讀取、不重複配置——國際服四語言彼此有別,行為完全不受影響。
        _gatherableFromLanguage = BuildLanguageDictionaries(languages, CreateGatherableDictionary,
            (a, b) => _data.Gatherables.Values.All(g => g.Name[a] == g.Name[b]));
        _fishFromLanguage = BuildLanguageDictionaries(languages, CreateFishDictionary,
            (a, b) => _data.Fishes.Values.All(f => f.Name[a] == f.Name[b]));
    }

    /// <summary>
    /// 依序建立每個語言對應的字典。若某語言在目前資料下實際取得的名稱與主要語言
    /// (languages[0])逐條完全相同,直接複用已建好的字典,不重新建置。
    /// </summary>
    private static TDict[] BuildLanguageDictionaries<TDict>(ClientLanguage[] languages,
        Func<ClientLanguage, TDict> createDictionary, Func<ClientLanguage, ClientLanguage, bool> producesIdenticalNames)
    {
        var result = new TDict[languages.Length];
        result[0] = createDictionary(languages[0]);
        for (var i = 1; i < languages.Length; ++i)
            result[i] = producesIdenticalNames(languages[0], languages[i]) ? result[0] : createDictionary(languages[i]);

        return result;
    }

    private FrozenDictionary<string, Gatherable> CreateGatherableDictionary(ClientLanguage l)
    {
        var dict = new Dictionary<string, Gatherable>(_data.Gatherables.Count);
        foreach (var (gatherable, name) in _data.Gatherables.Values.Select(g => (g, g.Name[l].ToLowerInvariant())))
        {
            if (!dict.TryAdd(name, gatherable))
            {
                for (var i = 2; i < 10; ++i)
                {
                    if (dict.TryAdd(name + $" ({i})", gatherable))
                        break;
                }
            }
        }

        return dict.ToFrozenDictionary();
    }

    private FrozenDictionary<string, Fish> CreateFishDictionary(ClientLanguage l)
    {
        var dict = new Dictionary<string, Fish>(_data.Fishes.Count);
        foreach (var (fish, name) in _data.Fishes.Values.Select(f => (f, f.Name[l].ToLowerInvariant())))
        {
            if (!dict.TryAdd(name, fish))
            {
                for (var i = 2; i < 10; ++i)
                {
                    if (dict.TryAdd(name + $" ({i})", fish))
                        break;
                }
            }
        }

        return dict.ToFrozenDictionary();
    }

    private static bool SearchContains<T>(FrozenDictionary<string, T> dict, string name, out T? ret) where T : class
    {
        ret = null;
        var length = int.MaxValue;
        foreach (var (n, obj) in dict)
        {
            if (length < 0)
            {
                if (n.Length >= -length || !n.StartsWith(name))
                    continue;

                ret    = obj;
                length = -n.Length;
            }
            else if (n.Length < length)
            {
                if (!n.Contains(name))
                    continue;

                ret = obj;
                if (n.StartsWith(name))
                    length = -n.Length;
                else
                    length = n.Length;
            }
            else if (n.StartsWith(name))
            {
                ret    = obj;
                length = -n.Length;
            }
        }

        return ret != null;
    }

    public Gatherable? IdentifyGatherable(string itemName)
    {
        if (itemName.Length == 0)
            return null;

        // Check for full matches in current language first, by initialization order.
        var itemNameLower = itemName.ToLowerInvariant();
        foreach (var dict in _gatherableFromLanguage)
        {
            if (dict.TryGetValue(itemNameLower, out var item))
                return item;
        }

        // Search for the shortest object in the current language that starts with the given string.
        // If none does, use the shortest object that contains the given string.
        if (SearchContains(_gatherableFromLanguage[0], itemNameLower, out var ret))
            return ret;

        // Check for fuzzy matches up to the given MaxDistance.
        return _data.GatherablesTrie.FuzzyFind(itemNameLower, MaxDistance, out var data) < MaxDistance ? data : null;
    }

    public Fish? IdentifyFish(string itemName)
    {
        if (itemName.Length == 0)
            return null;

        // Same as for gatherables.
        var itemNameLower = itemName.ToLowerInvariant();
        foreach (var dict in _fishFromLanguage)
        {
            if (dict.TryGetValue(itemNameLower, out var item))
                return item;
        }

        if (SearchContains(_fishFromLanguage[0], itemNameLower, out var ret))
            return ret;

        return _data.FishTrie.FuzzyFind(itemNameLower, MaxDistance, out var data) < MaxDistance ? data : null;
    }
}
