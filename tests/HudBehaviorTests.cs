using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TokLens;

internal static class HudBehaviorTests
{
    private static int checks;
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static RecentRequestRecord Request(DateTime at, double cost, string session)
    {
        return new RecentRequestRecord { TimestampMs = UsageTiming.EpochMs(at), Date = at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Tool = "OpenAI Codex", Model = "fixture-model", SessionId = session, Workspace = "C:\\work\\demo", CostUsd = cost, FreshInput = 4000, ActualContextLimit = 100000, ContextLimitEvidence = "fixture:context_window" };
    }
    private static ScanResult Source(DateTime now)
    {
        return new ScanResult { GeneratedAt = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), WindowRequestsComplete = true };
    }
    private static void ClockAndCoverage()
    {
        DateTime now = DateTime.UtcNow;
        ScanResult scan = Source(now);
        for (int i = 0; i < 600; i++) scan.WindowRequests.Add(Request(now.AddSeconds(-i), .01, i % 2 == 0 ? "A" : "B"));
        scan.RecentRequests.AddRange(scan.WindowRequests.GetRange(0, 500));
        scan.Records.Add(new RecordBucket { Date = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CostUsd = 6, Turns = 600 });
        HudTelemetry h = HudTelemetry.FromScan(scan, now, false, 7);
        Check(h.WindowComplete && Math.Abs(h.Last15Usd - 6) < 1e-8, "all 600 requests are counted, independently of the 500-row UI list");
        Check(h.Latest.Count == 5 && h.Latest[0].TimestampMs > h.Latest[1].TimestampMs, "shared requests do not duplicate the recent display");
        h = HudTelemetry.FromScan(scan, now.AddMinutes(16), false, 7);
        Check(h.Last15Usd == 0 && Math.Abs(h.LastHourUsd - 6) < 1e-8, "the clock ages requests out of 15 minutes without a new scan");
        h = HudTelemetry.FromScan(scan, now.AddHours(3), false, 7);
        double sum = 0; foreach (double n in h.SparkTwoHours) sum += n;
        Check(h.LastHourUsd == 0 && sum == 0, "two-hour chart empties without a new request");
        h = HudTelemetry.FromScan(scan, now.ToLocalTime().Date.AddDays(1).AddMinutes(1), false, 7);
        Check(h.TodayUsd == 0, "today resets at the local calendar boundary");
        string selected = HudTelemetry.SessionKey(scan.WindowRequests[1]);
        h = HudTelemetry.FromScan(scan, now, false, 7, selected);
        Check(h.SessionId == "B" && h.SelectionKey == selected, "a fixed session is not replaced by another session's newer request");
        Check(h.ContextLimitIsActual && h.ContextLimit == 100000, "explicit session context limit is distinguished from nominal model limit");
        h.ActualServiceTier = "unknown"; h.RequestedServiceTier = "fast";
        Check(h.TierLabel.Contains("请求") && h.TierLabel.Contains("实际未知"), "requested Fast is not presented as delivered Fast");
        h.ActualServiceTier = "fast";
        Check(h.TierLabel == "实际档位 · Fast", "delivered Fast requires actual tier evidence");
        scan.WindowRequestsComplete = false;
        h = HudTelemetry.FromScan(scan, now, false, 7);
        Check(!h.WindowComplete && Math.Abs(h.Last15Usd - 6) < 1e-8, "partial provider coverage still uses every successfully parsed window record");
        scan.WindowRequests.Clear();
        h = HudTelemetry.FromScan(scan, now, false, 7);
        Check(!h.WindowComplete && h.Last15Usd < 6, "legacy samples stay explicitly incomplete");
    }
    private static void PointerAndPlacement()
    {
        HudPointerGesture pointer = new HudPointerGesture();
        pointer.Begin(new Point(100, 100), 1); Check(pointer.End(), "normal click toggles the panel");
        pointer.Begin(new Point(100, 100), 2); Check(!pointer.End(), "the second click of a double click has no extra action");
        pointer.Begin(new Point(100, 100), 1); Check(pointer.Move(new Point(112, 100), 4, 4) && !pointer.End(), "dragging never triggers a click when released");
        pointer.Begin(new Point(100, 100), 1); pointer.Cancel(); Check(!pointer.End(), "lost capture cancels an armed click");
        List<Rect> screens = new List<Rect> { new Rect(0, 0, 1920, 1040), new Rect(-2560, -100, 2560, 1400) };
        Check(HudPlacement.BestScreen(new Rect(-2300, 100, 250, 56), screens) == 1, "negative-coordinate secondary monitor is recovered from saved bounds");
        Rect restored = HudPlacement.AtAnchor(screens[1], 375, 84, 1, .5);
        Check(restored.Right == screens[1].Right && restored.Left >= screens[1].Left, "a 150 percent DPI size keeps its right-edge anchor");
        Rect fallback = HudPlacement.Clamp(new Rect(-2300, 1200, 375, 84), screens[0]);
        Check(screens[0].Contains(fallback), "unplugged monitor coordinates are clamped to the surviving work area");
    }
    private static void Alerts()
    {
        DateTime now = DateTime.UtcNow;
        List<RecentRequestRecord> rows = new List<RecentRequestRecord> { Request(now.AddMinutes(-1), 100, "A") };
        BurnRateGuard guard = new BurnRateGuard();
        Check(guard.Evaluate(null, rows, false, 7, now).Count == 0, "one expensive request does not claim an agent is out of control");
        rows.Clear(); for (int i = 0; i < 12; i++) rows.Add(Request(now.AddSeconds(-i * 30), .5, "A"));
        List<BurnRateAlert> alerts = guard.Evaluate(null, rows, false, 7, now);
        Check(alerts.Count == 1 && !alerts[0].IsCritical && alerts[0].Message.Contains("不代表实付"), "sustained spend is an explained estimate warning");
        Check(guard.Evaluate(null, rows, false, 7, now.AddSeconds(1)).Count == 0, "alert cooldown prevents repeated prompts");
        rows.Clear(); for (int period = 0; period < 8; period++) for (int i = 0; i < 12; i++) rows.Add(Request(now.AddMinutes(-period * 15).AddSeconds(-i * 30), .5, "A"));
        guard.Reset(); Check(guard.Evaluate(null, rows, false, 7, now).Count == 0, "ordinary sustained active usage is compared with active windows instead of 168 idle hours");
    }
    private static void RaiseClick(Border surface, int count)
    {
        foreach (RoutedEvent routed in new[] { UIElement.MouseLeftButtonDownEvent, UIElement.MouseLeftButtonUpEvent })
        {
            MouseButtonEventArgs args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed };
            FieldInfo clickCount = typeof(MouseButtonEventArgs).GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(clickCount != null, "WPF event fixture can set the observed click count");
            clickCount.SetValue(args, count);
            surface.RaiseEvent(args);
        }
    }
    private static void WindowBehavior()
    {
        DateTime now = DateTime.UtcNow; ScanResult scan = Source(now); RecentRequestRecord row = Request(now.AddSeconds(-10), 2, "A"); scan.WindowRequests.Add(row); scan.RecentRequests.Add(row);
        MiniHudWindow hud = new MiniHudWindow(false, null, null);
        try
        {
            hud.UpdateSource(scan, false, 7, "");
            Border surface = Field<Border>(hud, "_surface"); Popup popup = Field<Popup>(hud, "_cardPopup");
            Check(surface.Child is Grid && surface.Width != 44, "the labelled capsule is the default");
            RaiseClick(surface, 1); Check(popup.IsOpen && !popup.StaysOpen, "real routed click opens a panel that dismisses on outside clicks");
            RaiseClick(surface, 2); Check(popup.IsOpen, "a routed double click does not cycle or hide the displayed metric");
            using (HwndSource input = new HwndSource(new HwndSourceParameters("TokLens HUD test input") { WindowStyle = 0, Width = 1, Height = 1 }))
            {
                KeyEventArgs escape = new KeyEventArgs(Keyboard.PrimaryDevice, input, Environment.TickCount, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                ((UIElement)popup.Child).RaiseEvent(escape); Check(!popup.IsOpen, "Escape from inside the popup dismisses it");
            }
            hud.SetAlert("API 估值升高", now.AddSeconds(20));
            Check(Field<HudTelemetry>(hud, "_data").Alert && Field<TextBlock>(hud, "_context").Text == "4%", "an alert retains the context number");
            hud.RefreshAt(now.AddMinutes(21));
            Check(!Field<HudTelemetry>(hud, "_data").Alert && Field<HudTelemetry>(hud, "_data").Last15Usd == 0, "clock clears expired alerts and rolling totals together");
            Check(Field<TextBlock>(hud, "_status").Text.Contains("陈旧"), "no successful collection is distinct from an idle agent");
            hud.UpdateSource(scan, false, 7, "fixture failure"); Check(Field<TextBlock>(hud, "_status").Text.Contains("采集异常"), "collection failures do not look like no usage");
        }
        finally { hud.Close(); }
        HudPreferences saved = new HudPreferences { Locked = true, Opacity = .65, Style = "ball", SessionKey = "tool\nsession", ScreenName = "fixture", X = -2500, Y = 30, AnchorX = .1, AnchorY = .2 };
        saved.Save(); HudPreferences loaded = HudPreferences.Load();
        Check(loaded.Locked && loaded.Opacity == .65 && loaded.Style == "ball" && loaded.SessionKey == saved.SessionKey && loaded.X == -2500, "style, lock, opacity, fixed session and monitor coordinates survive restart");
        Check(HudPreferences.FilePath.StartsWith(SyncService.AppConfigDir, StringComparison.OrdinalIgnoreCase), "HUD preferences honor the isolated config directory");
    }
    private static void UnpricedAmounts()
    {
        DateTime now = DateTime.UtcNow; ScanResult scan = Source(now);
        RecentRequestRecord unknown = Request(now.AddSeconds(-5), 99, "unknown-price"); unknown.CostBasis = "unpriced";
        scan.WindowRequests.Add(unknown); scan.RecentRequests.Add(unknown);
        scan.Records.Add(new RecordBucket { Date = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Turns = 1, CostUsd = 99, RateLabel = "stale label", CostBasis = "unpriced" });
        HudTelemetry h = HudTelemetry.FromScan(scan, now, false, 7);
        Check(h.UnpricedRequests == 1 && h.TodayUsd == 0 && h.Cost == "—", "metadata excludes stale numeric amounts and all-unpriced today is unknown rather than zero");
        Check(h.Last15UnpricedRequests == 1 && h.Last15Usd == 0 && h.TwoHourUnpricedRequests == 1, "rolling windows retain unknown-price coverage separately from known money");
        MiniHudWindow hud = new MiniHudWindow(false, null, null);
        try
        {
            hud.UpdateSource(scan, false, 7, ""); RaiseClick(Field<Border>(hud, "_surface"), 1);
            Check(Field<TextBlock>(hud, "_cardToday").Text == "—" && Field<TextBlock>(hud, "_cardHour").Text.Contains("未计价"), "HUD does not render all-unpriced usage as a free day or zero hourly rate");
            RecentRequestRecord priced = Request(now.AddSeconds(-30), 1.5, "reported-price"); priced.CostBasis = "reported";
            scan.WindowRequests.Add(priced); scan.RecentRequests.Add(priced);
            scan.Records.Add(new RecordBucket { Date = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Turns = 1, CostUsd = 1.5, CostBasis = "reported" });
            hud.UpdateSource(scan, false, 7, "");
            Check(Field<TextBlock>(hud, "_cardToday").Text == FormatterUtil.FormatMoney(1.5, false, 7) + " *", "partially priced today shows only known money plus an explicit missing-price marker");
            Check(Field<TextBlock>(hud, "_cardHour").Text.Contains("已计价部分") && Field<HudTelemetry>(hud, "_data").Last15Usd == 1.5, "mixed rolling rate is labelled as the priced portion");
            hud.RefreshAt(now.AddMinutes(16));
            Check(Field<HudTelemetry>(hud, "_data").Last15UnpricedRequests == 0 && !Field<TextBlock>(hud, "_cardHour").Text.Contains("已计价部分"), "unknown-price coverage ages out with its requests");
        }
        finally { hud.Close(); }
    }
    [STAThread]
    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "TokLens_HudTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", temp);
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { ClockAndCoverage(); PointerAndPlacement(); Alerts(); UnpricedAmounts(); WindowBehavior(); Console.WriteLine("HUD behavior: " + checks + " checks passed"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }
}
