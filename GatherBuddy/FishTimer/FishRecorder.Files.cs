using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using MessagePack;
using Newtonsoft.Json;

namespace GatherBuddy.FishTimer;

public partial class FishRecorder
{
    public const    string        FishRecordFileName = "fish_records.dat";
    public readonly DirectoryInfo FishRecordDirectory;

    public  int       Changes  = 0;
    public  TimeStamp SaveTime = TimeStamp.MaxValue;

    // 載入時讀不懂、而且備份不成：這次載入期間不寫 fish_records.dat，原檔保留。
    private bool _fileWriteBlocked;

    public int AddChanges()
    {
        SaveTime = TimeStamp.UtcNow.AddMinutes(3);
        return ++Changes;
    }

    public void WriteFile()
    {
        var file = new FileInfo(Path.Combine(FishRecordDirectory.FullName, FishRecordFileName));
        Changes  = 0;
        SaveTime = TimeStamp.MaxValue;
        WriteFileInternal(file);
    }

    private void WriteFileInternal(FileInfo file)
    {
        if (_fileWriteBlocked)
            return;

        GatherBuddy.Log.Debug($"Saving fish record file to {file.FullName} with {Changes} changes.");
        try
        {
            // 序列化在呼叫端執行緒做完；寫入在背景原子換名，卸載時由 ConfigFileSafety.Flush 等它寫完。
            var bytes = GetRecordBytes(false);
            ConfigFileSafety.EnqueueWrite(file.FullName, bytes, $"Could not write fish record file {file.FullName}", s => GatherBuddy.Log.Error(s));
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Could not write fish record file {file.FullName}:\n{e}");
        }
    }

    private void TimedSave()
    {
        if (TimeStamp.UtcNow > SaveTime)
        {
            WriteFile();
        }
    }

    public string ExportBase64()
    {
        var bytes = GetRecordBytes(false);
        return Functions.CompressedBase64(bytes);
    }

    public void ExportJson(FileInfo file)
    {
        try
        {
            var data = JsonConvert.SerializeObject(Records.Select(r => r.ToJson()), Formatting.Indented);
            File.WriteAllText(file.FullName, data);
            GatherBuddy.Log.Information($"Exported {Records.Count} fish records to {file.FullName}.");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Could not export json file to {file.FullName}:\n{e}");
        }
    }

    public void ImportBase64(string data)
    {
        try
        {
            var bytes   = Functions.DecompressedBase64(data);
            var records = ReadBytes(bytes, "Imported Data", out _);
            MergeRecordsIn(records);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Error while importing fish records:\n{e}");
        }
    }

    public void MergeRecordsIn(IReadOnlyList<FishRecord> newRecords)
    {
        foreach (var record in newRecords.Where(CheckSimilarity))
            AddUnchecked(record);

        if (Changes > 0)
            WriteFile();
    }

    // problem 非 null＝整份或部分讀不懂（已記 Error），回傳的是讀得懂的那些。
    public static List<FishRecord> ReadFile(FileInfo file, out string? problem)
    {
        problem = null;
        if (!file.Exists)
            return new List<FishRecord>();

        try
        {
            var bytes = File.ReadAllBytes(file.FullName);
            return ReadBytes(bytes, $"File {file.FullName}", out problem);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Unknown error reading fish record file {file.FullName}:\n{e}");
            problem = $"{e.GetType().Name}: {e.Message}";
            return new List<FishRecord>();
        }
    }

    private byte[] GetRecordBytes(bool remote)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(FishRecord.Version);

        var records = remote ? RemoteRecords : Records;
        MessagePackSerializer.Serialize(ms, records);

        return ms.ToArray();
    }

    private static List<FishRecord> ReadBytes(byte[] data, string name, out string? problem)
    {
        problem = null;
        if (data.Length == 0)
            return new List<FishRecord>();

        switch (data[0])
        {
            case 1:
            {
                if (data.Length % FishRecord.Version1ByteLength != 1)
                {
                    GatherBuddy.Log.Error($"{name} has no valid size for its record version, skipped.\n");
                    problem = "大小與版本 1 的紀錄長度不合";
                    return new List<FishRecord>();
                }

                var numRecords = (data.Length - 1) / FishRecord.Version1ByteLength;
                var ret        = new List<FishRecord>(numRecords);
                for (var i = 0; i < numRecords; ++i)
                {
                    if (!FishRecord.FromBytesV1(data, 1 + i * FishRecord.Version1ByteLength, out var record))
                    {
                        GatherBuddy.Log.Error($"{name}'s {i}th record is invalid, skipped.\n");
                        problem = "有紀錄無效、已略過";
                        continue;
                    }

                    ret.Add(record);
                }

                return ret;
            }
            case 2:
            {
                var span = data.AsSpan()[1..];
                try
                {
                    var list = MessagePackSerializer.Deserialize<List<FishRecord>>(span.ToArray());
                    return list;
                }
                catch (Exception e)
                {
                    GatherBuddy.Log.Error($"{name} was unable to be deserialized using V2 logic.");
                    problem = $"版本 2 解析失敗（{e.GetType().Name}: {e.Message}）";
                    return new List<FishRecord>();
                }
            }
            default:
                GatherBuddy.Log.Error($"{name} has no valid record version, skipped.\n");
                problem = $"不認得的版本位元組 {data[0]}";
                return new List<FishRecord>();
        }
    }

    private void LoadFile(FileInfo file)
    {
        if (!file.Exists)
            return;

        try
        {
            Records.AddRange(ReadFile(file, out var problem));
            if (problem != null)
                _fileWriteBlocked = Functions.BackupUnreadableSaveFile(file, $"讀取失敗（{problem}）",
                    "這次只用讀得懂的紀錄（可能是空的）；備份成功時，之後有新釣獲或卸載時會用目前的紀錄整份覆寫原檔，備份失敗則這次載入期間不寫它。")
                 == null;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Could not read fish record file {file.FullName}:\n{e}");
        }
        ResetTimes();
    }
}
