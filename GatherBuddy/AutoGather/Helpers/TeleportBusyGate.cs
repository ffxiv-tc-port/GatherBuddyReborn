namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// 自動採集送出傳送（以太之光、Lifestream 乙太網／回家）前的閘門：使用者正在忙就先不送，忙完再安靜 <see cref="QuietMs"/> 才送。
/// 純邏輯、不碰遊戲、不配置；忙不忙由呼叫端讀 Condition 後餵進來（null＝沒在忙），只在 framework 執行緒用。
/// 一段等待＝從第一次被擋到放行（或沒人再問）；開始／結束各回報一次，呼叫端據此各寫一行 log。
/// </summary>
public sealed class TeleportBusyGate
{
    public const long QuietMs = 2500;

    // 這麼久沒人問＝那次傳送已經不要了（目的地或清單變了），該段等待作廢。
    public const long StaleQueryMs = 2000;

    public readonly record struct Decision(bool Hold, bool BusyNow, bool Started, bool Released, long WaitedMs, string? Reason);

    public readonly record struct Abandoned(long WaitedMs, string? Reason);

    private bool    _everBusy;
    private long    _lastBusyAt;
    private string? _lastBusyReason;
    private bool    _holding;
    private long    _holdSince;
    private long    _lastQueryAt;
    private string? _holdReason;

    public bool Holding
        => _holding;

    /// <summary>每幀呼叫（含沒有要傳送的幀），記下最後一次忙的時間；回傳非 null＝一段等待因沒人再問而作廢。</summary>
    public Abandoned? Observe(long now, string? busyReason)
    {
        if (busyReason != null)
        {
            _everBusy       = true;
            _lastBusyAt     = now;
            _lastBusyReason = busyReason;
        }

        if (!_holding || now - _lastQueryAt <= StaleQueryMs)
            return null;

        _holding = false;
        return new Abandoned(_lastQueryAt - _holdSince, _holdReason);
    }

    /// <summary>要送傳送的那一幀呼叫；<see cref="Decision.Hold"/> 為 true＝這一幀不送，下一幀再問。</summary>
    public Decision Check(long now, string? busyReason)
    {
        Observe(now, busyReason);
        _lastQueryAt = now;

        var quiet = busyReason == null && (!_everBusy || now - _lastBusyAt >= QuietMs);
        if (quiet)
        {
            if (!_holding)
                return default;

            _holding = false;
            return new Decision(false, false, false, true, now - _holdSince, _holdReason);
        }

        var started = !_holding;
        if (started)
        {
            _holding    = true;
            _holdSince  = now;
            _holdReason = busyReason ?? _lastBusyReason;
        }

        return new Decision(true, busyReason != null, started, false, now - _holdSince, _holdReason);
    }

    /// <summary>停用自動採集時呼叫；回傳非 null＝有一段等待被取消。</summary>
    public Abandoned? Reset()
    {
        Abandoned? result = _holding ? new Abandoned(_lastQueryAt - _holdSince, _holdReason) : null;
        _holding        = false;
        _everBusy       = false;
        _lastBusyReason = null;
        _holdReason     = null;
        return result;
    }
}
