using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TokLens;

internal static class PricingStudioTests
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Capture(FrameworkElement content, string path, int width, int height)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        content.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        RenderTargetBitmap image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        PngBitmapEncoder png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (FileStream output = File.Create(path)) png.Save(output);
    }
    private static PricingView.PricingRow ModelRow(PricingView view, string model)
    {
        foreach (PricingView.PricingRow row in Field<DataGrid>(view, "_grid").Items)
            if (string.Equals(row.ModelId, model, StringComparison.OrdinalIgnoreCase) || string.Equals(row.NormId, model, StringComparison.OrdinalIgnoreCase))
                return row;
        throw new Exception("missing model " + model);
    }
    private static void SelectModel(PricingView view, string model)
    {
        Field<DataGrid>(view, "_grid").SelectedItem = ModelRow(view, model);
    }
    private static PricingView.TierRateRow TierRow(PricingView view, string tier)
    {
        foreach (PricingView.TierRateRow row in Field<DataGrid>(view, "_tierGrid").Items)
            if (row.Tier == tier) return row;
        throw new Exception("missing tier " + tier);
    }
    private static void SelectTier(PricingView view, string tier)
    {
        Field<DataGrid>(view, "_tierGrid").SelectedItem = TierRow(view, tier);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "TokLens_PricingView_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string output = args.Length > 0 ? Path.GetFullPath(args[0]) : root; Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR", Path.Combine(root, "store"));
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Check(typeof(PricingView).Assembly.GetType("TokLens.PricingStudioWindow") == null &&
                  typeof(PricingView).Assembly.GetType("TokLens.TierPricingView") == null,
                  "legacy split pricing dialog and standalone tier tab are consolidated into PricingView");

            int changed = 0;
            Window host = new Window { Width = 960, Height = 720 };
            DesignSystem.ApplyTheme(host, true);
            PricingView view = new PricingView(root, true, new[] { "gpt-6-astra", "fixture-missing-model" }, delegate { changed++; }, null);
            host.Content = view;

            Check(Field<Border>(view, "_detailDrawer").Visibility == Visibility.Collapsed, "editor drawer starts collapsed to keep the catalog clean");
            Check(Field<Button>(view, "_syncCloudBtn").Visibility == Visibility.Visible, "cloud price sync stays visible in the unified toolbar");

            PricingView.PricingRow missing = ModelRow(view, "fixture-missing-model");
            Check(missing.SourceKind == "unpriced" && missing.InputText == "—" && !missing.HasOfficialRate, "unpriced active models remain distinct from free or official prices");

            SelectModel(view, "gpt-6-astra");
            Check(Field<Border>(view, "_detailDrawer").Visibility == Visibility.Visible && Field<Border>(view, "_tierSectionBox").Visibility == Visibility.Visible,
                "selecting a model with official tier quotes opens its unified tier and custom panel");

            DataGrid tierGrid = Field<DataGrid>(view, "_tierGrid");
            Field<DatePicker>(view, "_tierDatePicker").SelectedDate = new DateTime(2026, 11, 17);
            Check(tierGrid.Items.Count == 4 && tierGrid.IsReadOnly, "all four tiers appear separately in a read-only table");
            Check(TierRow(view, "fast").Price.input_per_m == 20 && TierRow(view, "fast").Price.output_per_m == 100, "Astra Fast uses shared official tier prices");
            Check(TierRow(view, "standard").Price.input_per_m == 10 && TierRow(view, "ultrafast").Price.input_per_m == 60, "standard and ultrafast retain independent rate entries");
            Check(TierRow(view, "flex").Price == null && TierRow(view, "flex").InputText == "—", "missing Flex price is not a zero price or guessed multiplier");

            SelectTier(view, "fast");
            Check(Field<TextBlock>(view, "_tierLongRatesText").Text.Contains("272,000") && Field<TextBlock>(view, "_tierLongRatesText").Text.Contains("$150"),
                "Fast detail shows per-request long-context threshold and output price");
            Check(Field<TextBlock>(view, "_tierCacheText").Text.Contains("12h") && Field<TextBlock>(view, "_tierCacheText").Text.Contains("未单独识别"),
                "12h price is visible with the current log-parsing limitation");
            Check(Field<TextBlock>(view, "_tierMetaText").Text.Contains("developers.openai.com") && Field<TextBlock>(view, "_tierMetaText").Text.Contains("2026-11-17"),
                "source and effective date are reviewable");
            Capture(view, Path.Combine(output, "pricing-unified-dark.png"), 940, 720);

            Field<DatePicker>(view, "_tierDatePicker").SelectedDate = new DateTime(2026, 11, 16);
            Check(TierRow(view, "fast").Price == null && Field<Button>(view, "_tierTodayBtn").Visibility == Visibility.Visible,
                "historic dates without recorded rates do not silently show a later quote");
            Click(Field<Button>(view, "_tierTodayBtn"));
            Check(Field<DatePicker>(view, "_tierDatePicker").SelectedDate.Value.Date == DateTime.Today, "missing historical prices offer a one-click jump to today");

            SelectModel(view, "gpt-5.5");
            Field<DatePicker>(view, "_tierDatePicker").SelectedDate = new DateTime(2026, 11, 17);
            SelectTier(view, "fast");
            Check(Field<TextBlock>(view, "_tierRangeText").Text.Contains("272,000") && Field<TextBlock>(view, "_tierRangeText").Text.Contains("未计价"),
                "unsupported long prompts show the maximum input limit");

            SelectModel(view, "fixture-missing-model");
            Check(Field<Border>(view, "_tierSectionBox").Visibility == Visibility.Collapsed, "models without official tier quotes hide the tier panel");

            SelectModel(view, "gpt-6-astra");
            Field<DatePicker>(view, "_tierDatePicker").SelectedDate = new DateTime(2026, 11, 17);
            Field<TextBox>(view, "_inputRateBox").Text = "7";
            Field<TextBox>(view, "_outputRateBox").Text = "11";
            Field<TextBox>(view, "_cacheReadRateBox").Text = "0.5";
            Field<TextBox>(view, "_cacheWriteRateBox").Text = "8";
            Click(Field<Button>(view, "_saveRuleBtn"));
            string custom = Path.Combine(root, ".toklens", "custom_prices.json");
            Check(File.Exists(custom) && changed == 1 && new PricingEngine(root).Lookup("gpt-6-astra").InputPerM == 7,
                "editing an isolated home writes only that home's ordinary override and invokes refresh");
            string saved = File.ReadAllText(custom);
            Check(TierRow(view, "fast").Price.input_per_m == 20 && File.ReadAllText(custom) == saved,
                "saving an ordinary override neither changes nor applies to Fast tier rates");

            DesignSystem.ApplyTheme(host, false);
            view.SetTheme(false);
            SelectModel(view, "gpt-6-astra");
            Field<DatePicker>(view, "_tierDatePicker").SelectedDate = new DateTime(2026, 11, 17);
            SelectTier(view, "fast");
            Capture(view, Path.Combine(output, "pricing-unified-light.png"), 940, 720);
            Check(File.ReadAllText(custom) == saved, "rendering both themes never rewrites user rules");
            host.Close();
            Console.WriteLine("PASS: " + checks + " pricing view assertions; screenshots: " + output);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.Shutdown(); }
    }
}
