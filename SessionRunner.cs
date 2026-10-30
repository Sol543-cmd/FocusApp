using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>
/// Runs a sequence of focus sessions and breaks.
/// A day plan runs "on the clock": every block ends at its scheduled time, so starting late
/// drops you into whatever block is happening now and the day never drifts.
/// </summary>
public class SessionRunner
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<PlanBlock> _queue = new();
    private DateTime[]? _scheduledEndsUtc;
    private bool _loop;
    private DateTime _endsAtUtc;
    private TimeSpan _pausedRemaining;

    public int Index { get; private set; } = -1;
    public int Count => _queue.Count;
    public int Round { get; private set; } = 1;
    public bool IsPaused { get; private set; }
    public bool IsRunning => Index >= 0;

    /// <summary>True while running a day plan on the clock (pausing isn't possible then).</summary>
    public bool IsOnSchedule => _scheduledEndsUtc != null;

    /// <summary>The blocks being run (the planner shows these same blocks while a day plan runs).</summary>
    public IReadOnlyList<PlanBlock> Blocks => _queue;

    /// <summary>When a day plan on the clock started (local time), or null.</summary>
    public DateTime? ScheduleStartLocal =>
        _scheduledEndsUtc != null && _queue.Count > 0
            ? _scheduledEndsUtc[0].AddMinutes(-_queue[0].Minutes).ToLocalTime()
            : null;

    public PlanBlock? Current => Index >= 0 && Index < _queue.Count ? _queue[Index] : null;

    public PlanBlock? Next
    {
        get
        {
            if (Index < 0) return null;
            if (Index + 1 < _queue.Count) return _queue[Index + 1];
            return _loop && _queue.Count > 0 ? _queue[0] : null;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (IsPaused) return _pausedRemaining;
            var r = _endsAtUtc - DateTime.UtcNow;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
    }

    public event Action? Tick;
    public event Action<PlanBlock>? BlockStarted;
    public event Action? Finished;

    public SessionRunner() => _timer.Tick += (_, _) => OnTick();

    /// <summary>Starts blocks one after another from now (Pomodoro, quick focus).</summary>
    public void Start(IEnumerable<PlanBlock> blocks, bool loop)
    {
        Stop();
        // Copy the blocks so editing the planner doesn't change what's running.
        _queue = blocks.Select(b => b.Clone()).ToList();
        if (_queue.Count == 0) return;
        _loop = loop;
        _scheduledEndsUtc = null;
        Round = 1;
        StartBlock(0);
        _timer.Start();
    }

    /// <summary>
    /// Starts a day plan whose first block began at <paramref name="dayStart"/> (local time).
    /// Returns false if the whole plan is already over.
    /// </summary>
    public bool StartOnSchedule(IEnumerable<PlanBlock> blocks, DateTime dayStart)
    {
        Stop();
        _queue = blocks.Select(b => b.Clone()).ToList();
        if (_queue.Count == 0) return false;

        _loop = false;
        Round = 1;
        var ends = new DateTime[_queue.Count];
        var t = dayStart.ToUniversalTime();
        for (int i = 0; i < _queue.Count; i++)
        {
            t = t.AddMinutes(_queue[i].Minutes);
            ends[i] = t;
        }
        _scheduledEndsUtc = ends;

        int index = CurrentScheduledIndex(0);
        if (index < 0)
        {
            _scheduledEndsUtc = null;
            _queue = new();
            return false;
        }
        StartBlock(index);
        _timer.Start();
        return true;
    }

    /// <summary>A copy of what's running, to save to disk. Null when nothing is running.</summary>
    public RunState? Capture()
    {
        if (!IsRunning) return null;
        return new RunState
        {
            Blocks = _queue.Select(b => b.Clone()).ToList(),
            ScheduledEndsUtc = _scheduledEndsUtc?.ToList(),
            Loop = _loop,
            Index = Index,
            Round = Round,
            EndsAtUtc = _endsAtUtc,
            Paused = IsPaused,
            PausedRemaining = _pausedRemaining
        };
    }

    /// <summary>
    /// Picks up a saved run where it left off. A day plan rejoins whatever block is current now.
    /// Returns false if the saved run has already finished.
    /// </summary>
    public bool Restore(RunState state)
    {
        Stop();
        if (state.Blocks.Count == 0) return false;
        _queue = state.Blocks.Select(b => b.Clone()).ToList();
        _loop = state.Loop;
        Round = Math.Max(1, state.Round);

        if (state.ScheduledEndsUtc != null && state.ScheduledEndsUtc.Count == _queue.Count)
        {
            _scheduledEndsUtc = state.ScheduledEndsUtc.Select(AsUtc).ToArray();
            int index = CurrentScheduledIndex(0);
            if (index < 0) return Abandon();
            StartBlock(index);
            _timer.Start();
            return true;
        }

        if (state.Index < 0 || state.Index >= _queue.Count) return Abandon();

        if (state.Paused)
        {
            Index = state.Index;
            IsPaused = true;
            _pausedRemaining = state.PausedRemaining > TimeSpan.Zero
                ? state.PausedRemaining
                : TimeSpan.FromMinutes(_queue[Index].Minutes);
            BlockStarted?.Invoke(_queue[Index]);
            Tick?.Invoke();
            _timer.Start();
            return true;
        }

        var endsAt = AsUtc(state.EndsAtUtc);
        if (endsAt <= DateTime.UtcNow) return Abandon();
        Index = state.Index;
        IsPaused = false;
        _endsAtUtc = endsAt;
        BlockStarted?.Invoke(_queue[Index]);
        Tick?.Invoke();
        _timer.Start();
        return true;
    }

    private bool Abandon()
    {
        _scheduledEndsUtc = null;
        _queue = new();
        Index = -1;
        return false;
    }

    private static DateTime AsUtc(DateTime d) =>
        d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>
    /// For a running day plan that you've edited: takes the new list of blocks (the current one must
    /// still be in it) and recalculates when each one ends.
    /// </summary>
    public bool Reschedule(IReadOnlyList<PlanBlock> blocks, DateTime dayStart)
    {
        if (_scheduledEndsUtc == null || Current is not PlanBlock current) return false;
        var list = blocks.ToList();
        int index = list.IndexOf(current);
        if (index < 0) return false;

        var ends = new DateTime[list.Count];
        var t = dayStart.ToUniversalTime();
        for (int i = 0; i < list.Count; i++)
        {
            t = t.AddMinutes(list[i].Minutes);
            ends[i] = t;
        }
        _queue = list;
        _scheduledEndsUtc = ends;
        Index = index;
        _endsAtUtc = ends[index];
        Tick?.Invoke();
        return true;
    }

    /// <summary>
    /// Adds time to the current block when it runs on its own (Pomodoro, Quick focus). Day plans
    /// lengthen the block in the plan instead, so the blocks after it make room.
    /// </summary>
    public bool ExtendCurrent(TimeSpan extra)
    {
        if (!IsRunning || IsOnSchedule || extra <= TimeSpan.Zero) return false;
        if (IsPaused) _pausedRemaining += extra;
        else _endsAtUtc = _endsAtUtc.Add(extra);
        if (Current is PlanBlock b) b.Minutes += (int)Math.Round(extra.TotalMinutes);
        Tick?.Invoke();
        return true;
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused || IsOnSchedule) return;
        _pausedRemaining = Remaining;
        IsPaused = true;
        Tick?.Invoke();
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        _endsAtUtc = DateTime.UtcNow + _pausedRemaining;
        IsPaused = false;
        Tick?.Invoke();
    }

    /// <summary>Moves on to the next block. On a schedule, the next block still ends at its planned time.</summary>
    public void Skip()
    {
        if (IsRunning) Advance(skipping: true);
    }

    public void Stop()
    {
        _timer.Stop();
        Index = -1;
        IsPaused = false;
        _scheduledEndsUtc = null;
        Tick?.Invoke();
    }

    private void StartBlock(int index)
    {
        Index = index;
        IsPaused = false;
        _endsAtUtc = _scheduledEndsUtc != null
            ? _scheduledEndsUtc[index]
            : DateTime.UtcNow + TimeSpan.FromMinutes(_queue[index].Minutes);
        BlockStarted?.Invoke(_queue[index]);
        Tick?.Invoke();
    }

    /// <summary>The first block at or after <paramref name="from"/> that hasn't ended yet, or -1.</summary>
    private int CurrentScheduledIndex(int from)
    {
        if (_scheduledEndsUtc == null) return -1;
        var now = DateTime.UtcNow;
        for (int i = Math.Max(0, from); i < _scheduledEndsUtc.Length; i++)
            if (_scheduledEndsUtc[i] > now) return i;
        return -1;
    }

    private void Advance(bool skipping)
    {
        int next;
        if (_scheduledEndsUtc != null)
        {
            // After sleep or a long pause in Windows, jump straight to whatever block is current now.
            next = skipping ? Index + 1 : CurrentScheduledIndex(Index + 1);
            if (next >= _queue.Count) next = -1;
            if (next >= 0 && skipping && _scheduledEndsUtc[next] <= DateTime.UtcNow)
                next = CurrentScheduledIndex(next);
        }
        else
        {
            next = Index + 1;
            if (next >= _queue.Count)
            {
                if (_loop)
                {
                    Round++;
                    next = 0;
                }
                else next = -1;
            }
        }

        if (next < 0)
        {
            Stop();
            Finished?.Invoke();
            return;
        }
        StartBlock(next);
    }

    private void OnTick()
    {
        if (!IsRunning || IsPaused) return;
        if (Remaining <= TimeSpan.Zero) Advance(skipping: false);
        else Tick?.Invoke();
    }
}
