using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace TokLens
{
    internal static class DesignSystem
    {
        public static readonly FontFamily FontUi = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, sans-serif");
        public static readonly FontFamily FontDisplay = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI, sans-serif");
        public static readonly FontFamily FontMono = new FontFamily("Cascadia Mono, JetBrains Mono, Consolas, Microsoft YaHei UI, monospace");

        public static readonly string[] PaletteDark = new string[]
        {
            "#818CF8", "#38BDF8", "#34D399", "#A78BFA",
            "#F59E0B", "#FB7185", "#2DD4BF", "#FB923C"
        };

        public static readonly string[] PaletteLight = new string[]
        {
            "#4F46E5", "#0284C7", "#059669", "#7C3AED",
            "#D97706", "#E11D48", "#0D9488", "#EA580C"
        };

        private static readonly object BrushCacheLock = new object();
        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        private static ResourceDictionary _sharedControlTemplates;

        public static SolidColorBrush BrushHex(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return Brushes.Transparent;

            lock (BrushCacheLock)
            {
                SolidColorBrush cached;
                if (BrushCache.TryGetValue(hex, out cached))
                    return cached;

                Color c = (Color)ColorConverter.ConvertFromString(hex);
                SolidColorBrush b = new SolidColorBrush(c);
                b.Freeze();
                BrushCache[hex] = b;
                return b;
            }
        }

        public static string[] GetChartPalette(bool isDark)
        {
            return isDark ? PaletteDark : PaletteLight;
        }

        public static string GetProviderColorHex(string toolOrModel, bool isDark)
        {
            if (string.IsNullOrEmpty(toolOrModel))
                return isDark ? "#818CF8" : "#4F46E5";

            string s = toolOrModel.ToLowerInvariant();

            // Anthropic / Claude
            if (s.IndexOf("claude", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("anthropic", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#E5856B" : "#C25136";
            }

            // OpenAI / Codex / GPT / o1 / o3 / o4
            if (s.IndexOf("codex", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("openai", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("gpt-", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("chatgpt", StringComparison.Ordinal) >= 0 ||
                s.StartsWith("o1", StringComparison.Ordinal) ||
                s.StartsWith("o3", StringComparison.Ordinal) ||
                s.StartsWith("o4", StringComparison.Ordinal))
            {
                return isDark ? "#34D399" : "#059669";
            }

            // Cursor
            if (s.IndexOf("cursor", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#818CF8" : "#4F46E5";
            }

            // Google / Gemini / Antigravity
            if (s.IndexOf("gemini", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("antigravity", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("google", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#60A5FA" : "#2563EB";
            }

            // DeepSeek / OpenClaw / Cline / Roo
            if (s.IndexOf("deepseek", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("openclaw", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("cline", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("roo", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#38BDF8" : "#0284C7";
            }

            // Kimi / Moonshot
            if (s.IndexOf("kimi", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("moonshot", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#A78BFA" : "#7C3AED";
            }

            // Qwen / Alibaba
            if (s.IndexOf("qwen", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("tongyi", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#FB923C" : "#EA580C";
            }

            // GLM / Zhipu / ZCode / WorkBuddy
            if (s.IndexOf("glm", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("zhipu", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("zcode", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("workbuddy", StringComparison.Ordinal) >= 0)
            {
                return isDark ? "#2DD4BF" : "#0D9488";
            }

            // 确定性 FNV-1a 哈希回退至主题感知多色板
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= (uint)s[i];
                hash *= 16777619u;
            }
            string[] pal = GetChartPalette(isDark);
            return pal[(int)(hash % (uint)pal.Length)];
        }

        public static void ApplyTheme(Window targetWindow, bool isDark)
        {
            if (_sharedControlTemplates == null)
            {
                _sharedControlTemplates = (ResourceDictionary)XamlReader.Parse(GlobalControlsXaml);
            }

            if (Application.Current != null && Application.Current.Resources != null)
            {
                ApplyToDictionary(Application.Current.Resources, isDark);
            }

            if (targetWindow != null && targetWindow.Resources != null)
            {
                ApplyToDictionary(targetWindow.Resources, isDark);
            }

            if (targetWindow != null)
            {
                ModernWindowChrome.SyncDwmTheme(targetWindow, isDark);
            }
        }

        private static void ApplyToDictionary(ResourceDictionary res, bool isDark)
        {
            if (!res.MergedDictionaries.Contains(_sharedControlTemplates))
            {
                res.MergedDictionaries.Add(_sharedControlTemplates);
            }

            res["FontUi"] = FontUi;
            res["FontDisplay"] = FontDisplay;
            res["FontMono"] = FontMono;

            if (isDark)
            {
                res["BgWindow"]              = BrushHex("#09090B");
                res["BgTitleBar"]            = BrushHex("#0C0C0E");
                res["BgSidebar"]             = BrushHex("#121215");
                res["BgCard"]                = BrushHex("#16161A");
                res["BgSurface"]             = BrushHex("#111114");
                res["BgInput"]               = BrushHex("#111114");
                res["BgElevated"]            = BrushHex("#1F1F25");
                res["BgSegActive"]           = BrushHex("#26262E");
                res["BgHover"]               = BrushHex("#1C1C22");
                res["BgHoverStrong"]         = BrushHex("#27272F");
                res["BgSelected"]            = BrushHex("#1D1E30");
                res["ToggleOffBg"]           = BrushHex("#27272F");

                res["BorderSubtle"]          = BrushHex("#1E1E24");
                res["BorderCard"]            = BrushHex("#232329");
                res["BorderStrong"]          = BrushHex("#32323B");
                res["BorderFocus"]           = BrushHex("#6366F1");

                res["FgPrimary"]             = BrushHex("#F4F4F6");
                res["FgSecondary"]           = BrushHex("#A1A1AA");
                res["FgMuted"]               = BrushHex("#71717A");

                res["AccentPrimary"]         = BrushHex("#6366F1");
                res["AccentText"]            = BrushHex("#818CF8");
                res["AccentSubtleBg"]        = BrushHex("#191B2E");
                res["AccentSubtleBorder"]    = BrushHex("#2E3259");

                res["BtnPrimaryBg"]          = BrushHex("#5E6AD2");
                res["BtnPrimaryHover"]       = BrushHex("#6E7AE2");
                res["BtnPrimaryPressed"]     = BrushHex("#4E5AC0");
                res["BtnPrimaryBorder"]      = BrushHex("#7884E6");

                res["BtnDangerHoverBg"]      = BrushHex("#3A1A1E");
                res["BtnDangerPressedBg"]    = BrushHex("#481F24");

                res["SemanticSuccess"]       = BrushHex("#34D399");
                res["SemanticSuccessBg"]     = BrushHex("#10261E");
                res["SemanticSuccessBorder"] = BrushHex("#1B4334");

                res["SemanticWarning"]       = BrushHex("#FBBF24");
                res["SemanticWarningBg"]     = BrushHex("#281E0D");
                res["SemanticWarningBorder"] = BrushHex("#4E3915");

                res["SemanticDanger"]        = BrushHex("#F87171");
                res["SemanticDangerBg"]      = BrushHex("#2A1416");
                res["SemanticDangerBorder"]  = BrushHex("#542125");

                res["ChartBarFill"]          = BrushHex("#5E6AD2");
                res["ChartBarHover"]         = BrushHex("#818CF8");

                res["ScrollThumb"]           = BrushHex("#3F3F46");
                res["ScrollThumbHover"]      = BrushHex("#52525B");
                res["ScrollThumbActive"]     = BrushHex("#71717A");
            }
            else
            {
                res["BgWindow"]              = BrushHex("#F8F9FA");
                res["BgTitleBar"]            = BrushHex("#F8F9FA");
                res["BgSidebar"]             = BrushHex("#F3F4F6");
                res["BgCard"]                = BrushHex("#FFFFFF");
                res["BgSurface"]             = BrushHex("#F1F2F4");
                res["BgInput"]               = BrushHex("#FFFFFF");
                res["BgElevated"]            = BrushHex("#FFFFFF");
                res["BgSegActive"]           = BrushHex("#FFFFFF");
                res["BgHover"]               = BrushHex("#F4F4F5");
                res["BgHoverStrong"]         = BrushHex("#EAEAEE");
                res["BgSelected"]            = BrushHex("#EEF2FF");
                res["ToggleOffBg"]           = BrushHex("#D4D4D8");

                res["BorderSubtle"]          = BrushHex("#F1F1F4");
                res["BorderCard"]            = BrushHex("#E4E4E7");
                res["BorderStrong"]          = BrushHex("#D4D4D8");
                res["BorderFocus"]           = BrushHex("#4F46E5");

                res["FgPrimary"]             = BrushHex("#09090B");
                res["FgSecondary"]           = BrushHex("#52525B");
                res["FgMuted"]               = BrushHex("#71717A");

                res["AccentPrimary"]         = BrushHex("#4F46E5");
                res["AccentText"]            = BrushHex("#4F46E5");
                res["AccentSubtleBg"]        = BrushHex("#EEF2FF");
                res["AccentSubtleBorder"]    = BrushHex("#C7D2FE");

                res["BtnPrimaryBg"]          = BrushHex("#4F46E5");
                res["BtnPrimaryHover"]       = BrushHex("#4338CA");
                res["BtnPrimaryPressed"]     = BrushHex("#3730A3");
                res["BtnPrimaryBorder"]      = BrushHex("#4338CA");

                res["BtnDangerHoverBg"]      = BrushHex("#FEE2E2");
                res["BtnDangerPressedBg"]    = BrushHex("#FECACA");

                res["SemanticSuccess"]       = BrushHex("#047857");
                res["SemanticSuccessBg"]     = BrushHex("#ECFDF5");
                res["SemanticSuccessBorder"] = BrushHex("#A7F3D0");

                res["SemanticWarning"]       = BrushHex("#B45309");
                res["SemanticWarningBg"]     = BrushHex("#FFFBEB");
                res["SemanticWarningBorder"] = BrushHex("#FDE68A");

                res["SemanticDanger"]        = BrushHex("#DC2626");
                res["SemanticDangerBg"]      = BrushHex("#FEF2F2");
                res["SemanticDangerBorder"]  = BrushHex("#FECACA");

                res["ChartBarFill"]          = BrushHex("#4F46E5");
                res["ChartBarHover"]         = BrushHex("#6366F1");

                res["ScrollThumb"]           = BrushHex("#D4D4D8");
                res["ScrollThumbHover"]      = BrushHex("#A1A1AA");
                res["ScrollThumbActive"]     = BrushHex("#71717A");
            }

            // 旧资源键平滑兼容别名
            res["AccentBlue"]    = res["AccentText"];
            res["AccentIndigo"]  = res["AccentPrimary"];
            res["AccentEmerald"] = res["SemanticSuccess"];
            res["AccentAmber"]   = res["SemanticWarning"];
            res["WarnBg"]        = res["SemanticWarningBg"];
            res["WarnBorder"]    = res["SemanticWarningBorder"];
            res["WarnFg"]        = res["SemanticWarning"];
            res["BgRowAlt"]      = res["BgCard"];
        }

        private const string GlobalControlsXaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

  <!-- 1. 现代极细胶囊滚动条（无上下箭头、透明轨道、常态 4px 悬停平滑加宽至 6px） -->
  <ControlTemplate x:Key=""VertThumbTpl"" TargetType=""{x:Type Thumb}"">
    <Border x:Name=""ThumbBd"" Width=""4"" CornerRadius=""2"" Margin=""2,2,2,2""
            Background=""{DynamicResource ScrollThumb}"" SnapsToDevicePixels=""True""/>
    <ControlTemplate.Triggers>
      <Trigger Property=""IsMouseOver"" Value=""True"">
        <Setter TargetName=""ThumbBd"" Property=""Width"" Value=""6""/>
        <Setter TargetName=""ThumbBd"" Property=""CornerRadius"" Value=""3""/>
        <Setter TargetName=""ThumbBd"" Property=""Margin"" Value=""1,2,1,2""/>
        <Setter TargetName=""ThumbBd"" Property=""Background"" Value=""{DynamicResource ScrollThumbHover}""/>
      </Trigger>
      <Trigger Property=""IsDragging"" Value=""True"">
        <Setter TargetName=""ThumbBd"" Property=""Width"" Value=""6""/>
        <Setter TargetName=""ThumbBd"" Property=""CornerRadius"" Value=""3""/>
        <Setter TargetName=""ThumbBd"" Property=""Margin"" Value=""1,2,1,2""/>
        <Setter TargetName=""ThumbBd"" Property=""Background"" Value=""{DynamicResource ScrollThumbActive}""/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <ControlTemplate x:Key=""HorzThumbTpl"" TargetType=""{x:Type Thumb}"">
    <Border x:Name=""ThumbBd"" Height=""4"" CornerRadius=""2"" Margin=""2,2,2,2""
            Background=""{DynamicResource ScrollThumb}"" SnapsToDevicePixels=""True""/>
    <ControlTemplate.Triggers>
      <Trigger Property=""IsMouseOver"" Value=""True"">
        <Setter TargetName=""ThumbBd"" Property=""Height"" Value=""6""/>
        <Setter TargetName=""ThumbBd"" Property=""CornerRadius"" Value=""3""/>
        <Setter TargetName=""ThumbBd"" Property=""Margin"" Value=""2,1,2,1""/>
        <Setter TargetName=""ThumbBd"" Property=""Background"" Value=""{DynamicResource ScrollThumbHover}""/>
      </Trigger>
      <Trigger Property=""IsDragging"" Value=""True"">
        <Setter TargetName=""ThumbBd"" Property=""Height"" Value=""6""/>
        <Setter TargetName=""ThumbBd"" Property=""CornerRadius"" Value=""3""/>
        <Setter TargetName=""ThumbBd"" Property=""Margin"" Value=""2,1,2,1""/>
        <Setter TargetName=""ThumbBd"" Property=""Background"" Value=""{DynamicResource ScrollThumbActive}""/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <Style TargetType=""{x:Type ScrollBar}"">
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Style.Triggers>
      <Trigger Property=""Orientation"" Value=""Vertical"">
        <Setter Property=""Width"" Value=""8""/>
        <Setter Property=""Height"" Value=""Auto""/>
        <Setter Property=""Template"">
          <Setter.Value>
            <ControlTemplate TargetType=""{x:Type ScrollBar}"">
              <Grid Background=""Transparent"">
                <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Template=""{StaticResource VertThumbTpl}""/>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
                  </Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
      <Trigger Property=""Orientation"" Value=""Horizontal"">
        <Setter Property=""Height"" Value=""8""/>
        <Setter Property=""Width"" Value=""Auto""/>
        <Setter Property=""Template"">
          <Setter.Value>
            <ControlTemplate TargetType=""{x:Type ScrollBar}"">
              <Grid Background=""Transparent"">
                <Track x:Name=""PART_Track"" IsDirectionReversed=""False"">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageLeftCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Template=""{StaticResource HorzThumbTpl}""/>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageRightCommand"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
                  </Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- 2. 现代 Popover 右键/更多菜单（双层分离阴影，杜绝高分屏文字位图化发虚） -->
  <Style TargetType=""{x:Type ContextMenu}"">
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""UseLayoutRounding"" Value=""True""/>
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""HasDropShadow"" Value=""True""/>
    <Setter Property=""FontFamily"" Value=""{DynamicResource FontUi}""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type ContextMenu}"">
          <Grid Margin=""10"">
            <Border CornerRadius=""8"" Background=""{DynamicResource BgElevated}"" IsHitTestVisible=""False"">
              <Border.Effect>
                <DropShadowEffect BlurRadius=""16"" ShadowDepth=""4"" Direction=""270"" Color=""#000000"" Opacity=""0.26""/>
              </Border.Effect>
            </Border>
            <Border Padding=""4"" CornerRadius=""8""
                    Background=""{DynamicResource BgElevated}""
                    BorderBrush=""{DynamicResource BorderCard}""
                    BorderThickness=""1"">
              <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Cycle""/>
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType=""{x:Type MenuItem}"">
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""Foreground"" Value=""{DynamicResource FgPrimary}""/>
    <Setter Property=""FontSize"" Value=""12""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type MenuItem}"">
          <Border x:Name=""ItemBd"" Height=""30"" Padding=""10,0,12,0"" CornerRadius=""5"" Background=""Transparent"">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""Auto"" />
                <ColumnDefinition Width=""*"" />
                <ColumnDefinition Width=""Auto"" />
              </Grid.ColumnDefinitions>
              <ContentPresenter x:Name=""IconHost"" Grid.Column=""0"" ContentSource=""Icon"" VerticalAlignment=""Center"" Margin=""0,0,8,0""/>
              <ContentPresenter Grid.Column=""1"" ContentSource=""Header"" VerticalAlignment=""Center"" RecognizesAccessKey=""True""/>
              <TextBlock Grid.Column=""2"" Text=""{TemplateBinding InputGestureText}"" Margin=""16,0,0,0""
                         FontSize=""11"" Foreground=""{DynamicResource FgMuted}"" VerticalAlignment=""Center""/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""Icon"" Value=""{x:Null}"">
              <Setter TargetName=""IconHost"" Property=""Visibility"" Value=""Collapsed""/>
            </Trigger>
            <Trigger Property=""IsHighlighted"" Value=""True"">
              <Setter TargetName=""ItemBd"" Property=""Background"" Value=""{DynamicResource BgHoverStrong}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.45""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key=""{x:Static MenuItem.SeparatorStyleKey}"" TargetType=""{x:Type Separator}"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type Separator}"">
          <Border Height=""1"" Margin=""6,4,6,4"" Background=""{DynamicResource BorderCard}""/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 3. 现代暗黑/浅白悬浮提示框 ToolTip（双层分离阴影保障高分屏锐度） -->
  <Style TargetType=""{x:Type ToolTip}"">
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""UseLayoutRounding"" Value=""True""/>
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""HasDropShadow"" Value=""True""/>
    <Setter Property=""FontFamily"" Value=""{DynamicResource FontUi}""/>
    <Setter Property=""FontSize"" Value=""12""/>
    <Setter Property=""Foreground"" Value=""{DynamicResource FgPrimary}""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type ToolTip}"">
          <Grid Margin=""8"" MaxWidth=""420"">
            <Border CornerRadius=""6"" Background=""{DynamicResource BgElevated}"" IsHitTestVisible=""False"">
              <Border.Effect>
                <DropShadowEffect BlurRadius=""12"" ShadowDepth=""3"" Direction=""270"" Color=""#000000"" Opacity=""0.22""/>
              </Border.Effect>
            </Border>
            <Border Padding=""10,6"" CornerRadius=""6""
                    Background=""{DynamicResource BgElevated}""
                    BorderBrush=""{DynamicResource BorderStrong}""
                    BorderThickness=""1"">
              <ContentPresenter TextBlock.LineHeight=""18""/>
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 4. 现代圆角复选框 CheckBox -->
  <Style TargetType=""{x:Type CheckBox}"">
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""Foreground"" Value=""{DynamicResource FgSecondary}""/>
    <Setter Property=""FontSize"" Value=""12""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type CheckBox}"">
          <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"" Background=""Transparent"">
            <Border x:Name=""ChkBox"" Width=""16"" Height=""16"" CornerRadius=""4"" Margin=""0,0,8,0""
                    Background=""{DynamicResource BgInput}""
                    BorderBrush=""{DynamicResource BorderStrong}"" BorderThickness=""1""
                    VerticalAlignment=""Center"">
              <Path x:Name=""ChkMark"" Data=""M 2.5,7.0 L 5.5,10.0 L 11.5,3.5""
                    Stroke=""#FFFFFF"" StrokeThickness=""1.6""
                    StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round""
                    Visibility=""Collapsed""/>
            </Border>
            <ContentPresenter VerticalAlignment=""Center"" RecognizesAccessKey=""True""/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""ChkBox"" Property=""BorderBrush"" Value=""{DynamicResource BorderFocus}""/>
              <Setter Property=""Foreground"" Value=""{DynamicResource FgPrimary}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""ChkBox"" Property=""Background"" Value=""{DynamicResource BtnPrimaryBg}""/>
              <Setter TargetName=""ChkBox"" Property=""BorderBrush"" Value=""{DynamicResource BtnPrimaryBg}""/>
              <Setter TargetName=""ChkMark"" Property=""Visibility"" Value=""Visible""/>
              <Setter Property=""Foreground"" Value=""{DynamicResource FgPrimary}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }

    internal static class ModernWindowChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        private const int SM_CXSIZEFRAME = 32;
        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const int WM_DPICHANGED = 0x02E0;
        private const int HTMAXBUTTON = 9;

        private static int SafeGetMetricsForDpi(int nIndex, uint dpi)
        {
            if (dpi > 0)
            {
                try { return GetSystemMetricsForDpi(nIndex, dpi); } catch { }
            }
            return GetSystemMetrics(nIndex);
        }

        public static void Attach(
            Window win,
            Border rootShellBorder,
            Button maxBtn,
            Path maxIconPath,
            double captionHeight,
            Func<bool> isDarkGetter)
        {
            if (win == null) return;

            win.UseLayoutRounding = true;
            win.SnapsToDevicePixels = true;
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.ClearType);

            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = captionHeight;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0, 0, 0, 1);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(win, chrome);

            win.StateChanged += delegate(object s, EventArgs e)
            {
                UpdateMaximizedPadding(win, chrome, captionHeight, rootShellBorder, maxIconPath);
            };

            win.LocationChanged += delegate(object s, EventArgs e)
            {
                if (win.WindowState == WindowState.Maximized)
                {
                    UpdateMaximizedPadding(win, chrome, captionHeight, rootShellBorder, maxIconPath);
                }
            };

            win.SourceInitialized += delegate(object s, EventArgs e)
            {
                SyncDwmTheme(win, isDarkGetter != null ? isDarkGetter() : true);
                UpdateMaximizedPadding(win, chrome, captionHeight, rootShellBorder, maxIconPath);

                HwndSource src = PresentationSource.FromVisual(win) as HwndSource;
                if (src != null)
                {
                    bool ncHover = false;
                    src.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                    {
                        if (msg == WM_DPICHANGED)
                        {
                            win.Dispatcher.BeginInvoke(new Action(delegate()
                            {
                                UpdateMaximizedPadding(win, chrome, captionHeight, rootShellBorder, maxIconPath);
                            }), DispatcherPriority.Loaded);
                        }
                        else if (maxBtn != null)
                        {
                            if (msg == WM_NCHITTEST)
                            {
                                if (IsScreenPointInElement(maxBtn, lParam))
                                {
                                    if (!ncHover)
                                    {
                                        ncHover = true;
                                        maxBtn.SetResourceReference(Control.BackgroundProperty, "BgHoverStrong");
                                        if (maxIconPath != null)
                                            maxIconPath.SetResourceReference(Shape.StrokeProperty, "FgPrimary");
                                    }
                                    handled = true;
                                    return new IntPtr(HTMAXBUTTON);
                                }
                                else if (ncHover)
                                {
                                    ncHover = false;
                                    maxBtn.Background = Brushes.Transparent;
                                    if (maxIconPath != null)
                                        maxIconPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
                                }
                            }
                            else if (msg == WM_NCLBUTTONDOWN && wParam.ToInt64() == HTMAXBUTTON)
                            {
                                maxBtn.SetResourceReference(Control.BackgroundProperty, "BgElevated");
                                handled = true;
                            }
                            else if (msg == WM_NCLBUTTONUP && wParam.ToInt64() == HTMAXBUTTON)
                            {
                                ncHover = false;
                                maxBtn.Background = Brushes.Transparent;
                                if (maxIconPath != null)
                                    maxIconPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
                                if (IsScreenPointInElement(maxBtn, lParam))
                                {
                                    win.WindowState = (win.WindowState == WindowState.Maximized)
                                        ? WindowState.Normal
                                        : WindowState.Maximized;
                                }
                                handled = true;
                            }
                            else if (msg == WM_NCMOUSELEAVE && ncHover)
                            {
                                ncHover = false;
                                maxBtn.Background = Brushes.Transparent;
                                if (maxIconPath != null)
                                    maxIconPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
                            }
                        }
                        return IntPtr.Zero;
                    });
                }
            };
        }

        public static void SyncDwmTheme(Window win, bool isDark)
        {
            if (win == null) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkVal = isDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref darkVal, sizeof(int));
                }

                int roundPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPref, sizeof(int));

                // Win11 硬件级 1px 边框色 COLORREF (0x00BBGGRR): Dark #232329 -> 0x00292323, Light #D4D4D8 -> 0x00D8D4D4
                int borderColorRef = isDark ? 0x00292323 : 0x00D8D4D4;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorRef, sizeof(int));
            }
            catch { }
        }

        private static void UpdateMaximizedPadding(Window win, WindowChrome chrome, double captionHeight, Border rootShellBorder, Path maxIconPath)
        {
            bool isMax = (win.WindowState == WindowState.Maximized);
            double padTopDip = 0.0;
            if (isMax)
            {
                double scaleX = 1.0;
                double scaleY = 1.0;
                PresentationSource ps = PresentationSource.FromVisual(win);
                if (ps != null && ps.CompositionTarget != null)
                {
                    scaleX = ps.CompositionTarget.TransformFromDevice.M11;
                    scaleY = ps.CompositionTarget.TransformFromDevice.M22;
                }
                uint dpiX = scaleX > 0.01 ? (uint)Math.Round(96.0 / scaleX) : 96u;
                uint dpiY = scaleY > 0.01 ? (uint)Math.Round(96.0 / scaleY) : 96u;
                int padPxX = SafeGetMetricsForDpi(SM_CXSIZEFRAME, dpiX) + SafeGetMetricsForDpi(SM_CXPADDEDBORDER, dpiX);
                int padPxY = SafeGetMetricsForDpi(SM_CYSIZEFRAME, dpiY) + SafeGetMetricsForDpi(SM_CXPADDEDBORDER, dpiY);
                double padHorzDip = padPxX * scaleX;
                padTopDip = padPxY * scaleY;
                if (rootShellBorder != null)
                {
                    rootShellBorder.Padding = new Thickness(padHorzDip, padTopDip, padHorzDip, padTopDip);
                    rootShellBorder.BorderThickness = new Thickness(0);
                }
            }
            else
            {
                if (rootShellBorder != null)
                {
                    rootShellBorder.Padding = new Thickness(0);
                    rootShellBorder.BorderThickness = new Thickness(1);
                }
            }

            if (chrome != null)
            {
                chrome.CaptionHeight = isMax ? (captionHeight + padTopDip) : captionHeight;
                chrome.ResizeBorderThickness = isMax ? new Thickness(0) : new Thickness(6);
            }

            if (maxIconPath != null)
            {
                maxIconPath.Data = Geometry.Parse(isMax
                    ? "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z"
                    : "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
            }
        }

        private static bool IsScreenPointInElement(FrameworkElement el, IntPtr lParam)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                return false;
            try
            {
                long lp = lParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point ptInEl = el.PointFromScreen(new Point(sx, sy));
                return ptInEl.X >= 0 && ptInEl.X <= el.ActualWidth &&
                       ptInEl.Y >= 0 && ptInEl.Y <= el.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        public static StackPanel CreateCaptionButtons(Window win, bool showMinMax, out Button maxBtnOut, out Path maxPathOut)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            WindowChrome.SetIsHitTestVisibleInChrome(sp, true);

            maxBtnOut = null;
            maxPathOut = null;

            if (showMinMax)
            {
                Path minPath;
                Button minBtn = MakeChromeBtn("M 0.5,5.5 L 9.5,5.5", false, out minPath);
                minBtn.ToolTip = "最小化";
                minBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (win != null) win.WindowState = WindowState.Minimized;
                };
                sp.Children.Add(minBtn);

                Button maxBtn = MakeChromeBtn("M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z", false, out maxPathOut);
                maxBtn.ToolTip = "最大化 / 悬停使用分屏布局";
                maxBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    if (win != null)
                        win.WindowState = (win.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                };
                maxBtnOut = maxBtn;
                sp.Children.Add(maxBtn);
            }

            Path closePath;
            Button closeBtn = MakeChromeBtn("M 0.5,0.5 L 9.5,9.5 M 9.5,0.5 L 0.5,9.5", true, out closePath);
            closeBtn.ToolTip = "关闭";
            closeBtn.Click += delegate(object s, RoutedEventArgs e)
            {
                if (win != null) win.Close();
            };
            sp.Children.Add(closeBtn);

            return sp;
        }

        private static Button MakeChromeBtn(string pathData, bool isCloseDanger, out Path iconPath)
        {
            Button btn = new Button();
            btn.Width = 38;
            btn.Height = 28;
            btn.Background = Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);
            btn.FocusVisualStyle = null;
            btn.Cursor = Cursors.Arrow;
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            iconPath = new Path
            {
                Data = Geometry.Parse(pathData),
                Width = 10,
                Height = 10,
                Stretch = Stretch.None,
                StrokeThickness = 1.0,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            iconPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary");
            btn.Content = iconPath;

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "CbBd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.MarginProperty, new Thickness(1, 0, 1, 0));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hv = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (isCloseDanger)
            {
                hv.Setters.Add(new Setter(Border.BackgroundProperty, DesignSystem.BrushHex("#E81123"), "CbBd"));
            }
            else
            {
                hv.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverStrong"), "CbBd"));
            }
            tpl.Triggers.Add(hv);
            btn.Template = tpl;

            Path capturedPath = iconPath;
            if (isCloseDanger)
            {
                btn.MouseEnter += delegate(object s, MouseEventArgs e) { capturedPath.Stroke = Brushes.White; };
                btn.MouseLeave += delegate(object s, MouseEventArgs e) { capturedPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary"); };
            }
            else
            {
                btn.MouseEnter += delegate(object s, MouseEventArgs e) { capturedPath.SetResourceReference(Shape.StrokeProperty, "FgPrimary"); };
                btn.MouseLeave += delegate(object s, MouseEventArgs e) { capturedPath.SetResourceReference(Shape.StrokeProperty, "FgSecondary"); };
            }

            return btn;
        }
    }

    internal static class UiIcons
    {
        public const string Overview      = "M 2,2 L 7,2 L 7,9 L 2,9 Z M 9,2 L 14,2 L 14,6 L 9,6 Z M 9,8 L 14,8 L 14,14 L 9,14 Z M 2,11 L 7,11 L 7,14 L 2,14 Z";
        public const string Activity      = "M 1.5,8 L 4.5,8 L 6.5,2.5 L 9.5,13.5 L 11.5,8 L 14.5,8";
        public const string Models        = "M 8,1.5 L 14,5 L 14,11 L 8,14.5 L 2,11 L 2,5 Z M 2,5 L 8,8.5 L 14,5 M 8,8.5 L 8,14.5";
        public const string Devices       = "M 1.5,3 L 11.5,3 L 11.5,10 L 1.5,10 Z M 4.5,13 L 8.5,13 M 6.5,10 L 6.5,13 M 10.5,6.5 L 14.5,6.5 L 14.5,13.5 L 10.5,13.5 Z";
        public const string Calendar      = "M 2,3.5 L 14,3.5 L 14,14 L 2,14 Z M 2,7 L 14,7 M 5,1.5 L 5,4.5 M 11,1.5 L 11,4.5";
        public const string Settings      = "M 2,4 L 6,4 M 9,4 L 14,4 M 7.5,2.5 A 1.5,1.5 0 1 1 7.5,5.5 A 1.5,1.5 0 1 1 7.5,2.5 M 2,8 L 9,8 M 12,8 L 14,8 M 10.5,6.5 A 1.5,1.5 0 1 1 10.5,9.5 A 1.5,1.5 0 1 1 10.5,6.5 M 2,12 L 4,12 M 7,12 L 14,12 M 5.5,10.5 A 1.5,1.5 0 1 1 5.5,13.5 A 1.5,1.5 0 1 1 5.5,10.5";
        public const string Refresh       = "M 13.5,8 A 5.5,5.5 0 1 1 12,4.2 M 13.8,2 L 13.8,5.2 L 10.6,5.2";
        public const string Download      = "M 8,2 L 8,10.5 M 4.5,7.5 L 8,11 L 11.5,7.5 M 2.5,13.5 L 13.5,13.5";
        public const string Sun           = "M 8,5 A 3,3 0 1 1 7.99,5 M 8,1.5 L 8,3 M 8,13 L 8,14.5 M 1.5,8 L 3,8 M 13,8 L 14.5,8 M 3.4,3.4 L 4.5,4.5 M 11.5,11.5 L 12.6,12.6 M 12.6,3.4 L 11.5,4.5 M 4.5,11.5 L 3.4,12.6";
        public const string Moon          = "M 13.5,9.8 A 5.8,5.8 0 1 1 6.2,2.5 A 4.5,4.5 0 0 0 13.5,9.8 Z";
        public const string Plus          = "M 8,2.5 L 8,13.5 M 2.5,8 L 13.5,8";
        public const string Search        = "M 7,2.5 A 4.5,4.5 0 1 1 6.99,2.5 M 10.3,10.3 L 13.8,13.8";
        public const string Check         = "M 2.5,8.5 L 6.2,12.2 L 13.5,4";
        public const string Copy          = "M 5.5,5.5 L 13.5,5.5 L 13.5,13.5 L 5.5,13.5 Z M 10.5,5.5 L 10.5,2.5 L 2.5,2.5 L 2.5,10.5 L 5.5,10.5";
        public const string Shield        = "M 8,1.8 L 13.5,4 L 13.5,8.2 C 13.5,11.5 11,13.6 8,14.5 C 5,13.6 2.5,11.5 2.5,8.2 L 2.5,4 Z M 5.8,8.2 L 7.4,9.8 L 10.5,6.5";
        public const string Terminal      = "M 2,3 L 14,3 L 14,13 L 2,13 Z M 4.5,6 L 7,8 L 4.5,10 M 8,10 L 11,10";
        public const string Alert         = "M 8,2 A 6,6 0 1 1 7.99,2 M 8,5.2 L 8,8.8 M 8,11.2 L 8,11.4";
        public const string ChevronDown   = "M 4,6 L 8,10 L 12,6";
        public const string ExternalLink  = "M 9.5,2.5 L 13.5,2.5 L 13.5,6.5 M 13.5,2.5 L 7.5,8.5 M 11.5,9 L 11.5,13.5 L 2.5,13.5 L 2.5,4.5 L 7,4.5";
        public const string Folder        = "M 2,4 L 6.2,4 L 7.8,5.8 L 14,5.8 L 14,13 L 2,13 Z";
        public const string SidebarToggle = "M 2,2.5 L 14,2.5 L 14,13.5 L 2,13.5 Z M 6,2.5 L 6,13.5";
        public const string Trash         = "M 2.5,4.5 L 13.5,4.5 M 6,4.5 L 6,2.5 L 10,2.5 L 10,4.5 M 4,4.5 L 4.6,13.5 L 11.4,13.5 L 12,4.5 M 6.8,7.2 L 6.8,11 M 9.2,7.2 L 9.2,11";
        public const string Pause         = "M 5.5,3 L 5.5,13 M 10.5,3 L 10.5,13";
        public const string Play          = "M 4.5,2.8 L 13,8 L 4.5,13.2 Z";
        public const string Key           = "M 6,6.5 A 3.5,3.5 0 1 1 5.99,6.5 M 8.5,9 L 13.8,14.2 M 11.5,12 L 13.2,10.5";
        public const string Cloud         = "M 4.8,12.5 L 12,12.5 A 2.8,2.8 0 0 0 12.2,7 A 4.2,4.2 0 0 0 4.2,6.2 A 3.2,3.2 0 0 0 4.8,12.5 Z";
        public const string Sparkles      = "M 8,1.5 L 9.4,6.6 L 14.5,8 L 9.4,9.4 L 8,14.5 L 6.6,9.4 L 1.5,8 L 6.6,6.6 Z";
        public const string Swap          = "M 2.5,5 L 13.5,5 M 10.5,2.5 L 13.5,5 L 10.5,7.5 M 13.5,11 L 2.5,11 M 5.5,8.5 L 2.5,11 L 5.5,13.5";
        public const string Layers        = "M 8,2 L 14,5.2 L 8,8.4 L 2,5.2 Z M 2,8.4 L 8,11.6 L 14,8.4 M 2,11.4 L 8,14.5 L 14,11.4";
        public const string Doctor        = "M 8,2 A 6,6 0 1 1 7.99,2 M 8,5 L 8,11 M 5,8 L 11,8";
        public const string Help          = "M 8,2 A 6,6 0 1 1 7.99,2 M 6.3,6.3 A 1.7,1.7 0 1 1 8.8,7.9 C 8.2,8.3 8,8.7 8,9.4 M 8,11.8 L 8,12.0";
        public const string Capsule       = "M 4.5,4.5 L 11.5,4.5 A 3.5,3.5 0 0 1 11.5,11.5 L 4.5,11.5 A 3.5,3.5 0 0 1 4.5,4.5 Z M 5.5,8 L 7.5,8";
        public const string History       = "M 3.2,5.5 A 5.5,5.5 0 1 1 2.8,9.5 M 2.5,2.8 L 2.8,6 L 6,5.6 M 8,5.2 L 8,8.3 L 10.4,9.8";

        public static Path CreateIcon(string pathData, double size, string strokeResourceKey)
        {
            Path p = new Path();
            if (!string.IsNullOrEmpty(pathData))
            {
                p.Data = Geometry.Parse(pathData);
            }
            p.Width = size;
            p.Height = size;
            p.Stretch = Stretch.Uniform;
            p.StrokeThickness = 1.5;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
            p.UseLayoutRounding = true;
            p.SnapsToDevicePixels = true;

            if (!string.IsNullOrEmpty(strokeResourceKey))
            {
                p.SetResourceReference(Shape.StrokeProperty, strokeResourceKey);
            }
            else
            {
                Binding fgBinding = new Binding();
                fgBinding.Path = new PropertyPath(TextElement.ForegroundProperty);
                fgBinding.RelativeSource = RelativeSource.Self;
                p.SetBinding(Shape.StrokeProperty, fgBinding);
            }

            return p;
        }
    }

    internal static class ModernControlsFactory
    {
        public static Button CreateButton(string text, string variant)
        {
            bool isPrimary = string.Equals(variant, "primary", StringComparison.OrdinalIgnoreCase);
            bool isGhost   = string.Equals(variant, "ghost", StringComparison.OrdinalIgnoreCase);
            bool isDanger  = string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase);

            Button btn = new Button();
            btn.Content = text;
            btn.FontFamily = DesignSystem.FontUi;
            btn.FontSize = 12;
            btn.FontWeight = FontWeights.Medium;
            btn.Height = 28;
            btn.Padding = new Thickness(12, 0, 12, 0);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            if (isPrimary)
                btn.Foreground = Brushes.White;
            else if (isDanger)
                btn.SetResourceReference(Control.ForegroundProperty, "SemanticDanger");
            else if (isGhost)
                btn.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            else
                btn.SetResourceReference(Control.ForegroundProperty, "FgPrimary");

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "BtnBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

            if (isPrimary)
            {
                bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BtnPrimaryBg"));
                bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BtnPrimaryBorder"));
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }
            else if (isDanger)
            {
                bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("SemanticDangerBg"));
                bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticDangerBorder"));
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }
            else if (isGhost)
            {
                bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                bd.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }
            else
            {
                bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCard"));
                bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (isPrimary)
            {
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BtnPrimaryHover"), "BtnBd"));
            }
            else if (isDanger)
            {
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BtnDangerHoverBg"), "BtnBd"));
                hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SemanticDanger"), "BtnBd"));
            }
            else if (isGhost)
            {
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverStrong"), "BtnBd"));
                hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            }
            else
            {
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverStrong"), "BtnBd"));
            }
            tpl.Triggers.Add(hover);

            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            if (isPrimary)
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BtnPrimaryPressed"), "BtnBd"));
            else if (isDanger)
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BtnDangerPressedBg"), "BtnBd"));
            else
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgElevated"), "BtnBd"));
            tpl.Triggers.Add(pressed);

            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "BtnBd"));
            tpl.Triggers.Add(disabled);

            btn.Template = tpl;
            return btn;
        }

        public static Button CreateIconTextButton(string iconPathData, string text, string variant)
        {
            Button btn = CreateButton("", variant);
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(iconPathData))
            {
                Path icon = UiIcons.CreateIcon(iconPathData, 13, null);
                if (!string.IsNullOrEmpty(text))
                    icon.Margin = new Thickness(0, 0, 6, 0);
                sp.Children.Add(icon);
            }

            if (!string.IsNullOrEmpty(text))
            {
                TextBlock tb = new TextBlock
                {
                    Text = text,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(tb);
            }

            btn.Content = sp;
            return btn;
        }

        public static Button CreateIconGhostButton(string pathData, string tooltip)
        {
            Button btn = CreateButton("", "ghost");
            btn.Width = 28;
            btn.Height = 28;
            btn.Padding = new Thickness(0);
            btn.ToolTip = tooltip;
            btn.Content = UiIcons.CreateIcon(pathData, 14, null);
            return btn;
        }

        public static Border CreateSegmentedContainer()
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(8);
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(2);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);

            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.Child = sp;
            return b;
        }

        public static Button CreateSegmentedItem(string text)
        {
            Button btn = new Button();
            btn.Content = text;
            btn.Height = 22;
            btn.FontFamily = DesignSystem.FontUi;
            btn.FontSize = 12;
            btn.FontWeight = FontWeights.Medium;
            btn.Padding = new Thickness(10, 0, 10, 0);
            btn.Cursor = Cursors.Hand;
            btn.FocusVisualStyle = null;
            btn.Background = Brushes.Transparent;
            btn.BorderBrush = Brushes.Transparent;
            btn.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "SegBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            btn.Template = tpl;
            return btn;
        }

        public static void UpdateSegmentedGroup(Button[] group, int activeIndex)
        {
            if (group == null) return;
            for (int i = 0; i < group.Length; i++)
            {
                Button b = group[i];
                if (b == null) continue;
                b.FontWeight = FontWeights.Medium;
                if (i == activeIndex)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "BgSegActive");
                    b.SetResourceReference(Control.BorderBrushProperty, "BorderCard");
                    b.SetResourceReference(Control.ForegroundProperty, "FgPrimary");
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.BorderBrush = Brushes.Transparent;
                    b.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
                }
            }
        }

        public static TextBox CreateTextInput(string initialText, double height)
        {
            TextBox tb = new TextBox();
            tb.Text = initialText ?? "";
            tb.Height = height > 0 ? height : 30;
            tb.FontFamily = DesignSystem.FontUi;
            tb.FontSize = 12;
            tb.Padding = new Thickness(8, 0, 8, 0);
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
            tb.SetResourceReference(TextBox.CaretBrushProperty, "AccentPrimary");
            WindowChrome.SetIsHitTestVisibleInChrome(tb, true);

            ControlTemplate tpl = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "TbBd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgInput"));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            sv.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(sv);
            tpl.VisualTree = bd;

            Trigger hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("FgMuted"), "TbBd"));
            tpl.Triggers.Add(hoverTrig);

            Trigger focusTrig = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focusTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("BorderFocus"), "TbBd"));
            tpl.Triggers.Add(focusTrig);

            tb.Template = tpl;
            return tb;
        }

        public static CheckBox CreateToggleSwitch(string labelText)
        {
            CheckBox chk = new CheckBox();
            chk.Content = labelText ?? "";
            chk.FontFamily = DesignSystem.FontUi;
            chk.FontSize = 12;
            chk.Cursor = Cursors.Hand;
            chk.FocusVisualStyle = null;
            chk.SetResourceReference(Control.ForegroundProperty, "FgSecondary");
            WindowChrome.SetIsHitTestVisibleInChrome(chk, true);

            ControlTemplate tpl = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border), "SwitchTrack");
            track.SetValue(FrameworkElement.WidthProperty, 34.0);
            track.SetValue(FrameworkElement.HeightProperty, 18.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            track.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("ToggleOffBg"));
            track.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderStrong"));
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory knob = new FrameworkElementFactory(typeof(Border), "SwitchKnob");
            knob.SetValue(FrameworkElement.WidthProperty, 14.0);
            knob.SetValue(FrameworkElement.HeightProperty, 14.0);
            knob.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            knob.SetValue(Border.BackgroundProperty, Brushes.White);
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 0, 0, 0));
            track.AppendChild(knob);
            root.AppendChild(track);

            if (!string.IsNullOrEmpty(labelText))
            {
                FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
                root.AppendChild(cp);
            }

            tpl.VisualTree = root;

            Trigger hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            tpl.Triggers.Add(hoverTrig);

            Trigger checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrig.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BtnPrimaryBg"), "SwitchTrack"));
            checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("BtnPrimaryBg"), "SwitchTrack"));
            checkedTrig.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(17, 0, 0, 0), "SwitchKnob"));
            checkedTrig.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            tpl.Triggers.Add(checkedTrig);

            Trigger disTrig = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disTrig.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            tpl.Triggers.Add(disTrig);

            chk.Template = tpl;
            return chk;
        }

        public static Border CreateCard(Thickness padding)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(8);
            b.BorderThickness = new Thickness(1);
            b.Padding = padding;
            b.SnapsToDevicePixels = true;
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            return b;
        }

        public static Border CreateStatusBadge(string initialText, string variant, out TextBlock labelOut, out Ellipse dotOut)
        {
            string bgKey = "BgSurface";
            string bdKey = "BorderCard";
            string fgKey = "FgSecondary";

            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticSuccessBg";
                bdKey = "SemanticSuccessBorder";
                fgKey = "SemanticSuccess";
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticWarningBg";
                bdKey = "SemanticWarningBorder";
                fgKey = "SemanticWarning";
            }
            else if (string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(variant, "error", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "SemanticDangerBg";
                bdKey = "SemanticDangerBorder";
                fgKey = "SemanticDanger";
            }
            else if (string.Equals(variant, "accent", StringComparison.OrdinalIgnoreCase))
            {
                bgKey = "AccentSubtleBg";
                bdKey = "AccentSubtleBorder";
                fgKey = "AccentText";
            }

            Border pill = new Border();
            pill.Height = 20;
            pill.CornerRadius = new CornerRadius(4);
            pill.Padding = new Thickness(6, 0, 6, 0);
            pill.BorderThickness = new Thickness(1);
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.SetResourceReference(Border.BackgroundProperty, bgKey);
            pill.SetResourceReference(Border.BorderBrushProperty, bdKey);

            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            dotOut = new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dotOut.SetResourceReference(Shape.FillProperty, fgKey);
            sp.Children.Add(dotOut);

            labelOut = new TextBlock
            {
                Text = initialText ?? "",
                FontFamily = DesignSystem.FontUi,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            labelOut.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            sp.Children.Add(labelOut);

            pill.Child = sp;
            return pill;
        }

        public static void EnableTabularNumbers(TextBlock tb, bool useMonoStack)
        {
            if (tb == null) return;
            tb.FontFamily = useMonoStack ? DesignSystem.FontMono : DesignSystem.FontDisplay;
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            Typography.SetNumeralStyle(tb, FontNumeralStyle.Lining);
        }

        public static Popup CreateModernPopover(UIElement placementTarget, UIElement content)
        {
            Popup pop = new Popup();
            pop.PlacementTarget = placementTarget;
            pop.Placement = PlacementMode.Bottom;
            pop.VerticalOffset = 4;
            pop.AllowsTransparency = true;
            pop.StaysOpen = false;
            pop.PopupAnimation = PopupAnimation.Fade;

            Grid wrap = new Grid();
            wrap.Margin = new Thickness(12);
            wrap.UseLayoutRounding = true;
            wrap.SnapsToDevicePixels = true;

            Border shadowLayer = new Border();
            shadowLayer.CornerRadius = new CornerRadius(8);
            shadowLayer.IsHitTestVisible = false;
            shadowLayer.SetResourceReference(Border.BackgroundProperty, "BgElevated");

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 18;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Color = Colors.Black;
            shadow.Opacity = 0.28;
            shadowLayer.Effect = shadow;
            wrap.Children.Add(shadowLayer);

            Border shell = new Border();
            shell.Padding = new Thickness(8);
            shell.CornerRadius = new CornerRadius(8);
            shell.BorderThickness = new Thickness(1);
            shell.SnapsToDevicePixels = true;
            shell.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            shell.SetResourceReference(Border.BorderBrushProperty, "BorderCard");
            shell.Child = content;
            wrap.Children.Add(shell);

            pop.Child = wrap;
            return pop;
        }
    }

    internal static class ModernDataGridFactory
    {
        private sealed class RatioToWidthConverter : IValueConverter
        {
            private readonly double _maxWidth;

            public RatioToWidthConverter(double maxWidth)
            {
                _maxWidth = maxWidth;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value == null) return 0.0;
                double r = 0.0;
                try
                {
                    r = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return 0.0;
                }

                if (double.IsNaN(r) || double.IsInfinity(r) || r <= 0.0)
                    return 0.0;

                // 兼容 0..1 比例或 0..100 百分比数值
                if (r > 1.0001 && r <= 100.0)
                    r = r / 100.0;

                if (r > 1.0) r = 1.0;
                double px = r * _maxWidth;
                return px < 2.0 ? 2.0 : px;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private static ControlTemplate _cachedDataGridTemplate;

        private const string DataGridCleanTemplateXaml = @"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type DataGrid}"">
  <Border Background=""{TemplateBinding Background}""
          BorderBrush=""{TemplateBinding BorderBrush}""
          BorderThickness=""{TemplateBinding BorderThickness}""
          Padding=""{TemplateBinding Padding}""
          SnapsToDevicePixels=""True"">
    <ScrollViewer x:Name=""DG_ScrollViewer"" Focusable=""False"" Background=""Transparent"">
      <ScrollViewer.Template>
        <ControlTemplate TargetType=""{x:Type ScrollViewer}"">
          <Grid Background=""Transparent"">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width=""*"" />
              <ColumnDefinition Width=""Auto"" />
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
              <RowDefinition Height=""Auto"" />
              <RowDefinition Height=""*"" />
              <RowDefinition Height=""Auto"" />
            </Grid.RowDefinitions>

            <DataGridColumnHeadersPresenter x:Name=""PART_ColumnHeadersPresenter""
                                            Grid.Row=""0"" Grid.Column=""0"" Grid.ColumnSpan=""2""
                                            Visibility=""{Binding HeadersVisibility, ConverterParameter={x:Static DataGridHeadersVisibility.Column}, Converter={x:Static DataGrid.HeadersVisibilityConverter}, RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}}"" />

            <ScrollContentPresenter x:Name=""PART_ScrollContentPresenter""
                                    Grid.Row=""1"" Grid.Column=""0""
                                    CanContentScroll=""{TemplateBinding CanContentScroll}"" />

            <ScrollBar x:Name=""PART_VerticalScrollBar""
                       Grid.Row=""1"" Grid.Column=""1""
                       Orientation=""Vertical""
                       Maximum=""{TemplateBinding ScrollableHeight}""
                       ViewportSize=""{TemplateBinding ViewportHeight}""
                       Value=""{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}""
                       Visibility=""{TemplateBinding ComputedVerticalScrollBarVisibility}"" />

            <ScrollBar x:Name=""PART_HorizontalScrollBar""
                       Grid.Row=""2"" Grid.Column=""0""
                       Orientation=""Horizontal""
                       Maximum=""{TemplateBinding ScrollableWidth}""
                       ViewportSize=""{TemplateBinding ViewportWidth}""
                       Value=""{Binding HorizontalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}""
                       Visibility=""{TemplateBinding ComputedHorizontalScrollBarVisibility}"" />

            <Border Grid.Row=""2"" Grid.Column=""1"" Background=""Transparent"" />
          </Grid>
        </ControlTemplate>
      </ScrollViewer.Template>
      <ItemsPresenter SnapsToDevicePixels=""{TemplateBinding SnapsToDevicePixels}"" />
    </ScrollViewer>
  </Border>
</ControlTemplate>";

        public static DataGrid CreateModernDataGrid()
        {
            if (_cachedDataGridTemplate == null)
            {
                _cachedDataGridTemplate = (ControlTemplate)XamlReader.Parse(DataGridCleanTemplateXaml);
            }

            DataGrid dg = new DataGrid();
            dg.Template = _cachedDataGridTemplate;
            dg.AutoGenerateColumns = false;
            dg.IsReadOnly = true;
            dg.CanUserAddRows = false;
            dg.CanUserDeleteRows = false;
            dg.CanUserReorderColumns = false;
            dg.CanUserResizeRows = false;
            dg.HeadersVisibility = DataGridHeadersVisibility.Column;
            dg.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            dg.SelectionMode = DataGridSelectionMode.Single;
            dg.SelectionUnit = DataGridSelectionUnit.FullRow;
            dg.BorderThickness = new Thickness(0);
            dg.RowHeaderWidth = 0;
            dg.ColumnHeaderHeight = 32;
            dg.RowHeight = 34;
            dg.FontFamily = DesignSystem.FontUi;
            dg.FontSize = 12;
            dg.EnableRowVirtualization = true;
            ScrollViewer.SetCanContentScroll(dg, true);

            dg.SetResourceReference(DataGrid.BackgroundProperty, "BgCard");
            dg.SetResourceReference(DataGrid.RowBackgroundProperty, "BgCard");
            dg.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderSubtle");
            dg.SetResourceReference(DataGrid.ForegroundProperty, "FgPrimary");

            // 1. 表头样式（纯净底色 + 底边 1px BorderCard + ↑/↓ 排序指示）
            Style hdrStyle = new Style(typeof(DataGridColumnHeader));
            hdrStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgMuted")));
            hdrStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
            hdrStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            hdrStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

            ControlTemplate hdrTpl = new ControlTemplate(typeof(DataGridColumnHeader));
            FrameworkElementFactory hbd = new FrameworkElementFactory(typeof(Border), "HdrBd");
            hbd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCard"));
            hbd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderCard"));
            hbd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            hbd.SetValue(Border.PaddingProperty, new Thickness(12, 0, 12, 0));

            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            FrameworkElementFactory sortTxt = new FrameworkElementFactory(typeof(TextBlock), "SortArrow");
            sortTxt.SetValue(DockPanel.DockProperty, Dock.Right);
            sortTxt.SetValue(TextBlock.MarginProperty, new Thickness(4, 0, 0, 0));
            sortTxt.SetValue(TextBlock.FontSizeProperty, 11.0);
            sortTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            sortTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentText"));
            dock.AppendChild(sortTxt);

            FrameworkElementFactory hcp = new FrameworkElementFactory(typeof(ContentPresenter));
            hcp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            hcp.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            dock.AppendChild(hcp);
            hbd.AppendChild(dock);
            hdrTpl.VisualTree = hbd;

            Trigger hHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hHover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            hdrTpl.Triggers.Add(hHover);

            Trigger ascTrig = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = ListSortDirection.Ascending };
            ascTrig.Setters.Add(new Setter(TextBlock.TextProperty, "↑", "SortArrow"));
            ascTrig.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            hdrTpl.Triggers.Add(ascTrig);

            Trigger descTrig = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = ListSortDirection.Descending };
            descTrig.Setters.Add(new Setter(TextBlock.TextProperty, "↓", "SortArrow"));
            descTrig.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            hdrTpl.Triggers.Add(descTrig);

            hdrStyle.Setters.Add(new Setter(Control.TemplateProperty, hdrTpl));
            dg.ColumnHeaderStyle = hdrStyle;

            // 2. 整行柔和 Hover 与 Selected
            Style rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("BgCard")));
            rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            Trigger rowHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            rowHover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("BgHover")));
            rowStyle.Triggers.Add(rowHover);

            Trigger rowSel = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            rowSel.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("BgSelected")));
            rowStyle.Triggers.Add(rowSel);
            dg.RowStyle = rowStyle;

            // 3. 单元格纯净无焦点框样式
            Style cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            ControlTemplate cellTpl = new ControlTemplate(typeof(DataGridCell));
            FrameworkElementFactory cbd = new FrameworkElementFactory(typeof(Border));
            cbd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            cbd.SetValue(Border.PaddingProperty, new Thickness(12, 0, 12, 0));
            FrameworkElementFactory ccp = new FrameworkElementFactory(typeof(ContentPresenter));
            ccp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cbd.AppendChild(ccp);
            cellTpl.VisualTree = cbd;

            Trigger cellSel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            cellSel.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgPrimary")));
            cellTpl.Triggers.Add(cellSel);

            cellStyle.Setters.Add(new Setter(Control.TemplateProperty, cellTpl));
            dg.CellStyle = cellStyle;

            return dg;
        }

        public static DataGridTextColumn MakeTextOrMonoColumn(
            DataGrid dg,
            string header,
            string bindingPath,
            string sortPath,
            DataGridLength width,
            bool rightAlign,
            bool isMono,
            bool isPrimaryEmphasis)
        {
            DataGridTextColumn col = new DataGridTextColumn();
            col.Header = header;
            col.Binding = new Binding(bindingPath);
            col.SortMemberPath = sortPath;
            col.Width = width;
            if (width.IsStar)
            {
                col.MinWidth = 118;
            }

            if (rightAlign && dg != null && dg.ColumnHeaderStyle != null)
            {
                Style hdrRight = new Style(typeof(DataGridColumnHeader), dg.ColumnHeaderStyle);
                hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                col.HeaderStyle = hdrRight;
            }

            Style elemStyle = new Style(typeof(TextBlock));
            elemStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            elemStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            elemStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, isMono ? DesignSystem.FontMono : DesignSystem.FontUi));
            elemStyle.Setters.Add(new Setter(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular));
            elemStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new DynamicResourceExtension(isPrimaryEmphasis ? "FgPrimary" : "FgSecondary")));
            elemStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty,
                isPrimaryEmphasis ? FontWeights.Medium : FontWeights.Normal));

            if (rightAlign)
                elemStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));

            col.ElementStyle = elemStyle;
            return col;
        }

        public static DataGridTemplateColumn MakeBadgeColumn(
            string header,
            string bindingPath,
            string sortPath,
            DataGridLength width)
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = header;
            col.SortMemberPath = sortPath;
            col.Width = width;
            if (width.IsStar)
            {
                col.MinWidth = 115;
            }

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border));
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            pill.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1));
            pill.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            pill.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            pill.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            pill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
            pill.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderCard"));

            FrameworkElementFactory txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            txt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontUi);
            txt.SetValue(TextBlock.FontSizeProperty, 11.0);
            txt.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
            txt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgSecondary"));
            txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            pill.AppendChild(txt);
            dt.VisualTree = pill;
            col.CellTemplate = dt;
            return col;
        }

        public static DataGridTemplateColumn MakeShareBarColumn(
            string header,
            string textBindingPath,
            string ratioBindingPath,
            string sortPath,
            DataGridLength width)
        {
            DataGridTemplateColumn col = new DataGridTemplateColumn();
            col.Header = header;
            col.SortMemberPath = sortPath;
            col.Width = width;
            if (width.IsStar)
            {
                col.MinWidth = 115;
            }
            Style hdrRight = new Style(typeof(DataGridColumnHeader));
            hdrRight.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
            hdrRight.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            hdrRight.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgMuted")));
            hdrRight.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
            hdrRight.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            hdrRight.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
            hdrRight.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            hdrRight.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderCard")));
            col.HeaderStyle = hdrRight;

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            // 右侧等宽百分比文本
            FrameworkElementFactory pctTxt = new FrameworkElementFactory(typeof(TextBlock));
            pctTxt.SetValue(DockPanel.DockProperty, Dock.Right);
            pctTxt.SetValue(FrameworkElement.MinWidthProperty, 42.0);
            pctTxt.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            pctTxt.SetValue(TextBlock.FontFamilyProperty, DesignSystem.FontMono);
            pctTxt.SetValue(TextBlock.FontSizeProperty, 12.0);
            pctTxt.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            pctTxt.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("FgSecondary"));
            pctTxt.SetBinding(TextBlock.TextProperty, new Binding(textBindingPath));
            dock.AppendChild(pctTxt);

            // 左侧柔和进度微条（轨道 40px）
            const double barMaxW = 40.0;
            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border));
            track.SetValue(FrameworkElement.WidthProperty, barMaxW);
            track.SetValue(FrameworkElement.HeightProperty, 5.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.5));
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            track.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgSurface"));

            FrameworkElementFactory fill = new FrameworkElementFactory(typeof(Border));
            fill.SetValue(FrameworkElement.HeightProperty, 5.0);
            fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(2.5));
            fill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            fill.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BtnPrimaryBg"));

            Binding widthBinding = new Binding(ratioBindingPath);
            widthBinding.Converter = new RatioToWidthConverter(barMaxW);
            fill.SetBinding(FrameworkElement.WidthProperty, widthBinding);

            track.AppendChild(fill);
            dock.AppendChild(track);

            dt.VisualTree = dock;
            col.CellTemplate = dt;
            return col;
        }

        public static void UpdateItemsPreservingState(
            DataGrid dg,
            IList newItems,
            Func<object, string> keySelector,
            bool keepTopIfNearTop)
        {
            if (dg == null) return;

            // 1. 记录原视图排序描述与列头箭头方向
            List<SortDescription> savedSorts = new List<SortDescription>();
            if (dg.ItemsSource != null)
            {
                ICollectionView oldView = CollectionViewSource.GetDefaultView(dg.ItemsSource);
                if (oldView != null && oldView.SortDescriptions != null && oldView.SortDescriptions.Count > 0)
                {
                    for (int i = 0; i < oldView.SortDescriptions.Count; i++)
                        savedSorts.Add(oldView.SortDescriptions[i]);
                }
            }

            ListSortDirection?[] colDirs = new ListSortDirection?[dg.Columns.Count];
            for (int c = 0; c < dg.Columns.Count; c++)
            {
                colDirs[c] = dg.Columns[c].SortDirection;
            }

            // 2. 记录原选中项唯一键
            string selectedKey = null;
            if (keySelector != null && dg.SelectedItem != null)
            {
                try { selectedKey = keySelector(dg.SelectedItem); } catch { }
            }

            // 3. 记录原滚动条偏移
            ScrollViewer sv = FindVisualChild<ScrollViewer>(dg);
            double oldVertOffset = (sv != null) ? sv.VerticalOffset : 0.0;
            double oldHorzOffset = (sv != null) ? sv.HorizontalOffset : 0.0;
            if (keepTopIfNearTop)
            {
                bool rowScroll = ScrollViewer.GetCanContentScroll(dg);
                double nearThreshold = rowScroll ? 1.5 : 24.0;
                if (oldVertOffset <= nearThreshold)
                    oldVertOffset = 0.0;
            }

            // 4. 切换数据源并立即恢复排序规则
            dg.ItemsSource = newItems;

            if (savedSorts.Count > 0 && newItems != null)
            {
                ICollectionView newView = CollectionViewSource.GetDefaultView(dg.ItemsSource);
                if (newView != null)
                {
                    using (newView.DeferRefresh())
                    {
                        newView.SortDescriptions.Clear();
                        for (int i = 0; i < savedSorts.Count; i++)
                            newView.SortDescriptions.Add(savedSorts[i]);
                    }
                }
                for (int c = 0; c < dg.Columns.Count && c < colDirs.Length; c++)
                {
                    dg.Columns[c].SortDirection = colDirs[c];
                }
            }

            // 5. 恢复选中行（不强制 ScrollIntoView，防止打断用户浏览）
            if (!string.IsNullOrEmpty(selectedKey) && newItems != null && keySelector != null)
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    object item = newItems[i];
                    if (item == null) continue;
                    string k = null;
                    try { k = keySelector(item); } catch { }
                    if (string.Equals(k, selectedKey, StringComparison.Ordinal))
                    {
                        dg.SelectedItem = item;
                        break;
                    }
                }
            }

            // 6. 精准恢复滚动位置
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(oldVertOffset);
                if (oldHorzOffset > 0.0)
                    sv.ScrollToHorizontalOffset(oldHorzOffset);

                if (dg.IsLoaded && (oldVertOffset > 0.0 || oldHorzOffset > 0.0))
                {
                    double targetV = oldVertOffset;
                    double targetH = oldHorzOffset;
                    ScrollViewer capturedSv = sv;
                    dg.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate()
                    {
                        try
                        {
                            capturedSv.ScrollToVerticalOffset(targetV);
                            if (targetH > 0.0)
                                capturedSv.ScrollToHorizontalOffset(targetH);
                        }
                        catch { }
                    }));
                }
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T typed = child as T;
                if (typed != null)
                    return typed;
                T sub = FindVisualChild<T>(child);
                if (sub != null)
                    return sub;
            }
            return null;
        }
    }

    internal sealed class ToastManager
    {
        public StackPanel HostElement { get; private set; }

        public ToastManager()
        {
            HostElement = new StackPanel();
            HostElement.VerticalAlignment = VerticalAlignment.Bottom;
            HostElement.HorizontalAlignment = HorizontalAlignment.Right;
            HostElement.Margin = new Thickness(0, 0, 20, 20);
            Panel.SetZIndex(HostElement, 999);
        }

        public void Show(string title, string detail, string variant)
        {
            ShowWithAction(title, detail, variant, null, null);
        }

        public void ShowWithAction(string title, string detail, string variant, string actionText, Action onAction)
        {
            if (HostElement == null) return;

            // 控制最多同时堆叠 3 条通知
            while (HostElement.Children.Count >= 3)
            {
                HostElement.Children.RemoveAt(0);
            }

            string accentKey = "AccentText";
            string iconData = UiIcons.Activity;
            if (string.Equals(variant, "success", StringComparison.OrdinalIgnoreCase))
            {
                accentKey = "SemanticSuccess";
                iconData = UiIcons.Check;
            }
            else if (string.Equals(variant, "warning", StringComparison.OrdinalIgnoreCase))
            {
                accentKey = "SemanticWarning";
                iconData = UiIcons.Alert;
            }
            else if (string.Equals(variant, "error", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(variant, "danger", StringComparison.OrdinalIgnoreCase))
            {
                accentKey = "SemanticDanger";
                iconData = UiIcons.Alert;
            }

            TranslateTransform slideTrans = new TranslateTransform(0, 12);

            Grid toastCard = new Grid();
            toastCard.MinWidth = 260;
            toastCard.MaxWidth = 400;
            toastCard.Margin = new Thickness(0, 8, 0, 0);
            toastCard.Opacity = 0.0;
            toastCard.RenderTransform = slideTrans;
            toastCard.UseLayoutRounding = true;
            toastCard.SnapsToDevicePixels = true;

            Border toastShadowBd = new Border();
            toastShadowBd.CornerRadius = new CornerRadius(8);
            toastShadowBd.IsHitTestVisible = false;
            toastShadowBd.SetResourceReference(Border.BackgroundProperty, "BgElevated");

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 16;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Color = Colors.Black;
            shadow.Opacity = 0.28;
            toastShadowBd.Effect = shadow;
            toastCard.Children.Add(toastShadowBd);

            Border toastInnerBd = new Border();
            toastInnerBd.Padding = new Thickness(12, 10, 10, 10);
            toastInnerBd.CornerRadius = new CornerRadius(8);
            toastInnerBd.BorderThickness = new Thickness(1);
            toastInnerBd.SnapsToDevicePixels = true;
            toastInnerBd.SetResourceReference(Border.BackgroundProperty, "BgElevated");
            toastInnerBd.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧语义图标圆底
            Border iconWrap = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            iconWrap.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            iconWrap.Child = UiIcons.CreateIcon(iconData, 12, accentKey);
            Grid.SetColumn(iconWrap, 0);
            g.Children.Add(iconWrap);

            // 中间标题与详情
            StackPanel textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock titleTb = new TextBlock
            {
                Text = title ?? "",
                FontFamily = DesignSystem.FontUi,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            textStack.Children.Add(titleTb);

            if (!string.IsNullOrEmpty(detail))
            {
                TextBlock detailTb = new TextBlock
                {
                    Text = detail,
                    FontFamily = DesignSystem.FontUi,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                detailTb.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                textStack.Children.Add(detailTb);
            }
            Grid.SetColumn(textStack, 1);
            g.Children.Add(textStack);

            // 右侧可选动作按钮与关闭按钮
            StackPanel rightActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            bool closing = false;
            DispatcherTimer autoTimer = new DispatcherTimer();
            autoTimer.Interval = TimeSpan.FromMilliseconds(3500);

            Action dismissToast = delegate()
            {
                if (closing) return;
                closing = true;
                autoTimer.Stop();

                if (!toastCard.IsLoaded)
                {
                    HostElement.Children.Remove(toastCard);
                    return;
                }

                DoubleAnimation fadeOut = new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(180)));
                fadeOut.Completed += delegate(object s2, EventArgs e2)
                {
                    HostElement.Children.Remove(toastCard);
                };
                toastCard.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };

            if (!string.IsNullOrEmpty(actionText) && onAction != null)
            {
                Button actBtn = ModernControlsFactory.CreateButton(actionText, "secondary");
                actBtn.Height = 24;
                actBtn.FontSize = 11;
                actBtn.Padding = new Thickness(8, 0, 8, 0);
                actBtn.Margin = new Thickness(0, 0, 4, 0);
                actBtn.Click += delegate(object s, RoutedEventArgs e)
                {
                    dismissToast();
                    try { onAction(); } catch { }
                };
                rightActions.Children.Add(actBtn);
            }

            Button closeBtn = ModernControlsFactory.CreateIconGhostButton("M 3,3 L 13,13 M 13,3 L 3,13", "关闭提示");
            closeBtn.Width = 22;
            closeBtn.Height = 22;
            closeBtn.Click += delegate(object s, RoutedEventArgs e) { dismissToast(); };
            rightActions.Children.Add(closeBtn);

            Grid.SetColumn(rightActions, 2);
            g.Children.Add(rightActions);

            toastInnerBd.Child = g;
            toastCard.Children.Add(toastInnerBd);
            toastCard.MouseEnter += delegate(object s, MouseEventArgs e) { if (!closing) autoTimer.Stop(); };
            toastCard.MouseLeave += delegate(object s, MouseEventArgs e) { if (!closing) autoTimer.Start(); };

            HostElement.Children.Add(toastCard);

            // 滑入淡入动画
            DoubleAnimation fadeIn = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(180)));
            DoubleAnimation slideUp = new DoubleAnimation(12.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            slideUp.EasingFunction = ease;

            toastCard.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            slideTrans.BeginAnimation(TranslateTransform.YProperty, slideUp);

            autoTimer.Tick += delegate(object s, EventArgs e)
            {
                dismissToast();
            };
            autoTimer.Start();
        }
    }
}
