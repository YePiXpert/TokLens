using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TokLens;

internal static class ProductUiTests
{
    private static int checks;
    private static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(target);}
    private static void Call(object target,string name,params object[] args){target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(target,args);}
    private static void Layout(FrameworkElement root,double w,double h){root.Measure(new Size(w,h));root.Arrange(new Rect(0,0,w,h));root.UpdateLayout();}
    private static void Capture(FrameworkElement root,string file,int w,int h)
    {
        Layout(root,w,h);root.Dispatcher.Invoke(DispatcherPriority.Render,new Action(delegate {}));Layout(root,w,h);
        RenderTargetBitmap bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(FileStream stream=File.Create(file))encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T)yield return (T)root;
        foreach(object child in LogicalTreeHelper.GetChildren(root))if(child is DependencyObject)foreach(T item in Descendants<T>((DependencyObject)child))yield return item;
    }
    private static T Tagged<T>(DependencyObject root,string tag) where T:FrameworkElement
    {
        foreach(T item in Descendants<T>(root))if(Equals(item.Tag,tag))return item;
        throw new Exception("missing element "+tag);
    }
    private static void Click(DependencyObject root,string tag){Tagged<Button>(root,tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    [STAThread]
    public static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetTempPath(),"TokLens_UiTests_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string output=args.Length>0?Path.GetFullPath(args[0]):root;Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TOKLENS_CONFIG_DIR",Path.Combine(root,"store"));
        Application app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        try
        {
            MainWindow main=new MainWindow(root,true);string cfgPath=SyncService.ConfigFilePath;string cfgBefore=File.Exists(cfgPath)?File.ReadAllText(cfgPath):null;main.RunHeadlessUiSelfTest();
            Border[] nav=Field<Border[]>(main,"_navItemBorders");int visible=0;foreach(Border b in nav)if(b!=null&&b.Visibility==Visibility.Visible)visible++;
            Check(visible==5,"five primary navigation destinations");Check(typeof(MainWindow).Assembly.GetType("TokLens.WhatIfSimulatorView")==null,"simulator code removed from build");
            Check((File.Exists(cfgPath)?File.ReadAllText(cfgPath):null)==cfgBefore,"UI self-test never writes to the saved config");
            Check(typeof(SyncConfig).GetField("DailyBudgetUsd")==null,"daily budget feature removed from the config model");
            StackPanel cards=Field<StackPanel>(main,"_deviceCardsStack");Check(cards.Children.Count>1&&((Border)cards.Children[0]).Child!=null,"device cards render their content");
            StackPanel[] navGroups=Field<StackPanel[]>(main,"_navChildPanels");Border[] navChildren=Field<Border[]>(main,"_navChildBorders");
            Call(main,"SwitchActiveView",2);Check(navGroups[0].Children.Count==5&&(bool)navChildren[2].Tag,"model analysis is a highlighted usage sub-page in the sidebar");
            Check(Field<DockPanel>(main,"_pageToolbar").Visibility==Visibility.Visible&&Field<Border>(main,"_rangeContainer").Visibility==Visibility.Visible,"usage pages show the filter toolbar");
            Call(main,"SwitchActiveView",3);Check(Field<Border>(main,"_rangeContainer").Visibility==Visibility.Collapsed&&Field<TextBlock>(main,"_pageScopeText").Text.Contains("全部历史"),"project page states its own scope instead of a time filter it ignores");
            Call(main,"SwitchActiveView",9);Check(navGroups[1].Children.Count==3&&Field<DockPanel>(main,"_pageToolbar").Visibility==Visibility.Collapsed,"pricing rules are in the settings group and settings has no data filters");
            Call(main,"SwitchActiveView",8);Check(Field<TextBlock>(Field<object>(main,"_doctorView"),"_bannerStatusText").Text=="未检查","environment check never reports healthy before it has run");
            Call(main,"ApplyResponsiveSidebar",1020.0);bool narrowCollapsed=Field<bool>(main,"_sidebarCollapsed");
            Call(main,"ApplyResponsiveSidebar",1320.0);Check(narrowCollapsed&&!Field<bool>(main,"_sidebarCollapsed"),"sidebar collapses to icons on narrow windows and restores when widened");
            Call(main,"SwitchActiveView",7);UIElement[] pages=Field<UIElement[]>(main,"_viewPages");FrameworkElement heat=(FrameworkElement)Field<object>(main,"_heatmapView");
            Check(heat!=null&&heat.Parent==pages[7]&&pages[7].Visibility==Visibility.Visible,"daily summary hosts the 365-day usage calendar");
            Call(main,"SwitchActiveView",0);Capture((FrameworkElement)main.Content,Path.Combine(output,"overview-shell-dark.png"),1320,860);
            Call(main,"SwitchActiveView",4);Capture((FrameworkElement)main.Content,Path.Combine(output,"subscriptions-full-light.png"),1320,860);
            MiniHudWindow hud=new MiniHudWindow(false,null,null);
            long now=(long)(DateTime.UtcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
            HudTelemetry sample=new HudTelemetry { Tool="OpenAI Codex",Model="gpt-6.1-sol",Cost="$215.72",Tokens="469.18M",TimestampMs=now,Output=824,DurationMs=10000,TimingSource="request" };
            hud.UpdateTelemetry(sample);FrameworkElement surface=(FrameworkElement)hud.Content;
            Capture(surface,Path.Combine(output,"hud-light.png"),410,90);
            Check(Field<TextBlock>(hud,"_speed").Text=="82.4","measured TPS is visible independently of model text");
            double initialWidth=Field<Border>(hud,"_pill").ActualWidth;
            sample.Model=new string('x',250);hud.UpdateTelemetry(sample);Layout(surface,410,90);
            Check(Field<Border>(hud,"_pill").ActualWidth==initialWidth&&Field<TextBlock>(hud,"_speed").Text=="82.4","long model IDs cannot expand the capsule or erase TPS");
            TextBlock speed=Field<TextBlock>(hud,"_speed"),model=Field<TextBlock>(hud,"_model");Point left=speed.TranslatePoint(new Point(0,0),surface),edge=model.TranslatePoint(new Point(model.ActualWidth,0),surface);
            Check(left.X>=edge.X,"model text does not overlap the speed column");
            sample.Output=99999;sample.DurationMs=10000;hud.UpdateTelemetry(sample);Layout(surface,410,90);
            Point largeLeft=speed.TranslatePoint(new Point(0,0),surface);Check(largeLeft.X>=edge.X,"large measured speeds fit inside the fixed TPS column");
            Capture(surface,Path.Combine(output,"hud-large-tps.png"),410,90);sample.Output=824;
            sample.Model="gemini-flash-worker-agy";hud.UpdateTelemetry(sample);hud.SetTheme(true);Capture(surface,Path.Combine(output,"hud-dark.png"),410,90);
            sample.TimingSource="";hud.UpdateTelemetry(sample);Check(Field<TextBlock>(hud,"_speed").Text=="—"&&Field<TextBlock>(hud,"_speedLabel").Text.Contains("无计时"),"missing timing cannot masquerade as live speed");
            sample.TimestampMs=now-600000;hud.UpdateTelemetry(sample);Check(Field<TextBlock>(hud,"_speedLabel").Text.Contains("空闲"),"stale request becomes idle");
            Field<Button>(hud,"_face").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(Field<Popup>(hud,"_popup").IsOpen,"click opens timing detail");Field<Popup>(hud,"_popup").IsOpen=false;
            hud.Close();

            RequestInspectorPanel inspector=new RequestInspectorPanel(root,null,null);
            LiveRequestRowView timed=new LiveRequestRowView {Output=10,DurationMs=10,TimingSource="request",Tps=999};
            Call(inspector,"BindTelemetryMetrics",timed,10L);
            Check(Field<TextBlock>(inspector,"_durationMainText").Text=="10ms"&&Field<TextBlock>(inspector,"_tpsMainText").Text=="1000.0","request inspector accepts real short timings and recalculates TPS");
            timed.DurationMs=1200000;Call(inspector,"BindTelemetryMetrics",timed,600L);
            Check(Field<TextBlock>(inspector,"_durationMainText").Text!="—"&&Field<TextBlock>(inspector,"_tpsMainText").Text=="0.5","request inspector accepts real long timings");
            timed.TimingSource="";Call(inspector,"BindTelemetryMetrics",timed,600L);
            Check(Field<TextBlock>(inspector,"_tpsMainText").Text=="—","request inspector rejects unverified cached speed");
            string detail=(string)typeof(RequestInspectorPanel).GetMethod("BuildForensicsJson",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(inspector,new object[]{timed});
            var detailMap=(Dictionary<string,object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(detail);
            var telemetry=(Dictionary<string,object>)detailMap["telemetry"];
            Check(Convert.ToInt32(telemetry["duration_ms"])==0&&Convert.ToString(telemetry["tps_kind"])=="unavailable","request JSON carries the same timing provenance as the UI");

            SyncConfig config=new SyncConfig();SubscriptionCostsView view=new SubscriptionCostsView();Window owner=new Window { Content=view };DesignSystem.ApplyTheme(owner,false);
            view.Bind(config,delegate(SyncConfig c){SyncService.SaveConfig(c);});
            List<RecordBucket> usage=new List<RecordBucket>{new RecordBucket{Date=DateTime.Today.ToString("yyyy-MM-dd"),Tool="Claude Code",Model="glm-5.3",CostUsd=12,Output=500},new RecordBucket{Date=DateTime.Today.ToString("yyyy-MM-dd"),Tool="OpenCode",Model="kimi-k3",CostUsd=8,Output=200}};
            view.Render(usage,true,7,false,DateTime.Today);Check(config.Subscriptions.Count==0,"usage never auto-selects a purchased subscription");
            bool glm=false,kimi=false;foreach(TextBlock label in Descendants<TextBlock>(Field<StackPanel>(view,"_origins"))){glm|=label.Text=="智谱 GLM";kimi|=label.Text=="Kimi / Moonshot";}
            Check(glm&&kimi,"domestic origins stay separate even when both run through third-party tools");
            owner.ShowInTaskbar=false;owner.ShowActivated=false;owner.Left=-10000;owner.Top=-10000;owner.Show();
            Exception callbackFailure=null;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate {
                Window editor=null;foreach(Window w in app.Windows)if(w.Title=="登记订阅")editor=w;
                try {
                    Check(editor!=null,"subscription editor opens on the plan step");DependencyObject ui=editor.Content as DependencyObject;
                    Click(ui,"group:国产");Click(ui,"provider:glm");
                    Check(Tagged<StackPanel>(ui,"offers").Children.Count==10,"plan step lists the provider catalogue inside the editor");
                    Capture((FrameworkElement)editor.Content,Path.Combine(output,"subscription-editor-plan.png"),640,620);
                    Click(ui,"provider:kimi");Click(ui,"offer:kimi-plus");Click(ui,"next");
                    TextBox amount=Tagged<TextBox>(ui,"amount");
                    Check(amount.Text=="99"&&Convert.ToString(Tagged<ComboBox>(ui,"currency").SelectedItem)=="CNY"&&Tagged<TextBox>(ui,"months").Text=="1","choosing a plan prefills catalogue price, currency and cycle");
                    Tagged<TextBox>(ui,"account").Text="test account";amount.Text="NaN";Click(ui,"save");
                    Check(config.Subscriptions.Count==0&&editor.IsVisible&&Tagged<TextBlock>(ui,"error").Text.Length>0,"invalid amount leaves saved history untouched");
                    amount.Text="88";Capture((FrameworkElement)editor.Content,Path.Combine(output,"subscription-editor-pay.png"),640,620);Click(ui,"save");
                    SubscriptionPlanConfig saved=config.Subscriptions.Count==1?config.Subscriptions[0]:null;
                    Check(saved!=null&&saved.Amount==88&&saved.Currency=="CNY"&&saved.EcoId=="kimi"&&saved.FeeConfirmed&&saved.AccountLabel=="test account"&&saved.EndDate==DateTime.Today.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"),"editor persists actual fee in original currency with a computed end date");
                } catch(Exception e){callbackFailure=e;} finally{if(editor!=null&&editor.IsVisible)editor.Close();}
            }));
            Call(view,"EditSubscription",null,null);if(callbackFailure!=null)throw callbackFailure;
            owner.Content=null;view=new SubscriptionCostsView { Resources=owner.Resources };
            config.Subscriptions.Add(new SubscriptionPlanConfig { EcoId="claude",Enabled=true,PlanName="Max 5x",MonthlyUsd=100 });
            view.Bind(config,null);view.Render(usage,true,7,false,DateTime.Today);
            Capture(view,Path.Combine(output,"subscriptions-light.png"),1100,900);
            Button[] tabs=Field<Button[]>(view,"_tabBtns");
            Check(Convert.ToString(tabs[0].Content)=="当前 1"&&Convert.ToString(tabs[1].Content)=="待处理 1","verified and legacy records are listed separately");
            Check(Field<TextBlock>(view,"_spendValue").Text.Contains("88")&&Field<TextBlock>(view,"_spendSub").Text.Contains("1 项待处理"),"unverified legacy records stay out of the monthly total");
            DesignSystem.ApplyTheme(owner,true);view.Resources=owner.Resources;view.Render(usage,true,7,true,DateTime.Today);
            Capture(view,Path.Combine(output,"subscriptions-dark.png"),1100,900);
            owner.Close();main.Close();Console.WriteLine("PASS: "+checks+" UI assertions; screenshots: "+output);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{app.Shutdown();}
    }
}
