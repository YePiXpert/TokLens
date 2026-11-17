using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TokLens
{
    internal sealed class TierPricingView : Grid
    {
        internal sealed class ModelOption
        {
            public string Model { get; set; }
            public string Provider { get; set; }
            public string Display { get { return Provider + " · " + Model; } }
            public override string ToString() { return Display; }
        }
        internal sealed class PriceRow
        {
            public string Tier { get; set; }
            public string TierLabel { get; set; }
            public ServiceTierPrice Price { get; set; }
            public string InputText { get { return Price == null ? "—" : Money(Price.input_per_m); } }
            public string OutputText { get { return Price == null ? "—" : Money(Price.output_per_m); } }
            public string ReadText { get { return Price == null ? "—" : Money(Price.cache_read_per_m); } }
            public string WriteText { get { return Price == null ? "—" : Money(Price.cache_write_per_m); } }
            public string Status { get { return Price == null ? "该日期未收录" : "已收录参考价"; } }
        }

        private readonly string _customHome;
        private readonly ComboBox _modelBox;
        private readonly DatePicker _date;
        private readonly DataGrid _grid;
        private readonly TextBlock _detailsTitle;
        private readonly TextBlock _range;
        private readonly TextBlock _longRates;
        private readonly TextBlock _cacheDuration;
        private readonly TextBlock _effective;
        private readonly TextBlock _source;
        private readonly TextBlock _notice;
        private readonly Button _openSource;
        private readonly Button _viewCurrent;
        private string _sourceUrl;
        private bool _loading;

        private static string Money(double amount) { return "$" + amount.ToString("0.####", CultureInfo.InvariantCulture); }
        private static TextBlock Label(string text,string color,double size)
        {
            TextBlock value=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
            value.SetResourceReference(TextBlock.ForegroundProperty,color);return value;
        }
        public TierPricingView(string home,IEnumerable<string> activeModels)
        {
            string realHome=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _customHome=string.IsNullOrWhiteSpace(home)||string.Equals(home.TrimEnd('\\','/'),realHome.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)?null:home;
            _loading=true;
            RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            ScrollViewer scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            StackPanel content=new StackPanel {Margin=new Thickness(0,10,0,0)};scroll.Content=content;Children.Add(scroll);
            TextBlock heading=Label("按模型、档位与日期查看参考单价", "FgPrimary",17);heading.FontWeight=FontWeights.SemiBold;content.Children.Add(heading);
            TextBlock subtitle=Label("USD / 百万 token · API 等值估算，独立于订阅费用和额度消耗", "FgMuted",12);subtitle.Margin=new Thickness(0,4,0,12);content.Children.Add(subtitle);

            Grid controls=new Grid {Margin=new Thickness(0,0,0,12)};
            controls.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
            controls.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(16)});
            controls.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(160)});
            StackPanel modelFields=new StackPanel();modelFields.Children.Add(Label("模型（可键入名称快速定位）","FgSecondary",12));
            _modelBox=ModernControlsFactory.CreateComboBox();_modelBox.Tag="tier-model";_modelBox.DisplayMemberPath="Display";_modelBox.IsTextSearchEnabled=true;TextSearch.SetTextPath(_modelBox,"Model");_modelBox.Margin=new Thickness(0,5,0,0);modelFields.Children.Add(_modelBox);controls.Children.Add(modelFields);
            StackPanel dateFields=new StackPanel();dateFields.Children.Add(Label("价格适用日期","FgSecondary",12));
            _date=new DatePicker {SelectedDate=DateTime.Today,Height=32,Margin=new Thickness(0,5,0,0),Tag="tier-date"};
            _date.SetResourceReference(Control.ForegroundProperty,"FgPrimary");_date.SetResourceReference(Control.BackgroundProperty,"BgInput");_date.SetResourceReference(Control.BorderBrushProperty,"BorderStrong");
            Style dateText=new Style(typeof(System.Windows.Controls.Primitives.DatePickerTextBox));
            dateText.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("BgInput")));
            dateText.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("FgPrimary")));
            dateText.Setters.Add(new Setter(Control.BorderBrushProperty,new DynamicResourceExtension("BorderStrong")));
            _date.Resources[typeof(System.Windows.Controls.Primitives.DatePickerTextBox)]=dateText;
            dateFields.Children.Add(_date);Grid.SetColumn(dateFields,2);controls.Children.Add(dateFields);content.Children.Add(controls);

            Border table=ModernControlsFactory.CreateCard(new Thickness(0));table.ClipToBounds=true;
            _grid=ModernDataGridFactory.CreateModernDataGrid();_grid.Tag="tier-prices";_grid.IsReadOnly=true;_grid.Height=194;_grid.RowHeight=40;_grid.ColumnHeaderHeight=32;_grid.CanUserSortColumns=false;
            AddColumn("档位","TierLabel",105,false);AddColumn("输入","InputText",1,true);AddColumn("输出","OutputText",1,true);AddColumn("缓存读","ReadText",1,true);AddColumn("缓存写","WriteText",1,true);AddColumn("价格状态","Status",125,false);
            _grid.SizeChanged+=delegate(object sender,SizeChangedEventArgs e)
            {
                double numericWidth=Math.Max(78,(e.NewSize.Width-234)/4);
                for(int i=1;i<=4;i++)_grid.Columns[i].Width=new DataGridLength(numericWidth);
                ScrollViewer viewport=_grid.Template.FindName("DG_ScrollViewer",_grid) as ScrollViewer;
                if(viewport!=null){viewport.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;viewport.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;}
            };
            table.Child=_grid;content.Children.Add(table);

            Border detail=ModernControlsFactory.CreateCard(new Thickness(14,12,14,12));detail.Margin=new Thickness(0,12,0,0);
            StackPanel detailContent=new StackPanel();detail.Child=detailContent;
            _detailsTitle=Label("档位详情","FgPrimary",14);_detailsTitle.FontWeight=FontWeights.SemiBold;detailContent.Children.Add(_detailsTitle);
            _range=Label("","FgSecondary",12);_range.Margin=new Thickness(0,8,0,0);detailContent.Children.Add(_range);
            _longRates=Label("","FgPrimary",12);_longRates.Margin=new Thickness(0,6,0,0);detailContent.Children.Add(_longRates);
            _cacheDuration=Label("","FgSecondary",12);_cacheDuration.Margin=new Thickness(0,6,0,0);detailContent.Children.Add(_cacheDuration);
            _effective=Label("","FgMuted",12);_effective.Margin=new Thickness(0,10,0,0);detailContent.Children.Add(_effective);
            _source=Label("","FgMuted",12);_source.Margin=new Thickness(0,4,0,0);detailContent.Children.Add(_source);
            _openSource=ModernControlsFactory.CreateButton("打开价格来源","ghost");_openSource.HorizontalAlignment=HorizontalAlignment.Left;_openSource.Height=28;_openSource.Margin=new Thickness(-7,6,0,0);
            _openSource.Click+=delegate {Uri uri;if(Uri.TryCreate(_sourceUrl,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="http"))try{Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch{_notice.Text="无法打开来源链接，可复制上方地址到浏览器。";}};
            detailContent.Children.Add(_openSource);
            _viewCurrent=ModernControlsFactory.CreateButton("查看今天的参考价","secondary");_viewCurrent.Tag="tier-current-date";_viewCurrent.HorizontalAlignment=HorizontalAlignment.Left;_viewCurrent.Height=28;_viewCurrent.Margin=new Thickness(0,8,0,0);
            _viewCurrent.Click+=delegate {_date.SelectedDate=DateTime.Today;};detailContent.Children.Add(_viewCurrent);content.Children.Add(detail);
            _notice=Label("普通价编辑器仅覆盖普通档位；Fast、Ultrafast、Flex 使用各自档位规则。未收录的价格显示 —，不代表免费。","FgMuted",12);_notice.Margin=new Thickness(0,10,0,2);content.Children.Add(_notice);

            List<ModelOption> options=new List<ModelOption>();HashSet<string> keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ServiceTierPrice> catalog;
            try{catalog=ServiceTierPricing.Enumerate(_customHome);}catch(Exception error){catalog=new List<ServiceTierPrice>();_notice.Text="档位价格读取失败："+error.Message;}
            foreach(ServiceTierPrice p in catalog)
                if(p!=null&&!string.IsNullOrWhiteSpace(p.model)&&keys.Add(p.provider+"|"+p.model))options.Add(new ModelOption {Model=p.model,Provider=p.provider});
            string firstActive=null;
            if(activeModels!=null)foreach(string raw in activeModels)
            {
                string model=PricingEngine.NormalizeModelId(raw,"unknown");if(string.IsNullOrWhiteSpace(raw)||model=="unknown")continue;
                if(firstActive==null)firstActive=model;
                bool found=false;foreach(ModelOption option in options)if(option.Model==model){found=true;break;}
                if(!found)options.Add(new ModelOption {Model=model,Provider=UsageServiceTier.InferProvider(model)});
            }
            options.Sort(delegate(ModelOption a,ModelOption b){return StringComparer.OrdinalIgnoreCase.Compare(a.Display,b.Display);});
            _modelBox.ItemsSource=options;
            foreach(ModelOption option in options)if(option.Model==firstActive){_modelBox.SelectedItem=option;break;}
            if(_modelBox.SelectedIndex<0&&options.Count>0)_modelBox.SelectedIndex=0;
            _modelBox.SelectionChanged+=delegate {if(!_loading)RefreshRows();};_date.SelectedDateChanged+=delegate {if(!_loading)RefreshRows();};_grid.SelectionChanged+=delegate {UpdateDetails();};
            _loading=false;RefreshRows();
        }
        private void AddColumn(string header,string field,double width,bool star)
        {
            DataGridTextColumn column=ModernDataGridFactory.MakeTextOrMonoColumn(_grid,header,field,field,new DataGridLength(width,star?DataGridLengthUnitType.Star:DataGridLengthUnitType.Pixel),star,star,false);
            if(star)column.MinWidth=78;column.CanUserResize=false;_grid.Columns.Add(column);
        }
        private void RefreshRows()
        {
            ModelOption model=_modelBox.SelectedItem as ModelOption;List<PriceRow> rows=new List<PriceRow>();
            string day=(_date.SelectedDate??DateTime.Today).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            foreach(string tier in new[]{"standard","fast","ultrafast","flex"})
            {
                ServiceTierPrice price=null;
                try{if(model!=null)price=ServiceTierPricing.Find(model.Provider,model.Model,tier,day,_customHome);}catch(Exception error){_notice.Text="档位价格读取失败："+error.Message;}
                rows.Add(new PriceRow {Tier=tier,TierLabel=tier=="standard"?"Standard":tier=="fast"?"Fast":tier=="ultrafast"?"Ultrafast":"Flex",Price=price});
            }
            PriceRow selected=_grid.SelectedItem as PriceRow;string selectedTier=selected==null?"fast":selected.Tier;
            _grid.ItemsSource=rows;foreach(PriceRow row in rows)if(row.Tier==selectedTier){_grid.SelectedItem=row;break;}
            UpdateDetails();
        }
        private void UpdateDetails()
        {
            PriceRow row=_grid.SelectedItem as PriceRow;if(row==null)return;
            _detailsTitle.Text=row.TierLabel+" · 价格依据";ServiceTierPrice p=row.Price;
            if(p==null)
            {
                _range.Text="所选模型和日期没有此档位的已收录价格。";_longRates.Text="金额保持未计价；不会套用普通价或猜测倍率。";
                _cacheDuration.Text="";_effective.Text="可更换模型或查看今天的参考价；当前价格不代表历史报价。";_source.Text="";_sourceUrl=null;_openSource.Visibility=Visibility.Collapsed;
                _viewCurrent.Visibility=(_date.SelectedDate??DateTime.Today).Date==DateTime.Today?Visibility.Collapsed:Visibility.Visible;return;
            }
            _viewCurrent.Visibility=Visibility.Collapsed;
            _range.Text=p.max_prompt_tokens>0?"已核验支持范围：输入上下文 ≤ "+p.max_prompt_tokens.ToString("N0")+" token；超出范围未计价。":"表内未设额外输入上下文限制。";
            bool hasLong=p.long_context_threshold>0&&(p.max_prompt_tokens<=0||p.max_prompt_tokens>p.long_context_threshold);
            _longRates.Text=hasLong?"输入上下文 > "+p.long_context_threshold.ToString("N0")+" token：输入 "+Money(p.long_input_per_m)+" · 输出 "+Money(p.long_output_per_m)+" · 缓存读 "+Money(p.long_cache_read_per_m)+" · 缓存写 "+Money(p.long_cache_write_per_m)+" / 1M\n按单次请求的输入总量（含缓存）选择档位，全请求采用对应价格。":"没有适用于该支持范围的另一组长上下文单价。";
            _cacheDuration.Text=string.IsNullOrEmpty(p.cache_write_long_duration)?"未单列其他缓存写入时长。":"长期缓存写入（"+p.cache_write_long_duration+"）："+Money(p.cache_write_long_per_m)+" / 1M"+(hasLong?"；长上下文 "+Money(p.long_cache_write_long_per_m)+" / 1M":"")+(p.cache_write_long_duration=="12h"?"。12h 为官方参考，当前日志解析未单独识别该时长。":"");
            _effective.Text="适用日期："+p.effective_from+" 起"+(string.IsNullOrEmpty(p.effective_until)?"":"，至 "+p.effective_until)+"   ·   版本："+(p.version??"未标注");
            _source.Text="来源："+p.source;_sourceUrl=p.source;Uri uri;
            _openSource.Visibility=Uri.TryCreate(_sourceUrl,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="http")?Visibility.Visible:Visibility.Collapsed;
        }
        public void FocusModel() {_modelBox.Focus();_modelBox.IsDropDownOpen=true;}
    }
}
