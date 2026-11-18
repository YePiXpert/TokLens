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
    private static void Set(object target,string name,object value){target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(target,value);}
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
            Border[] nav=Field<Border[]>(main,"_navRows");int visible=0;foreach(Border b in nav)if(b!=null&&b.Visibility==Visibility.Visible)visible++;
            Check(nav.Length==10&&visible==9,"every page has its own sidebar row; only the optional subscriptions page is hidden by default");Check(typeof(MainWindow).Assembly.GetType("TokLens.WhatIfSimulatorView")==null,"simulator code removed from build");
            Check(typeof(MainWindow).GetField("_navChildPanels",BindingFlags.NonPublic|BindingFlags.Instance)==null,"section captions are labels, not a second set of destinations");
            Check((File.Exists(cfgPath)?File.ReadAllText(cfgPath):null)==cfgBefore,"UI self-test never writes to the saved config");
            Check(typeof(SyncConfig).GetField("DailyBudgetUsd")==null,"daily budget feature removed from the config model");
            StackPanel cards=Field<StackPanel>(main,"_deviceCardsStack");Check(cards.Children.Count>1&&((Border)cards.Children[0]).Child!=null,"device cards render their content");
            Call(main,"SwitchActiveView",2);int highlighted=0;foreach(Border b in nav)if(b!=null&&(bool)b.Tag)highlighted++;
            Check((bool)nav[2].Tag&&highlighted==1&&Field<TextBlock>(main,"_viewGroupText").Text=="用量 /","exactly one sidebar row is highlighted and the page title names its section");
            Check(Field<DockPanel>(main,"_pageToolbar").Visibility==Visibility.Visible&&Field<Border>(main,"_rangeContainer").Visibility==Visibility.Visible,"usage pages show the filter toolbar");
            Call(main,"SwitchActiveView",3);Check(Field<Border>(main,"_rangeContainer").Visibility==Visibility.Collapsed&&Field<TextBlock>(main,"_pageScopeText").Text.Contains("全部历史"),"project page states its own scope instead of a time filter it ignores");
            Call(main,"SwitchActiveView",9);Border pricingRow=Field<Border>(main,"_navPricingRow");
            Check(pricingRow.Visibility==Visibility.Visible&&!(bool)pricingRow.Tag&&Field<DockPanel>(main,"_pageToolbar").Visibility==Visibility.Collapsed,"pricing rules sit in the settings section but never show as the current page, and settings has no data filters");
            Call(main,"SwitchActiveView",8);Check(Field<TextBlock>(Field<object>(main,"_doctorView"),"_bannerStatusText").Text=="未检查","environment check never reports healthy before it has run");
            Call(main,"ApplyResponsiveSidebar",1167.0);Check(!Field<bool>(main,"_sidebarCollapsed"),"the default window size keeps the labelled sidebar");
            Call(main,"ApplyResponsiveSidebar",940.0);bool narrowCollapsed=Field<bool>(main,"_sidebarCollapsed");int reachable=0;foreach(Border b in nav)if(b!=null&&b.Visibility==Visibility.Visible)reachable++;
            Check(narrowCollapsed&&reachable==9&&Field<TextBlock[]>(main,"_navRowLabels")[2].Visibility==Visibility.Collapsed,"collapsed to icons, every page is still one click away");
            Call(main,"ApplyResponsiveSidebar",1320.0);Check(!Field<bool>(main,"_sidebarCollapsed")&&Field<TextBlock[]>(main,"_navRowLabels")[2].Visibility==Visibility.Visible,"sidebar restores its labels when widened");
            Call(main,"SwitchActiveView",7);UIElement[] pages=Field<UIElement[]>(main,"_viewPages");FrameworkElement heat=(FrameworkElement)Field<object>(main,"_heatmapView");
            Check(heat!=null&&heat.Parent==pages[7]&&pages[7].Visibility==Visibility.Visible,"daily summary hosts the 365-day usage calendar");
            Call(main,"SwitchActiveView",0);Capture((FrameworkElement)main.Content,Path.Combine(output,"overview-shell-dark.png"),1320,860);
            Check(nav[4].Visibility==Visibility.Collapsed,"subscriptions page is hidden by default");
            Call(main,"SwitchActiveView",4);Check(Field<int>(main,"_activeView")==0,"the subscriptions page cannot be opened while the feature is off");
            Field<SyncConfig>(main,"_syncCfg").ShowSubscriptions=true;Call(main,"ApplySubscriptionFeature");Check(nav[4].Visibility==Visibility.Visible,"turning the setting on shows the subscriptions page");
            Call(main,"SwitchActiveView",4);Check(Field<int>(main,"_activeView")==4&&Field<Button>(main,"_deviceSelectorBtn").Visibility==Visibility.Collapsed&&Field<Border>(main,"_rangeContainer").Visibility==Visibility.Collapsed,"subscription page shows no device or time filter");Capture((FrameworkElement)main.Content,Path.Combine(output,"subscriptions-full-light.png"),1320,860);
            MiniHudWindow hud=new MiniHudWindow(false,null,null);
            long now=(long)(DateTime.UtcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
            HudTelemetry sample=new HudTelemetry { Tool="OpenAI Codex",Model="gpt-6.1-sol",TimestampMs=now,Output=824,DurationMs=10000,TimingSource="request",TodayUsd=215.72,LastHourUsd=12.5,Last15Usd=3.2,LastCostUsd=0.42,ContextTokens=234000,ContextLimit=1000000,TodayTokens=469180000,TodayRequests=2333,CacheHitRate=0.97 };
            sample.Workspace=@"C:\Projects\TokLens"; sample.SessionId="demo-session"; sample.WindowComplete=true;
            sample.Sessions.Add(new HudSessionChoice { Key="",Label="最新请求 · 本机全部工具" });
            sample.Sessions.Add(new HudSessionChoice { Key="demo",Label="Codex · TokLens" });
            for(int i=0;i<24;i++)sample.SparkTwoHours[i]=(i%5)*0.8+(i==23?1:0);
            sample.Latest.Add(new RecentRequestRecord{TimestampMs=now,Tool="OpenAI Codex",Model="gpt-6.1-sol",FreshInput=1000,CacheRead=233000,Output=824,CostUsd=0.42});
            hud.UpdateTelemetry(sample);FrameworkElement surface=(FrameworkElement)hud.Content;
            Func<string> contextText=delegate{return Field<TextBlock>(hud,"_context").Text;};
            Capture(surface,Path.Combine(output,"hud-capsule-light.png"),250,56);
            Check(contextText()=="23%"&&Field<TextBlock>(hud,"_scope").Text.Contains("标称"),"context explicitly identifies nominal model limit");
            Check(Field<TextBlock>(hud,"_status").Text.Contains("请求"),"recent activity has a text status");
            sample.ContextLimit=0;hud.UpdateTelemetry(sample);Check(contextText().Contains("234")&&!contextText().Contains("%"),"unknown limit shows absolute tokens");
            sample.ContextLimit=1000000;sample.RequestedServiceTier="fast";hud.UpdateTelemetry(sample);
            Call(hud,"OpenCard");Popup card=Field<Popup>(hud,"_cardPopup");Check(card.IsOpen&&!card.StaysOpen,"click-open panel supports outside dismissal");
            Call(hud,"RenderCard");Check(Field<TextBlock>(hud,"_cardTier").Text.Contains("请求")&&Field<TextBlock>(hud,"_cardTier").Text.Contains("未知"),"requested Fast is distinguished from actual execution");
            sample.Alert=true;sample.AlertText="近 15 分钟估值较活跃时段基线高";hud.UpdateTelemetry(sample);Call(hud,"RenderCard");
            Check(contextText()=="23%"&&Field<TextBlock>(hud,"_cardAlert").Visibility==Visibility.Visible,"alert explains the condition without hiding context");
            Check(Field<Canvas>(hud,"_chart").Children.Count==24&&Field<TextBlock>(hud,"_cardTiming").Text.Contains("82.4"),"card renders full trend and measured request speed");
            hud.SetAlert("",DateTime.MinValue);hud.SetTheme(true);Call(hud,"RenderCard");Capture((FrameworkElement)card.Child,Path.Combine(output,"hud-card-dark.png"),348,450);
            Capture(surface,Path.Combine(output,"hud-capsule-dark.png"),250,56);
            Call(hud,"CloseCard");Check(!card.IsOpen,"explicit dismissal closes the panel");
            sample.Alert=false;sample.TimingSource="";sample.TimestampMs=now-600000;hud.UpdateTelemetry(sample);Call(hud,"RenderCard");
            Check(Field<TextBlock>(hud,"_status").Text.Contains("空闲"),"old request is idle");
            Check(Field<TextBlock>(hud,"_cardTiming").Text.Contains("未提供可靠"),"missing timing is stated instead of inferred");
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
            Check(Field<TextBlock>(view,"_paybackValue").Text=="—","payback stays empty without a verified subscription");
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
            Check(Field<TextBlock>(view,"_paybackValue").Text=="64%","payback = matched Kimi usage at API prices / amount paid this cycle (8 / (88 / 7))");
            DesignSystem.ApplyTheme(owner,true);view.Resources=owner.Resources;view.Render(usage,true,7,true,DateTime.Today);
            Capture(view,Path.Combine(output,"subscriptions-dark.png"),1100,900);
            owner.Close();
            ScanResult missingPrice=new ScanResult { ReferenceDate=DateTime.Today.ToString("yyyy-MM-dd") };
            missingPrice.Records.Add(new RecordBucket { Date=missingPrice.ReferenceDate,Tool="OpenAI Codex",Model="fixture-fast-model",CostBasis="unpriced",ActualServiceTier="fast",Turns=2,FreshInput=100,RateLabel="未定价" });
            NodeSnapshot isolated=new NodeSnapshot { MachineId="ui-isolated",MachineName="UI fixture",IsLocal=true };isolated.Records=missingPrice.Records;
            Set(main,"_scanResult",missingPrice);Set(main,"_localNode",isolated);Set(main,"_remoteNodes",new List<NodeSnapshot>());
            Set(main,"_selectedMachineId",null);Set(main,"_selectedTool",null);Set(main,"_selectedDate",null);Set(main,"_rangeDays",0);
            Call(main,"RefreshDashboardView");
            Check(Field<TextBlock>(main,"_kpiCostVal").Text=="—"&&Field<TextBlock>(main,"_kpiCostSub").Text.Contains("未计价"),"all-unpriced overview is unknown, not free");
            missingPrice.Records.Add(new RecordBucket { Date=missingPrice.ReferenceDate,Tool="OpenAI Codex",Model="fixture-fast-model",CostBasis="reported",ActualServiceTier="standard",CostUsd=2,Turns=1,FreshInput=100 });Call(main,"RefreshDashboardView");
            Check(Field<TextBlock>(main,"_kpiCostSub").Text.Contains("已计价部分")&&Field<DataGrid>(main,"_modelGrid").Items.Count==2,"partial pricing is explicit and model analysis separates service tiers");
            Check(Field<TextBlock>(main,"_kpiSaveVal").Text=="—","reported or unknown charges cannot imply cache savings");
            main.Close();Console.WriteLine("PASS: "+checks+" UI assertions; screenshots: "+output);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{app.Shutdown();}
    }
}
