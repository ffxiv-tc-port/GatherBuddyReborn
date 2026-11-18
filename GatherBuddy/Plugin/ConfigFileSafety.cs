using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Utility;

namespace GatherBuddy.Plugin;

// 本檔假設與契約（艦隊範本 config-load-safety）：
// ①讀壞的原檔在任何寫入之前先複製成 <檔名>.bad-<時間>；既有備份永不覆寫（CreateNew），同內容只留一份。
// ②寫入一律 FilesystemUtil.WriteAllTextSafe（暫存檔＋落盤＋換名）：中斷時原檔維持上一份完整內容。
// ③同一路徑的寫入在背景依序執行、尚未寫出的只留最新一份；序列化由呼叫端在自己的執行緒做完才交進來。
// ④所有公開方法永不擲例外；失敗只記錄，不重試。
internal static class ConfigFileSafety
{
    private static readonly object                     QueueLock = new();
    private static readonly Dictionary<string, Write>  Pending   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task>   Running   = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Write(string Text, string FailureMessage, Action<string> LogError);

    // 回傳這份內容的備份檔完整路徑（含「早已備份過」的那一份）；失敗回 null。
    public static string? BackupUnreadable(string path, string reason, string consequence, Action<string> log, Func<DateTime>? now = null)
    {
        var name = Path.GetFileName(path);
        try
        {
            var original = File.ReadAllBytes(path);
            var dir      = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles(name + ".bad-*"))
            {
                if (old.Length == original.Length && File.ReadAllBytes(old.FullName).AsSpan().SequenceEqual(original))
                {
                    log($"[設定] {name}：{reason}；相同內容已備份過（{old.FullName}），這次不再另存。{consequence}");
                    return old.FullName;
                }
            }

            var backup = Path.Combine(dir, $"{name}.bad-{(now ?? (() => DateTime.Now))():yyyyMMdd-HHmmss-fff}");
            using (var fs = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(original);
            log($"[設定] {name}：{reason}；原檔已備份為 {backup}。{consequence}");
            return backup;
        }
        catch (Exception e)
        {
            log($"[設定] {name}：{reason}；備份原檔失敗（{e.GetType().Name}: {e.Message}）。{consequence}");
            return null;
        }
    }

    // 部分載入（略過或修正了某些項目）之後、寫回之前呼叫：寫回內容與原檔逐字相同就不備份、不記錄；
    // 算不出寫回內容時當成會改變，照樣備份。
    public static string? BackupBeforeRewrite(string path, string originalText, Func<string> newText, string reason, string consequence,
        Action<string> log, Func<DateTime>? now = null)
    {
        try
        {
            if (string.Equals(originalText, newText(), StringComparison.Ordinal))
                return null;
        }
        catch (Exception)
        {
            // 見上：比不出來就照樣備份。
        }

        return BackupUnreadable(path, reason, consequence, log, now);
    }

    public static void EnqueueWrite(string path, string text, string failureMessage, Action<string> logError)
    {
        lock (QueueLock)
        {
            Pending[path] = new Write(text, failureMessage, logError);
            if (!Running.ContainsKey(path))
                Running[path] = Task.Run(() => Drain(path));
        }
    }

    // 卸載時呼叫：等背景寫入做完，逾時回 false（不取消，已開始的那一份照樣寫完）。
    public static bool Flush(TimeSpan timeout)
    {
        Task[] tasks;
        lock (QueueLock)
            tasks = Running.Values.ToArray();
        try
        {
            return Task.WaitAll(tasks, timeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Drain(string path)
    {
        while (true)
        {
            Write next;
            lock (QueueLock)
            {
                if (!Pending.Remove(path, out next))
                {
                    Running.Remove(path);
                    return;
                }
            }

            try
            {
                FilesystemUtil.WriteAllTextSafe(path, next.Text);
            }
            catch (Exception e)
            {
                try
                {
                    next.LogError($"{next.FailureMessage}（原檔維持上一次的內容）:\n{e}");
                }
                catch (Exception)
                {
                    // 背景執行緒上不能讓例外逃出去；記錄失敗就只能放棄這一筆。
                }
            }
        }
    }
}
