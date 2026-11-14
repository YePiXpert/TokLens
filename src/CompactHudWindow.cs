using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TokLens
{
    // This surface always uses local requests and local costs, independent of dashboard filters.
    public sealed class MiniHudWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h,int n);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h,int n,int v);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int size);
        private readonly Action _restore,_refresh;
        private readonly Border _pill,_detailsBorder,_divider;
        private readonly TextBlock _tool,_model,_speedLabel,_speed,_unit,_cost,_details;
        private readonly List<TextBlock> _muted=new List<TextBlock>(),_primary=new List<TextBlock>();
        private readonly Popup _popup;
        private readonly Button _face;
        private readonly DispatcherTimer _timer;
        private HudTelemetry _data=new HudTelemetry();
        private bool _dark,_locked,_armed,_dragged;
        private Point _down;

        public MiniHudWindow(bool isDark,Action onRestoreMainWindow,Action onRefreshRequested)
        {
            _restore=onRestoreMainWindow;_refresh=onRefreshRequested;
            Title="TokLens · 轻量胶囊";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
            Topmost=true;ShowInTaskbar=false;ShowActivated=false;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.WidthAndHeight;
            UseLayoutRounding=true;SnapsToDevicePixels=true;FontFamily=DesignSystem.FontUi;
            Grid outer=new Grid { Margin=new Thickness(12) };Content=outer;
            _pill=new Border { Width=386,Height=66,CornerRadius=new CornerRadius(24),BorderThickness=new Thickness(1),
                Effect=new DropShadowEffect { Color=Colors.Black,BlurRadius=18,ShadowDepth=3,Opacity=.14 } };
            outer.Children.Add(_pill);
            _face=new Button { Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(16,9,16,9),HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center,Cursor=Cursors.Hand };
            ControlTemplate template=new ControlTemplate(typeof(Button)); FrameworkElementFactory presenter=new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.MarginProperty,new TemplateBindingExtension(Control.PaddingProperty));template.VisualTree=presenter;_face.Template=template;
            System.Windows.Automation.AutomationProperties.SetName(_face,"查看本机 TPS 和用量详情");_pill.Child=_face;
            Grid content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(112) });content.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(80) });_face.Content=content;
            Grid identity=new Grid { Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center };
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(25) });identity.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            System.Windows.Shapes.Path icon=new System.Windows.Shapes.Path { Data=Geometry.Parse(UiIcons.Terminal),Width=16,Height=16,Stretch=Stretch.Uniform,StrokeThickness=1.2,VerticalAlignment=VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"FgSecondary");identity.Children.Add(icon);
            StackPanel names=new StackPanel { VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(names,1);identity.Children.Add(names);
            _tool=Text("本机",DesignSystem.TextCaption,true);_tool.TextTrimming=TextTrimming.CharacterEllipsis;names.Children.Add(_tool);
            _model=Text("等待请求",DesignSystem.TextLabel,false);_model.Margin=new Thickness(0,3,0,0);_model.TextTrimming=TextTrimming.CharacterEllipsis;names.Children.Add(_model);content.Children.Add(identity);
            StackPanel metric=new StackPanel { HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,12,0) };Grid.SetColumn(metric,1);content.Children.Add(metric);
            _speedLabel=Text("TPS · 空闲",DesignSystem.TextCaption,true);_speedLabel.TextAlignment=TextAlignment.Right;metric.Children.Add(_speedLabel);
            StackPanel number=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
            Viewbox fittedNumber=new Viewbox { MaxWidth=100,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Right,Child=number };metric.Children.Add(fittedNumber);
            _speed=Text("—",DesignSystem.TextDisplay,false);_speed.FontWeight=FontWeights.Medium;ModernControlsFactory.EnableTabularNumbers(_speed,false);number.Children.Add(_speed);
            _unit=Text("tok/s",DesignSystem.TextCaption,true);_unit.VerticalAlignment=VerticalAlignment.Bottom;_unit.Margin=new Thickness(4,0,0,4);number.Children.Add(_unit);
            _divider=new Border { BorderThickness=new Thickness(1,0,0,0),Padding=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(_divider,2);content.Children.Add(_divider);
            StackPanel price=new StackPanel { HorizontalAlignment=HorizontalAlignment.Right };_divider.Child=price;
            price.Children.Add(Text("今日估值",DesignSystem.TextCaption,true));_cost=Text("—",DesignSystem.TextLabel,false);_cost.TextAlignment=TextAlignment.Right;_cost.Margin=new Thickness(0,5,0,0);_cost.TextTrimming=TextTrimming.CharacterEllipsis;ModernControlsFactory.EnableTabularNumbers(_cost,false);price.Children.Add(_cost);
            _detailsBorder=new Border { Width=386,Padding=new Thickness(16),CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1) };
            StackPanel detailContent=new StackPanel();_detailsBorder.Child=detailContent;detailContent.Children.Add(Text("本机 · 计速与用量",DesignSystem.TextTitle,false));
            _details=Text("",DesignSystem.TextBody,true);_details.TextWrapping=TextWrapping.Wrap;_details.LineHeight=22;_details.Margin=new Thickness(0,9,0,10);detailContent.Children.Add(_details);
            Button open=ModernControlsFactory.CreateButton("打开主窗口","secondary");open.Click+=delegate { _popup.IsOpen=false;if(_restore!=null)_restore(); };detailContent.Children.Add(open);
            _popup=new Popup { PlacementTarget=_pill,Placement=PlacementMode.Bottom,VerticalOffset=8,AllowsTransparency=true,StaysOpen=false,Child=_detailsBorder };
            _face.Click+=delegate { if(!_dragged)_popup.IsOpen=!_popup.IsOpen; };
            _face.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) {
                _dragged=false;_armed=true;_down=e.GetPosition(this);
                if(e.ClickCount==2){_armed=false;_dragged=true;_popup.IsOpen=false;if(_restore!=null)_restore();e.Handled=true;}
            };
            _face.PreviewMouseMove+=delegate(object sender,MouseEventArgs e) {
                if(!_armed||_locked||e.LeftButton!=MouseButtonState.Pressed)return;
                Point pos=e.GetPosition(this);if(Math.Abs(pos.X-_down.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(pos.Y-_down.Y)<SystemParameters.MinimumVerticalDragDistance)return;
                _armed=false;_dragged=true;_popup.IsOpen=false;try{DragMove();ClampPosition();}catch(InvalidOperationException){}
            };
            _face.PreviewMouseLeftButtonUp+=delegate { _armed=false; };
            _face.ToolTip="单击查看详情 · 拖动移动 · 双击打开主窗口 · 右键设置";
            BuildMenu();SetTheme(isDark);
            _timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };_timer.Tick+=delegate { Render(); };
            IsVisibleChanged+=delegate { if(IsVisible){Render();_timer.Start();}else{_timer.Stop();_popup.IsOpen=false;} };
            Closed+=delegate { _timer.Stop();_popup.IsOpen=false; };
            PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){_popup.IsOpen=false;e.Handled=true;}if(e.Key==Key.System&&e.SystemKey==Key.F4){Hide();e.Handled=true;}};
            SourceInitialized+=delegate {
                IntPtr h=new WindowInteropHelper(this).Handle;SetWindowLong(h,-20,GetWindowLong(h,-20)|0x80);
                try{int corner=1,none=unchecked((int)0xfffffffe);DwmSetWindowAttribute(h,33,ref corner,4);DwmSetWindowAttribute(h,34,ref none,4);}catch{}
            };
            Loaded+=delegate { Left=SystemParameters.WorkArea.Right-ActualWidth-20;Top=SystemParameters.WorkArea.Top+20;ClampPosition(); };
        }
        private TextBlock Text(string value,double size,bool muted)
        {
            TextBlock t=new TextBlock { Text=value,FontSize=size,FontFamily=DesignSystem.FontUi,VerticalAlignment=VerticalAlignment.Center };
            (muted?_muted:_primary).Add(t);return t;
        }
        public void SetTheme(bool dark)
        {
            if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(delegate{SetTheme(dark);}));return;}
            _dark=dark;DesignSystem.ApplyTheme(this,dark);
            // 悬浮胶囊与主窗口用同一套主题色
            _pill.SetResourceReference(Border.BackgroundProperty,"BgElevated");_detailsBorder.SetResourceReference(Border.BackgroundProperty,"BgElevated");
            _pill.SetResourceReference(Border.BorderBrushProperty,"BorderStrong");_detailsBorder.SetResourceReference(Border.BorderBrushProperty,"BorderStrong");_divider.SetResourceReference(Border.BorderBrushProperty,"BorderCard");
            foreach(TextBlock t in _muted)t.SetResourceReference(TextBlock.ForegroundProperty,"FgSecondary");
            foreach(TextBlock t in _primary)t.SetResourceReference(TextBlock.ForegroundProperty,"FgPrimary");Render();
        }
        public void UpdateTelemetry(HudTelemetry telemetry)
        {
            if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(delegate{UpdateTelemetry(telemetry);}));return;}
            _data=telemetry??new HudTelemetry();Render();
        }
        private void Render()
        {
            _tool.Text=string.IsNullOrEmpty(_data.Tool)||_data.Tool=="本机"?"本机":"本机 · "+HudTelemetry.ShortTool(_data.Tool);_tool.ToolTip=_data.Tool;
            _model.Text=HudTelemetry.ShortModel(_data.Model);_model.ToolTip=_data.Model;_cost.Text=_data.Cost+(_data.UnpricedRequests>0?"*":"");_cost.ToolTip="本机今日 API 参考估值："+_data.Cost+(_data.UnpricedRequests>0?"；另有 "+_data.UnpricedRequests+" 次未定价请求未计入":"");
            bool recent=_data.IsRecent(DateTime.UtcNow);double tps=_data.Tps;
            _speed.Text=recent&&tps>0?UsageTiming.FormatNumber(tps):"—";_unit.Visibility=recent&&tps>0?Visibility.Visible:Visibility.Collapsed;
            _speedLabel.Text=!recent?"TPS · 空闲":tps>0?"TPS · 请求均速":"TPS · 无计时";
            string at=_data.TimestampMs>0?new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(_data.TimestampMs).ToLocalTime().ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture):"暂无请求";
            _details.Text="工具  "+_data.Tool+"\n模型  "+_data.Model+"\n最近记录  "+at+"\n输出  "+_data.Output.ToString("N0",CultureInfo.InvariantCulture)+" tokens\n请求耗时  "+(tps>0?FormatterUtil.FormatDuration(_data.DurationMs):"未提供可靠计时")+"\n请求均速  "+FormatterUtil.FormatTps(tps)+"\n计时口径  "+(tps>0?"日志中的请求耗时，可能包含等待；非流式实时速度":"不使用相邻日志间隔推算 TPS")+"\n今日用量  "+_data.Tokens+" tokens\n今日 API 估值  "+_data.Cost+"（非实付）";
            if(_data.UnpricedRequests>0)_details.Text+="\n未定价  "+_data.UnpricedRequests+" 次请求未计入估值，不代表免费";
        }
        private void BuildMenu()
        {
            ContextMenu menu=new ContextMenu();MenuItem locked=new MenuItem { Header="锁定位置",IsCheckable=true };locked.Click+=delegate{_locked=locked.IsChecked;};menu.Items.Add(locked);
            MenuItem opacity=new MenuItem { Header="透明度" };foreach(double n in new double[]{1,.85,.65}){double value=n;MenuItem item=new MenuItem { Header=(n*100).ToString("0")+"%" };item.Click+=delegate { Opacity=value; };opacity.Items.Add(item);}menu.Items.Add(opacity);
            MenuItem refresh=new MenuItem { Header="重新扫描" };refresh.Click+=delegate { if(_refresh!=null)_refresh(); };menu.Items.Add(refresh);
            MenuItem show=new MenuItem { Header="打开主窗口" };show.Click+=delegate { if(_restore!=null)_restore(); };menu.Items.Add(show);
            MenuItem hide=new MenuItem { Header="隐藏悬浮条" };hide.Click+=delegate { Hide(); };menu.Items.Add(hide);_face.ContextMenu=menu;
        }
        private void ClampPosition()
        {
            Rect area=SystemParameters.WorkArea;PresentationSource source=PresentationSource.FromVisual(this);
            if(source!=null&&source.CompositionTarget!=null){System.Drawing.Rectangle pixels=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;Matrix m=source.CompositionTarget.TransformFromDevice;Point origin=m.Transform(new Point(pixels.Left,pixels.Top));Point edge=m.Transform(new Point(pixels.Right,pixels.Bottom));area=new Rect(origin,edge);}
            Left=Math.Max(area.Left,Math.Min(Left,area.Right-ActualWidth));Top=Math.Max(area.Top,Math.Min(Top,area.Bottom-ActualHeight));
        }
    }
}
