using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher
{
    public static class SwitchWorkflow
    {
        public static async Task RunAsync(Func<Task<bool>> blocked, Action apply, Action launch,
            CancellationToken cancellation, TimeSpan pollDelay, TimeSpan quietPeriod, TimeSpan maxWait)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                bool waiting = await blocked();
                cancellation.ThrowIfCancellationRequested();
                if (!waiting)
                {
                    // Require a quiet interval and a fresh snapshot, not one transient empty list.
                    await Task.Delay(quietPeriod, cancellation);
                    waiting = await blocked();
                    cancellation.ThrowIfCancellationRequested();
                    if (!waiting) break;
                }
                if (elapsed.Elapsed >= maxWait)
                    throw new UserError("等待已超過 5 分鐘，已取消這次切換，帳號尚未變更。請關閉畫面列出的來源後，再按一次切換。");
                await Task.Delay(pollDelay, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            apply();
            // After committing credentials, complete the launch instead of leaving a half-finished operation.
            launch();
        }
    }
}
