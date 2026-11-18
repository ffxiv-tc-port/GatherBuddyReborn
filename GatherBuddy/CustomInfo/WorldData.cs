using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using GatherBuddy.Plugin;
using Newtonsoft.Json;

namespace GatherBuddy.CustomInfo
{
    public static class WorldData
    {
        private static string WorldLocationsPath = Path.Combine(Dalamud.PluginInterface.ConfigDirectory.FullName, "world_locations.json");
        private static string NodeOffsetsPath = Path.Combine(Dalamud.PluginInterface.ConfigDirectory.FullName, "node_offsets.json");
        public static Dictionary<uint, List<Vector3>> WorldLocationsByNodeId { get; set; } = new();
        public static Dictionary<Vector3, Vector3>    NodeOffsets            { get; set; } = new();
        public static ReadOnlyCollection<(ushort BaseGathering, ushort BasePerception)> IlvConvertTable { get; private set; }

        static WorldData()
        {
            LoadLocationsFromFile();
            LoadOffsetsFromFile();
            LoadIlvConvertTableFromFile();
        }

        // 載入時讀不懂、而且備份不成：這次載入期間不再寫該檔，原檔保留。
        private static volatile bool _locationsWriteBlocked;
        private static volatile bool _offsetsWriteBlocked;

        private static void LoadOffsetsFromFile()
        {
            var settings = new JsonSerializerSettings();
            var resourceName = "GatherBuddy.CustomInfo.node_offsets.json";
            NodeOffsets = LoadFromFile<List<OffsetPair>>(NodeOffsetsPath, resourceName, settings, MergeData, out var blocked)
                .GroupBy(x => x.Original).ToDictionary(x => x.Key, x => x.First().Offset);
            _offsetsWriteBlocked = blocked;
        }

        private static void LoadLocationsFromFile()
        {
            var resourceName = "GatherBuddy.CustomInfo.world_locations.json";
            WorldLocationsByNodeId = LoadFromFile<Dictionary<uint, List<Vector3>>>(WorldLocationsPath, resourceName, new JsonSerializerSettings(), MergeData,
                out var blocked);
            _locationsWriteBlocked = blocked;
        }

        // 契約：使用者的檔讀不懂（壞 JSON、半截、空白、全 NUL、開不了）不擲例外——這裡在靜態建構子裡，擲了＝插件每次都載入失敗。
        // 讀不懂就先備份原檔再改用內嵌預設；備份不成就不寫（writeBlocked）。合併結果與原檔逐字相同就不寫。
        private static T LoadFromFile<T>(string path, string resourceName, JsonSerializerSettings settings, Func<T, T, T> merge, out bool writeBlocked)
            where T : class, new()
        {
            var assembly = typeof(GatherBuddy).Assembly;
            T   defaultObj;

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException("Embedded resource not found.", resourceName);

                using (var reader = new StreamReader(stream))
                {
                    defaultObj = DeserializeStream<T>(reader, settings) ?? new T();
                }
            }

            writeBlocked = false;
            var     file     = new FileInfo(path);
            string? original = null;
            T?      existing = null;
            if (file.Exists)
            {
                string? problem = null;
                try
                {
                    original = File.ReadAllText(path);
                    if (original.AsSpan().Trim(" \t\r\n\0").IsEmpty)
                        problem = "內容是空的";
                    else if ((existing = DeserializeStream<T>(new StringReader(original), settings)) == null)
                        problem = "內容無法辨識（解析結果是 null）";
                }
                catch (Exception e)
                {
                    problem = $"{e.GetType().Name}: {e.Message}";
                }

                if (problem != null)
                {
                    writeBlocked = Functions.BackupUnreadableSaveFile(file, $"讀取失敗（{problem}）",
                        "這次改用內建的預設資料（自己發現的採集點／偏移要從備份找回）；備份成功才會用預設資料覆寫原檔，備份失敗則這次載入期間不寫它。")
                     == null;
                    original = null;
                    existing = null;
                }
            }

            // 沒有檔時也走合併：內嵌的偏移表有重複項，合併會去重 ⇒ 第一次寫出的就是之後每次載入都不會再改的內容。
            var merged = merge(defaultObj, existing ?? new T());
            if (writeBlocked)
                return merged;

            var text = JsonConvert.SerializeObject(merged, Formatting.Indented, settings);
            if (!string.Equals(text, original, StringComparison.Ordinal))
                Functions.WriteSaveFile(file, text, $"Could not write {file.FullName}");
            return merged;
        }

        private static T? DeserializeStream<T>(TextReader reader, JsonSerializerSettings settings)
        {
            var serializer = JsonSerializer.CreateDefault(settings);
            serializer.CheckAdditionalContent = true;
            using var json = new JsonTextReader(reader);
            return serializer.Deserialize<T>(json);
        }

