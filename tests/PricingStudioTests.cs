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
    private static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    private static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    private static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    private static void Capture(FrameworkElement content,string path,int width,int height)
    {
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        content.Dispatcher.Invoke(DispatcherPriority.Render,new Action(delegate {}));content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        RenderTargetBitmap image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(content);
        PngBitmapEncoder png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(FileStream output=File.Create(path))png.Save(output);
    }
    private static TierPricingView.PriceRow Row(TierPricingView view,string tier)
    {foreach(TierPricingView.PriceRow row in Field<DataGrid>(view,"_grid").Items)if(row.Tier==tier)return row;throw new Exception("missing tier "+tier);}
    private static void Model(TierPricingView view,string model)
    {ComboBox box=Field<ComboBox>(view,"_modelBox");foreach(TierPricingView.ModelOption item in box.Items)if(item.Model==model){box.SelectedItem=item;return;}throw new Exception("missing model "+model);}
    private static void Select(TierPricingView view,string tier){Field<DataGrid>(view,"_grid").SelectedItem=Row(view,tier);}
    [STAThread]
    public static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_PricingStudio_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string output=args.Length>0?args[0]:root;Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",Path.Combine(root,"store"));
        Application app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        try
        {
            int changed=0;
            PricingStudioWindow window=new PricingStudioWindow(null,root,true,new[]{"gpt-6-astra","fixture-missing-model"},delegate {changed++;});
            TierPricingView view=Field<TierPricingView>(window,"_tierPane");DataGrid grid=Field<DataGrid>(view,"_grid");
            Field<DatePicker>(view,"_date").SelectedDate=new DateTime(2026,11,17);
            Check(Field<bool>(window,"_showTierPrices")&&view.Visibility==Visibility.Visible,"pricing studio opens on tier reference prices");
            Check(grid.Items.Count==4&&grid.IsReadOnly,"all four tiers appear separately in a read-only table");
            Check(Row(view,"fast").Price.input_per_m==20&&Row(view,"fast").Price.output_per_m==100,"Astra Fast uses shared official tier prices rather than standard Lookup");
            Check(Row(view,"standard").Price.input_per_m==10&&Row(view,"ultrafast").Price.input_per_m==60,"standard and ultrafast retain independent rate entries");
            Check(Row(view,"flex").Price==null&&Row(view,"flex").InputText=="—","missing Flex price is not a zero price or guessed multiplier");
            Select(view,"fast");
            Check(Field<TextBlock>(view,"_longRates").Text.Contains("272,000")&&Field<TextBlock>(view,"_longRates").Text.Contains("$150"),"Fast detail shows per-request long-context threshold and output price");
            Check(Field<TextBlock>(view,"_cacheDuration").Text.Contains("12h")&&Field<TextBlock>(view,"_cacheDuration").Text.Contains("未单独识别"),"12h price is visible with the current log-parsing limitation");
            Check(Field<TextBlock>(view,"_source").Text.Contains("developers.openai.com")&&Field<TextBlock>(view,"_effective").Text.Contains("2026-11-17"),"source and effective date are reviewable");
            Capture((FrameworkElement)window.Content,Path.Combine(output,"pricing-tiers-dark.png"),940,720);
            Capture((FrameworkElement)window.Content,Path.Combine(output,"pricing-tiers-narrow.png"),700,480);
            Check(grid.ActualWidth<=view.ActualWidth&&grid.ActualWidth>600,"tier table fits the minimum window width");
            double columns=0;foreach(DataGridColumn column in grid.Columns)columns+=column.ActualWidth;
            Check(columns<=grid.ActualWidth,"all price columns remain visible at the minimum width");

            Field<DatePicker>(view,"_date").SelectedDate=new DateTime(2026,11,16);
            Check(Row(view,"fast").Price==null&&Field<Button>(view,"_viewCurrent").Visibility==Visibility.Visible,"historic dates without recorded rates do not silently show a later quote");
            Click(Field<Button>(view,"_viewCurrent"));Check(Field<DatePicker>(view,"_date").SelectedDate.Value.Date==DateTime.Today,"missing historical prices offer an explicit current-date action");
            Model(view,"gpt-5.5");Select(view,"fast");Check(Field<TextBlock>(view,"_range").Text.Contains("272,000")&&Field<TextBlock>(view,"_range").Text.Contains("未计价"),"unsupported long prompts have a visible maximum and unpriced outcome");
            Model(view,"fixture-missing-model");Check(Row(view,"fast").Price==null&&Row(view,"standard").Price==null,"active unknown models remain visible without invented prices");
            Model(view,"gpt-6-astra");Select(view,"fast");
            Check(Field<Button>(window,"_syncCloudBtn").Visibility==Visibility.Collapsed,"ordinary cloud-price sync is not advertised as a tier-table update");
            Click(Field<Button>(window,"_standardTab"));
            Check(!Field<bool>(window,"_showTierPrices")&&Field<UIElement>(window,"_standardPane").Visibility==Visibility.Visible,"ordinary custom-price editor remains available on its own page");
            foreach(PricingStudioWindow.PricingStudioRow row in Field<DataGrid>(window,"_grid").Items)
                if(row.ModelId=="fixture-missing-model")Check(row.SourceKind=="unpriced"&&row.InputText=="—"&&!row.HasOfficialRate,"ordinary price table also distinguishes missing quotes from free or official prices");
            Field<TextBox>(window,"_modelIdInput").Text="gpt-6-astra";Field<TextBox>(window,"_inputRateBox").Text="7";Field<TextBox>(window,"_outputRateBox").Text="11";Field<TextBox>(window,"_cacheReadRateBox").Text="0.5";Field<TextBox>(window,"_cacheWriteRateBox").Text="8";
            Click(Field<Button>(window,"_saveRuleBtn"));
            string custom=Path.Combine(root,".toklens","custom_prices.json");
            Check(File.Exists(custom)&&changed==1&&new PricingEngine(root).Lookup("gpt-6-astra").InputPerM==7,"editing an isolated home writes only that home's ordinary override and invokes refresh");
            string saved=File.ReadAllText(custom);
            Capture((FrameworkElement)window.Content,Path.Combine(output,"pricing-standard-editor-dark.png"),940,720);
            Click(Field<Button>(window,"_tierTab"));Check(Row(view,"fast").Price.input_per_m==20&&File.ReadAllText(custom)==saved,"viewing tier prices neither changes the ordinary override nor applies it to Fast");
            window.Close();

            PricingStudioWindow light=new PricingStudioWindow(null,root,false,new[]{"gpt-6-astra"},null);
            TierPricingView lightView=Field<TierPricingView>(light,"_tierPane");Field<DatePicker>(lightView,"_date").SelectedDate=new DateTime(2026,11,17);Select(lightView,"fast");
            Capture((FrameworkElement)light.Content,Path.Combine(output,"pricing-tiers-light.png"),940,720);
            Check(File.ReadAllText(custom)==saved,"opening and rendering both themes does not rewrite user rules");light.Close();
            Console.WriteLine("PASS: "+checks+" pricing studio assertions; screenshots: "+output);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
