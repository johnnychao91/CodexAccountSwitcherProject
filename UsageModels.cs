using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexAccountSwitcher
{
    public sealed class UsageWindow
    {
        public double? RemainingPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
    }

    public sealed class AccountUsage
    {
        public UsageWindow FiveHour { get; set; }
        public UsageWindow Weekly { get; set; }
        public DateTimeOffset FetchedAt { get; set; }

        public static AccountUsage Parse(Dictionary<string, object> result)
        {
            var usage = new AccountUsage { FetchedAt = DateTimeOffset.UtcNow };
            Dictionary<string, object> limits = null;
            object byLimitId;
            if (result != null && result.TryGetValue("rateLimitsByLimitId", out byLimitId) && byLimitId != null)
            {
                // A model-specific bucket must never be shown as the main Codex allowance.
                limits = Map(byLimitId as Dictionary<string, object>, "codex");
            }
            else
            {
                limits = Map(result, "rateLimits");
            }

            if (!IsCodexLimit(limits)) return usage;
            ReadWindow(usage, Map(limits, "primary"));
            ReadWindow(usage, Map(limits, "secondary"));
            return usage;
        }

        private static bool IsCodexLimit(Dictionary<string, object> limits)
        {
            object limitId;
            if (limits == null) return false;
            if (!limits.TryGetValue("limitId", out limitId) || limitId == null) return true;
            return limitId is string && ((string)limitId == "" || (string)limitId == "codex");
        }

        private static void ReadWindow(AccountUsage usage, Dictionary<string, object> source)
        {
            double duration;
            if (!Number(source, "windowDurationMins", out duration) || (duration != 300 && duration != 10080)) return;

            double used;
            var window = new UsageWindow();
            if (Number(source, "usedPercent", out used))
                window.RemainingPercent = Math.Max(0, Math.Min(100, 100 - used));

            double reset;
            if (Number(source, "resetsAt", out reset) && reset == Math.Truncate(reset)
                && reset >= -62135596800d && reset <= 253402300799d)
            {
                window.ResetsAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(reset);
            }

            if (duration == 300 && usage.FiveHour == null) usage.FiveHour = window;
            if (duration == 10080 && usage.Weekly == null) usage.Weekly = window;
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static bool Number(Dictionary<string, object> source, string key, out double number)
        {
            number = 0;
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return false;
            if (!(value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint
                || value is long || value is ulong || value is float || value is double || value is decimal)) return false;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !Double.IsNaN(number) && !Double.IsInfinity(number);
        }
    }
}