        private static Dictionary<uint,List<Vector3>> MergeData(Dictionary<uint, List<Vector3>> defaultData, Dictionary<uint, List<Vector3>> existingData)
        {
            foreach (var kvp in defaultData)
            {
                if (existingData.TryGetValue(kvp.Key, out var existingList))
                {
                    foreach (var vector in kvp.Value)
                    {
                        if (!existingList.Contains(vector))
                        {
                            existingList.Add(vector);
                        }
                    }
                }
                else
                {
                    existingData[kvp.Key] = new List<Vector3>(kvp.Value);
                }
            }

            return existingData;
        }

        private static List<OffsetPair> MergeData(List<OffsetPair> defaultData, List<OffsetPair> existingData)
        {
            return existingData.Concat(defaultData).GroupBy(v => v.Original).Select(v => v.First()).ToList();
        }

        public static void AddOffset(Vector3 original, Vector3 offset)
        {
            if (NodeOffsets.ContainsKey(original))
            {
                GatherBuddy.Log.Error($"{original} already has an offset of {NodeOffsets[original]}. Unable to add new offset.");
                return;
            }
            
            NodeOffsets[original] = offset;
            Task.Run(SaveOffsetsToFile);
        }

        public static void SaveOffsetsToFile()
        {
            if (_offsetsWriteBlocked)
                return;

            var settings = new JsonSerializerSettings();
            var offsetJson = Newtonsoft.Json.JsonConvert.SerializeObject(NodeOffsets.Select(x => new OffsetPair(x.Key, x.Value)).ToList(), Formatting.Indented, settings);

            Functions.WriteSaveFile(new FileInfo(NodeOffsetsPath), offsetJson, $"Could not write {NodeOffsetsPath}");
        }

        public static void AddLocation(uint nodeId, Vector3 location)
        {
            if (!WorldLocationsByNodeId.TryGetValue(nodeId, out var list))
            {
                lock (WorldLocationsByNodeId)
                    WorldLocationsByNodeId[nodeId] = list = [];
                foreach (var node in GatherBuddy.GameData.GatheringNodes.Values.Where(v => v.WorldPositions.ContainsKey(nodeId)))
                    node.WorldPositions[nodeId] = list;
            }
                
            if (!list.Contains(location))
            {
                lock (WorldLocationsByNodeId)
                    list.Add(location);

                Task.Run(SaveLocationsToFile);
                GatherBuddy.Log.Debug($"Added location {location} to node {nodeId}");
                WorldLocationsChanged?.Invoke();
            }
        }

        public static event Action? WorldLocationsChanged;

        /// <summary>
        /// Serializes writers against each other. Deliberately not <see cref="WorldLocationsByNodeId"/>: that one is
        /// also taken by the framework thread in <see cref="AddLocation"/>, and it must never have to wait on file I/O.
        /// </summary>
        private static readonly object SaveLocationsLock = new();

        public static void SaveLocationsToFile()
        {
            if (_locationsWriteBlocked)
                return;

            // Only the snapshot is taken under the data lock; serializing and writing happen outside it. Previously the
            // caller held the data lock for the entire serialize + WriteAllText, so the framework thread stalled inside
            // AddLocation until the write completed - which it hit repeatedly while discovering new nodes in a fresh zone.
            lock (SaveLocationsLock)
            {
                Dictionary<uint, List<Vector3>> snapshot;
                lock (WorldLocationsByNodeId)
                    snapshot = WorldLocationsByNodeId.ToDictionary(kvp => kvp.Key, kvp => new List<Vector3>(kvp.Value));

                var locJson = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented);

                // 在鎖內排進背景佇列（同路徑只留最新一份）：快照的先後就是落地的先後。
                Functions.WriteSaveFile(new FileInfo(WorldLocationsPath), locJson, $"Could not write {WorldLocationsPath}");
            }
        }

        [MemberNotNull(nameof(IlvConvertTable))]
        private static unsafe void LoadIlvConvertTableFromFile()
        {
            var resourceName = "GatherBuddy.CustomInfo.IlvConvertTable.csv";
            var assembly = typeof(GatherBuddy).Assembly;
            var resource = assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException("Embedded resource not found.", resourceName);
            var stream = new StreamReader(resource);

            stream.ReadLine();
            var list = new List<(ushort, ushort)>(1000);
            while (stream.ReadLine() is string line and { Length: > 5 })
            {
                Span<ushort> values = [.. line.Split(',').Select(ushort.Parse)];
                while (list.Count < values[0] + 1) list.Add((0, 0));
                list[values[0]] = (values[1], values[2]);
            }
            IlvConvertTable = new([.. list]);
        }
    }

    public record struct OffsetPair(Vector3 Original, Vector3 Offset)
    {
    }
}
