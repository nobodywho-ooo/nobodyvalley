using System.Collections;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley.Jobs;

/// <summary>Thrown inside a job to abort it with a reason the agent will see.</summary>
internal sealed class JobFailed : Exception
{
    public JobFailed(string reason) : base(reason) { }
}

/// <summary>A long-running action (walking, tilling, ...) driven one game tick at a time.</summary>
internal sealed class Job
{
    private static int nextId;
    public int Id { get; } = Interlocked.Increment(ref nextId);
    public string Action { get; init; } = "";
    /// <summary>Human-readable description, also shared with other players (e.g. "tilling soil").</summary>
    public string Activity { get; init; } = "";
    public Func<Job, IEnumerator> Body { get; init; } = null!;

    /// <summary>Keep ticking while a menu is open (for jobs that answer dialogues themselves).</summary>
    public bool RunDuringMenus { get; init; }

    /// <summary>Keep ticking even through cutscenes, warps and fades (the bus ride - the trip to the
    /// desert is a scripted sequence the job must watch to completion before it can report).</summary>
    public bool RunThroughEvents { get; set; }

    /// <summary>Set by the body as it progresses; reported when the job ends.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// Runs at most one <see cref="Job"/> at a time as a stack of nested coroutines.
///
/// Inside a coroutine:
///   yield return null;            -> wait one tick
///   yield return Wait.Ticks(n);   -> wait n ticks
///   yield return otherEnumerator; -> run a sub-step to completion, then resume
///   throw new JobFailed("...");   -> abort the whole job
/// </summary>
internal sealed class JobRunner
{
    private readonly IMonitor monitor;
    private readonly Action<Job, bool, string> onFinished;
    private readonly Stack<IEnumerator> stack = new();
    private int waitTicks;

    /// <summary>Abort any job that runs this long without finishing, so a frozen job (e.g. a walk
    /// when the festival warped the player away, or one waiting forever behind an unanswered
    /// dialogue) can never leave an agent doing nothing. Generous: the longest legit jobs (long
    /// walks, bulk clearing) run under a couple of minutes.</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(180);
    private DateTime startedAt;

    public Job? Current { get; private set; }

    public JobRunner(IMonitor monitor, Action<Job, bool, string> onFinished)
    {
        this.monitor = monitor;
        this.onFinished = onFinished;
    }

    public void Start(Job job)
    {
        Cancel("interrupted by a new action");
        Current = job;
        stack.Push(job.Body(job));
        waitTicks = 0;
        startedAt = DateTime.UtcNow;
        PlayerState.SetActivity(job.Activity);
    }

    public void Cancel(string reason)
    {
        if (Current == null)
            return;
        var job = Current;
        Reset();
        StopPlayer();
        onFinished(job, false, string.IsNullOrEmpty(job.Summary) ? reason : $"{job.Summary} ({reason})");
    }

    public void Tick()
    {
        if (Current == null)
            return;

        // A job that never finishes (stuck walk, a menu nobody answers) would otherwise freeze the
        // agent forever; bail out after a while so it hears about it and re-plans.
        if (DateTime.UtcNow - startedAt > MaxDuration)
        {
            Cancel("it's taking too long - stopped and re-planning");
            return;
        }

        // A ready check freezes every work job for everyone (and would otherwise stall for the whole
        // 180s timeout). Fail fast with a plain reason so the agent hears the day is ending and
        // waits for the new day instead of re-planning useless actions.
        if (!Current.RunDuringMenus && Game1.activeClickableMenu is ReadyCheckDialog)
        {
            Cancel("the day is ending (ready check) - no more actions this turn");
            return;
        }

        // Freeze while a menu, cutscene or screen transition is up; resume afterwards. The bus ride
        // keeps ticking through all of it (it answers its own ticket question and waits for the warp).
        //
        // Jobs that run during menus (clicking/closing them - shops, chests, level-ups, ...) must
        // keep running while the screen fade is black: the end-of-night menus (shipping summary,
        // level-ups) appear during the sleep fade, and a co-op day can't roll over until they're
        // dismissed. A click/close on a visible menu is safe while fading; moving/acting jobs still
        // wait for the transition to finish.
        bool menuJob = Current.RunDuringMenus;
        if (!Current.RunThroughEvents
            && ((Game1.activeClickableMenu != null && !menuJob)
                || Game1.eventUp
                || Game1.isWarping
                || (Game1.fadeToBlack && !menuJob)))
            return;

        if (waitTicks > 0)
        {
            waitTicks--;
            return;
        }

        var job = Current;
        try
        {
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop();
                    continue; // resume the parent in the same tick
                }
                switch (top.Current)
                {
                    case IEnumerator sub:
                        stack.Push(sub);
                        continue;
                    case Wait w:
                        waitTicks = w.Count;
                        return;
                    default:
                        return; // null: next tick
                }
            }
            Reset();
            onFinished(job, true, string.IsNullOrEmpty(job.Summary) ? "done" : job.Summary);
        }
        catch (JobFailed f)
        {
            Reset();
            StopPlayer();
            onFinished(job, false, string.IsNullOrEmpty(job.Summary) ? f.Message : $"{job.Summary}; stopped: {f.Message}");
        }
        catch (Exception ex)
        {
            monitor.Log($"Job {job.Action} crashed: {ex}", LogLevel.Error);
            Reset();
            StopPlayer();
            onFinished(job, false, $"internal error: {ex.Message}");
        }
    }

    private void Reset()
    {
        stack.Clear();
        Current = null;
        waitTicks = 0;
        PlayerState.SetActivity(null);
        PlayerState.SetClaim(null);
    }

    private static void StopPlayer()
    {
        var p = Game1.player;
        if (p == null)
            return;
        p.controller = null;
        p.Halt();
    }
}

internal sealed class Wait
{
    public int Count { get; }
    private Wait(int count) => Count = count;
    public static Wait Ticks(int n) => new(Math.Max(0, n - 1));
    public static Wait Seconds(double s) => Ticks((int)(s * 60));
}

/// <summary>Out-parameter for sub-steps (iterators can't have out/ref params).</summary>
internal sealed class StepResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public void Fail(string error)
    {
        Ok = false;
        Error = error;
    }
}
