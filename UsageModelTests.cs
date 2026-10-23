using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexAccountSwitcher
{
    public static class UsageModelTests
    {
        private static void Check(bool condition) { if (!condition) throw new Exception("Usage model assertion failed."); }
        private static Dictionary<string, object> Window(object duration, object used, object reset)
        {
            return new Dictionary<string, object> { { "windowDurationMins", duration }, { "usedPercent", used }, { "resetsAt", reset } };
        }
        private static Dictionary<string, object> Limits(object primary, object secondary)
        {
            return new Dictionary<string, object> { { "primary", primary }, { "secondary", secondary } };
        }
        private static Dictionary<string, object> Legacy(object primary, object secondary)
        {
            return new Dictionary<string, object> { { "rateLimits", Limits(primary, secondary) } };
        }

        private static void FindProgressBars(DependencyObject parent, List<ProgressBar> bars)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                var bar = child as ProgressBar;
                if (bar != null) bars.Add(bar);
                FindProgressBars(child, bars);
            }
        }

        private static ProgressBar UsageBar(List<ProgressBar> bars, string property)
        {
            foreach (var bar in bars)
            {
                Binding binding = BindingOperations.GetBinding(bar, ProgressBar.ValueProperty);
                if (binding != null && binding.Path != null && binding.Path.Path == property) return bar;
            }
            throw new Exception("The usage card is missing its " + property + " progress bar.");
        }

        public static void Register(Action<string, Action> test)
        {
            test("Usage cards create their actual WPF progress bindings and update after a refresh", delegate
            {
                // Demo mode only constructs synthetic rows; it never loads the account vault.
                var view = new MainView(true);
                try
                {
                    var content = (FrameworkElement)view.Window.Content;
                    var bounds = new Size(view.Window.Width, view.Window.Height);
                    content.Measure(bounds);
                    content.Arrange(new Rect(new Point(), bounds));
                    content.UpdateLayout();
                    var list = (ListBox)view.Window.FindName("AccountList");
                    var card = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    Check(card != null);
                    var bars = new List<ProgressBar>();
                    FindProgressBars(card, bars);
                    Check(bars.Count == 2);
                    ProgressBar fiveHour = UsageBar(bars, "FiveHourValue");
                    ProgressBar weekly = UsageBar(bars, "WeeklyValue");
                    Check(fiveHour.Value == 80 && weekly.Value == 60);
                    Check(fiveHour.Visibility == Visibility.Visible && weekly.Visibility == Visibility.Visible);

                    var row = (AccountRow)list.Items[0];
                    row.SetUsage(new AccountUsage
                    {
                        FiveHour = new UsageWindow { RemainingPercent = 12.5 },
                        Weekly = new UsageWindow { RemainingPercent = 0 },
                        FetchedAt = DateTimeOffset.UtcNow
                    });
                    view.Window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(delegate { }));
                    content.UpdateLayout();
                    Check(fiveHour.Value == 12.5 && weekly.Value == 0);
                    Check(fiveHour.Visibility == Visibility.Visible && weekly.Visibility == Visibility.Visible);
                }
                finally { view.Window.Close(); }
            });
            test("Usage selects the Codex bucket instead of other model or legacy buckets", delegate
            {
                var result = Legacy(Window(300, 99, null), Window(10080, 99, null));
                result["rateLimitsByLimitId"] = new Dictionary<string, object>
                {
                    { "codex_other_model", Limits(Window(300, 1, null), Window(10080, 2, null)) },
                    { "codex", Limits(Window(300, 40, null), Window(10080, 75, null)) }
                };
                var value = AccountUsage.Parse(result);
                Check(value.FiveHour.RemainingPercent == 60 && value.Weekly.RemainingPercent == 25);
            });
            test("Usage does not substitute a model bucket when the Codex bucket is absent", delegate
            {
                var result = Legacy(Window(300, 20, null), Window(10080, 20, null));
                result["rateLimitsByLimitId"] = new Dictionary<string, object>
                { { "codex_other_model", Limits(Window(300, 1, null), Window(10080, 2, null)) } };
                var value = AccountUsage.Parse(result);
                Check(value.FiveHour == null && value.Weekly == null);
                result["rateLimitsByLimitId"] = new Dictionary<string, object>();
                value = AccountUsage.Parse(result);
                Check(value.FiveHour == null && value.Weekly == null);
            });
            test("Usage handles legacy snapshots and null newer buckets", delegate
            {
                var result = Legacy(Window(300, 12.5m, null), Window(10080, 70L, null));
                var value = AccountUsage.Parse(result);
                Check(value.FiveHour.RemainingPercent == 87.5 && value.Weekly.RemainingPercent == 30);
                result["rateLimitsByLimitId"] = null;
                value = AccountUsage.Parse(result);
                Check(value.FiveHour.RemainingPercent == 87.5 && value.Weekly.RemainingPercent == 30);
            });
            test("Usage identifies windows by duration even when their order is reversed", delegate
            {
                var value = AccountUsage.Parse(Legacy(Window(10080, 20, null), Window(300, 80, null)));
                Check(value.FiveHour.RemainingPercent == 20 && value.Weekly.RemainingPercent == 80);
                value = AccountUsage.Parse(Legacy(Window(60, 20, null), Window(1440, 80, null)));
                Check(value.FiveHour == null && value.Weekly == null);
            });
            test("Usage accepts only an unspecified or Codex legacy limit identity", delegate
            {
                var result = Legacy(Window(300, 40, null), Window(10080, 75, null));
                var limits = (Dictionary<string, object>)result["rateLimits"];
                foreach (object limitId in new object[] { null, "", "codex" })
                {
                    limits["limitId"] = limitId;
                    var value = AccountUsage.Parse(result);
                    Check(value.FiveHour.RemainingPercent == 60 && value.Weekly.RemainingPercent == 25);
                }
                foreach (object limitId in new object[] { "codex_other_model", "CODEX", " ", 1, false })
                {
                    limits["limitId"] = limitId;
                    var value = AccountUsage.Parse(result);
                    Check(value.FiveHour == null && value.Weekly == null);
                }
            });
            test("Usage rejects a Codex map entry with a conflicting limit identity", delegate
            {
                var limits = Limits(Window(300, 40, null), Window(10080, 75, null));
                var result = Legacy(Window(300, 0, null), Window(10080, 0, null));
                result["rateLimitsByLimitId"] = new Dictionary<string, object> { { "codex", limits } };
                limits["limitId"] = "codex_other_model";
                var value = AccountUsage.Parse(result);
                Check(value.FiveHour == null && value.Weekly == null);
                limits["limitId"] = "codex";
                value = AccountUsage.Parse(result);
                Check(value.FiveHour.RemainingPercent == 60 && value.Weekly.RemainingPercent == 25);
            });
            test("Usage leaves missing or malformed windows unavailable", delegate
            {
                var value = AccountUsage.Parse(null);
                Check(value.FiveHour == null && value.Weekly == null);
                value = AccountUsage.Parse(new Dictionary<string, object>());
                Check(value.FiveHour == null && value.Weekly == null);
                value = AccountUsage.Parse(Legacy(null, "invalid-window"));
                Check(value.FiveHour == null && value.Weekly == null);
                value = AccountUsage.Parse(Legacy(Window(300, 10, null), null));
                Check(value.FiveHour.RemainingPercent == 90 && value.Weekly == null);
            });
            test("Usage does not convert missing usage to a full allowance", delegate
            {
                var source = Window(300, null, 0);
                var value = AccountUsage.Parse(Legacy(source, null));
                Check(value.FiveHour != null && value.FiveHour.RemainingPercent == null);
                source.Remove("usedPercent");
                value = AccountUsage.Parse(Legacy(source, null));
                Check(value.FiveHour.RemainingPercent == null && value.FiveHour.ResetsAt.HasValue);
            });
            test("Usage clamps the remaining allowance between zero and one hundred", delegate
            {
                var value = AccountUsage.Parse(Legacy(Window(300, -20, null), Window(10080, 120, null)));
                Check(value.FiveHour.RemainingPercent == 100 && value.Weekly.RemainingPercent == 0);
                value = AccountUsage.Parse(Legacy(Window(300, 100, null), Window(10080, 0, null)));
                Check(value.FiveHour.RemainingPercent == 0 && value.Weekly.RemainingPercent == 100);
            });
            test("Usage reset times are UTC Unix seconds and include a fetch timestamp", delegate
            {
                var before = DateTimeOffset.UtcNow;
                var value = AccountUsage.Parse(Legacy(Window(300, 0, 1704067200L), Window(10080, 0, 0)));
                var after = DateTimeOffset.UtcNow;
                Check(value.FiveHour.ResetsAt == new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
                Check(value.Weekly.ResetsAt == new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero));
                Check(value.FetchedAt >= before && value.FetchedAt <= after && value.FetchedAt.Offset == TimeSpan.Zero);
            });
            test("Usage rejects nonnumeric percentages and nonfinite values", delegate
            {
                var invalid = new object[] { null, "20", true, false, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, new object[0], new Dictionary<string, object>() };
                foreach (object input in invalid)
                {
                    var value = AccountUsage.Parse(Legacy(Window(300, input, null), null));
                    Check(value.FiveHour.RemainingPercent == null);
                }
            });
            test("Usage rejects invalid reset timestamps without hiding valid usage", delegate
            {
                var invalid = new object[] { null, "1704067200", true, Double.NaN, Double.PositiveInfinity, 0.5, 253402300800d, -62135596801d, Int64.MaxValue };
                foreach (object input in invalid)
                {
                    var value = AccountUsage.Parse(Legacy(Window(300, 25, input), null));
                    Check(value.FiveHour.RemainingPercent == 75 && value.FiveHour.ResetsAt == null);
                }
                var missing = Window(300, 25, null); missing.Remove("resetsAt");
                Check(AccountUsage.Parse(Legacy(missing, null)).FiveHour.ResetsAt == null);
            });
            test("Usage rejects malformed durations and malformed bucket maps", delegate
            {
                var invalid = new object[] { null, "300", true, Double.NaN, Double.PositiveInfinity, 300.5, 0 };
                foreach (object input in invalid)
                {
                    var value = AccountUsage.Parse(Legacy(Window(input, 25, 0), null));
                    Check(value.FiveHour == null && value.Weekly == null);
                }
                var result = Legacy(Window(300, 25, 0), null);
                result["rateLimitsByLimitId"] = "invalid-map";
                var malformed = AccountUsage.Parse(result);
                Check(malformed.FiveHour == null && malformed.Weekly == null);
            });
        }
    }
}
