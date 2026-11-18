using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;

namespace TokLens
{
    // Remembers the main window's last size. The position is deliberately not stored: the
    // window always opens centred, so a changed monitor layout can never leave it off-screen.
    public sealed class WindowPlacementStore
    {
        public const double MinWidth = 900.0;
        public const double MinHeight = 580.0;

        public double Width, Height;
        public bool Maximized;

        private static string FilePath { get { return Path.Combine(SyncService.AppConfigDir, "window.json"); } }

        // First launch: a comfortable part of the work area instead of nearly all of it.
        public static Size DefaultSize(Rect workArea)
        {
            return Clamp(new Size(Math.Min(1180.0, workArea.Width * 0.76), Math.Min(760.0, workArea.Height * 0.84)), workArea);
        }

        public static Size Clamp(Size size, Rect workArea)
        {
            double width = double.IsNaN(size.Width) || double.IsInfinity(size.Width) ? MinWidth : size.Width;
            double height = double.IsNaN(size.Height) || double.IsInfinity(size.Height) ? MinHeight : size.Height;
            double maxWidth = Math.Max(MinWidth, workArea.Width), maxHeight = Math.Max(MinHeight, workArea.Height);
            return new Size(Math.Round(Math.Max(MinWidth, Math.Min(maxWidth, width))), Math.Round(Math.Max(MinHeight, Math.Min(maxHeight, height))));
        }

        public static bool TryLoad(out Size size, out bool maximized)
        {
            size = Size.Empty; maximized = false;
            try
            {
                if (!File.Exists(FilePath)) return false;
                WindowPlacementStore saved = new JavaScriptSerializer().Deserialize<WindowPlacementStore>(File.ReadAllText(FilePath));
                if (saved == null || !(saved.Width > 0) || !(saved.Height > 0)) return false;
                size = new Size(saved.Width, saved.Height); maximized = saved.Maximized;
                return true;
            }
            catch { return false; }
        }

        public static void Save(Size size, bool maximized)
        {
            if (size.IsEmpty || !(size.Width > 0) || !(size.Height > 0)) return;
            Directory.CreateDirectory(SyncService.AppConfigDir);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(new WindowPlacementStore { Width = Math.Round(size.Width), Height = Math.Round(size.Height), Maximized = maximized }));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
        }
    }
}
