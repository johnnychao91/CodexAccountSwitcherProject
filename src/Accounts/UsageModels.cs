using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexAccountSwitcher
{
    public enum UsageAvailability { Unknown, Available, Exhausted }

    public sealed class UsageWindow
    {
        public double? RemainingPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        internal bool InvalidPercentage { get; set; }
    }

    public sealed class AccountUsage
    {
        public UsageWindow FiveHour { get; set; }
        public UsageWindow Weekly { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public bool HasMalformedData { get; private set; }

        public UsageAvailability GetAvailability(DateTimeOffset now, TimeSpan maxAge)
        {
            // Never infer renewed allowance from an old sample or a reset timestamp.
            if (maxAge <= TimeSpan.Zero || FetchedAt == default(DateTimeOffset) || now < FetchedAt
                || now - FetchedAt > maxAge || ResetReached(FiveHour, now) || ResetReached(Weekly, now))
                return UsageAvailability.Unknown;

            // A known exhausted window blocks use even if another window is unavailable.
            if (ValidPercentage(FiveHour) && FiveHour.RemainingPercent.Value == 0
                || ValidPercentage(Weekly) && Weekly.RemainingPercent.Value == 0)
                return UsageAvailability.Exhausted;

            if (HasMalformedData || FiveHour != null && !ValidPercentage(FiveHour)
                || Weekly != null && !ValidPercentage(Weekly)) return UsageAvailability.Unknown;
            return FiveHour != null || Weekly != null ? UsageAvailability.Available : UsageAvailability.Unknown;
        }

        public static Account SelectFirstEligible(IEnumerable<Account> orderedAccounts,
            Func<Account, AccountUsage> usageForAccount, string currentAccountId, DateTimeOffset now, TimeSpan maxAge)
        {
            if (orderedAccounts == null) throw new ArgumentNullException("orderedAccounts");
            if (usageForAccount == null) throw new ArgumentNullException("usageForAccount");
            foreach (var account in orderedAccounts)
            {
                if (account == null || currentAccountId != null && String.Equals(account.Id, currentAccountId, StringComparison.Ordinal)) continue;
                // Null is also how the UI represents a failed or uncompleted lookup.
                AccountUsage usage = usageForAccount(account);
                if (usage != null && usage.GetAvailability(now, maxAge) == UsageAvailability.Available) return account;
            }
            return null;
        }

        private static bool ValidPercentage(UsageWindow window)
        {
            if (window == null || window.InvalidPercentage || !window.RemainingPercent.HasValue) return false;
            double value = window.RemainingPercent.Value;
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 100;
        }

        private static bool ResetReached(UsageWindow window, DateTimeOffset now)
        { return window != null && window.ResetsAt.HasValue && window.ResetsAt.Value <= now; }

        public static AccountUsage Parse(Dictionary<string, object> result)
        {
            var usage = new AccountUsage { FetchedAt = DateTimeOffset.UtcNow };
            Dictionary<string, object> limits = null;
            object byLimitId;
            if (result != null && result.TryGetValue("rateLimitsByLimitId", out byLimitId) && byLimitId != null)
            {
                // A model-specific bucket must never be shown as the main Codex allowance.
                var buckets = byLimitId as Dictionary<string, object>;
                if (buckets == null) usage.HasMalformedData = true;
                else limits = usage.ReadMap(buckets, "codex");
            }
            else limits = usage.ReadMap(result, "rateLimits");

            if (!IsCodexLimit(limits))
            {
                if (limits != null) usage.HasMalformedData = true;
                return usage;
            }
            usage.ReadWindow(usage.ReadMap(limits, "primary"));
            usage.ReadWindow(usage.ReadMap(limits, "secondary"));
            return usage;
        }

        private static bool IsCodexLimit(Dictionary<string, object> limits)
        {
            object limitId;
            if (limits == null) return false;
            if (!limits.TryGetValue("limitId", out limitId) || limitId == null) return true;
            return limitId is string && ((string)limitId == "" || (string)limitId == "codex");
        }

        private void ReadWindow(Dictionary<string, object> source)
        {
            if (source == null) return; // An omitted window is different from an invalid supplied window.
            double duration;
            if (!Number(source, "windowDurationMins", out duration) || (duration != 300 && duration != 10080))
            { HasMalformedData = true; return; }

            double used;
            var window = new UsageWindow();
            if (Number(source, "usedPercent", out used))
            {
                // Preserve the display contract while keeping out-of-range data ineligible.
                window.RemainingPercent = Math.Max(0, Math.Min(100, 100 - used));
                window.InvalidPercentage = used < 0 || used > 100;
            }
            else window.InvalidPercentage = true;
            if (window.InvalidPercentage) HasMalformedData = true;

            object resetValue;
            if (source.TryGetValue("resetsAt", out resetValue) && resetValue != null)
            {
                double reset;
                if (Number(source, "resetsAt", out reset) && reset == Math.Truncate(reset)
                    && reset >= -62135596800d && reset <= 253402300799d)
                    window.ResetsAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(reset);
                else HasMalformedData = true;
            }

            if (duration == 300)
            {
                if (FiveHour == null) FiveHour = window;
                else HasMalformedData = true;
            }
            else
            {
                if (Weekly == null) Weekly = window;
                else HasMalformedData = true;
            }
        }

        private Dictionary<string, object> ReadMap(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            var map = value as Dictionary<string, object>;
            if (map == null) HasMalformedData = true;
            return map;
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
