# Regenerate the editor's original 24-unit icons. They are editor resources,
# rather than Content assets. RmlUi's GL2 backend loads uncompressed TGA
# textures, so no icon font or SVG runtime is required.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Resources\Editor\Icons'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('SharpEngine.Tools.EditorIconBuilder' -as [type])) {
    $references = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Color].Assembly.Location)
    foreach ($reference in [System.Drawing.Bitmap].Assembly.GetReferencedAssemblies()) {
        if ($reference.Name -like 'System.Private.Windows.*') {
            $references += [System.Reflection.Assembly]::Load($reference).Location
        }
    }
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SharpEngine.Tools
{
    public static class EditorIconBuilder
    {
        static readonly Color Ink = Color.FromArgb(205, 217, 233);
        static readonly Color Blue = Color.FromArgb(112, 177, 241);
        static readonly Color Gold = Color.FromArgb(229, 183, 91);
        static readonly Color Green = Color.FromArgb(120, 206, 159);
        static readonly Color Purple = Color.FromArgb(188, 151, 232);
        static readonly Color Teal = Color.FromArgb(102, 200, 201);

        static PointF[] Points(params float[] xy)
        {
            var points = new PointF[xy.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(xy[2*i], xy[2*i+1]);
            return points;
        }
        static Pen Pen(Color color, float width = 1.7f) => new Pen(color, width) {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        static void Line(Graphics g, Color color, params float[] xy)
        { using (var pen = Pen(color)) g.DrawLines(pen, Points(xy)); }
        static void Polygon(Graphics g, Color color, params float[] xy)
        { using (var brush = new SolidBrush(color)) g.FillPolygon(brush, Points(xy)); }
        static void Box(Graphics g, Color color, float x, float y, float w, float h, bool fill = false)
        {
            if (fill) { using (var brush = new SolidBrush(color)) g.FillRectangle(brush, x, y, w, h); }
            else { using (var pen = Pen(color)) g.DrawRectangle(pen, x, y, w, h); }
        }
        static void Circle(Graphics g, Color color, float x, float y, float size, bool fill = false)
        {
            if (fill) { using (var brush = new SolidBrush(color)) g.FillEllipse(brush, x, y, size, size); }
            else { using (var pen = Pen(color)) g.DrawEllipse(pen, x, y, size, size); }
        }
        static void Arc(Graphics g, Color color, float x, float y, float w, float h, float start, float sweep)
        { using (var pen = Pen(color)) g.DrawArc(pen, x, y, w, h, start, sweep); }
        static void Document(Graphics g, Color color)
        {
            Line(g, color, 14,3, 5,3, 5,21, 19,21, 19,8, 14,3, 14,8, 19,8);
        }
        static void Cube(Graphics g)
        {
            Polygon(g, Color.FromArgb(48, 77, 106), 12,3, 21,8, 21,17, 12,22, 3,17, 3,8);
            Line(g, Blue, 12,3, 21,8, 21,17, 12,22, 3,17, 3,8, 12,3);
            Line(g, Blue, 3,8, 12,13, 21,8);
            Line(g, Blue, 12,13, 12,22);
        }
        static void Draw(Graphics g, string name)
        {
            switch (name)
            {
                case "folder":
                    Polygon(g, Color.FromArgb(175,132,57), 2,6, 3,4, 9,4, 12,7, 21,7, 22,9, 22,19, 2,19);
                    Polygon(g, Gold, 2,9, 22,9, 22,19, 2,19);
                    Line(g, Color.FromArgb(249,207,124), 3,10, 21,10);
                    break;
                case "mesh": Cube(g); break;
                case "material":
                    Circle(g, Color.FromArgb(77,58,102), 3,3,18,true);
                    Circle(g, Purple, 3,3,18);
                    Arc(g, Purple, 6,4,11,14,75,160);
                    Arc(g, Purple, 4,9,16,10,0,180);
                    Circle(g, Color.FromArgb(231,211,255), 8,6,4,true);
                    break;
                case "world":
                    Circle(g, Teal, 3,3,18);
                    Arc(g, Teal, 8,3,8,18,0,360);
                    Line(g, Teal, 3,12, 21,12);
                    Arc(g, Teal, 4,5,16,6,0,180);
                    break;
                case "ui":
                    Box(g, Blue,3,4,18,16);
                    Line(g, Blue,3,9,21,9);
                    Box(g, Blue,6,12,4,5,true);
                    Line(g, Blue,13,13,18,13); Line(g, Blue,13,17,17,17);
                    break;
                case "style":
                    Line(g, Purple,8,4,6,4,6,10,3,12,6,14,6,20,8,20);
                    Line(g, Purple,16,4,18,4,18,10,21,12,18,14,18,20,16,20);
                    Line(g, Purple,13,8,11,16);
                    break;
                case "texture":
                    Box(g, Green,3,4,18,16); Circle(g, Green,6,7,4,true);
                    Line(g, Green,4,18,10,12,14,16,17,12,21,17);
                    break;
                case "control-container":
                    Box(g, Blue, 3, 4, 18, 16);
                    Box(g, Blue, 7, 8, 10, 8);
                    break;
                case "control-horizontal":
                    Box(g, Blue, 3, 5, 7, 14);
                    Box(g, Blue, 14, 5, 7, 14);
                    break;
                case "control-vertical":
                    Box(g, Blue, 3, 4, 18, 6);
                    Box(g, Blue, 3, 14, 18, 6);
                    break;
                case "control-text":
                    Line(g, Green, 4, 5, 20, 5);
                    Line(g, Green, 12, 5, 12, 20);
                    Line(g, Green, 8, 20, 16, 20);
                    break;
                case "control-button":
                    Box(g, Gold, 3, 6, 18, 12);
                    Line(g, Gold, 8, 12, 16, 12);
                    break;
                case "control-input":
                    Box(g, Gold, 2, 6, 20, 12);
                    Line(g, Gold, 7, 9, 7, 15);
                    Line(g, Gold, 12, 15, 18, 15);
                    break;
                case "control-checkbox":
                    Box(g, Gold, 4, 4, 16, 16);
                    Line(g, Gold, 7, 12, 11, 16, 18, 8);
                    break;
                case "control-slider":
                    Line(g, Gold, 3, 12, 21, 12);
                    Box(g, Gold, 12, 7, 4, 10, true);
                    break;
                case "control-progress":
                    Box(g, Green, 2, 7, 20, 10);
                    Box(g, Green, 5, 10, 9, 4, true);
                    break;
                case "control-reference":
                    Box(g, Purple, 3, 3, 12, 12);
                    Box(g, Purple, 9, 9, 12, 12);
                    break;
                case "control-slot":
                    Line(g, Purple, 8, 4, 4, 4, 4, 20, 8, 20);
                    Line(g, Purple, 16, 4, 20, 4, 20, 20, 16, 20);
                    Line(g, Purple, 9, 12, 15, 12);
                    break;
                case "audio":
                    Polygon(g, Gold,3,9,7,9,12,5,12,19,7,15,3,15);
                    Arc(g, Gold,11,7,8,10,-65,130);
                    Arc(g, Gold,11,3,12,18,-60,120);
                    break;
                case "data":
                    Document(g, Blue); Line(g, Blue,8,12,11,12); Line(g, Blue,8,16,16,16);
                    break;
                case "file":
                    Document(g, Ink); Line(g, Ink,8,12,15,12); Line(g, Ink,8,16,13,16);
                    break;
                case "search":
                    Circle(g, Color.FromArgb(130,149,173),4,4,11); Line(g, Color.FromArgb(130,149,173),14,14,20,20);
                    break;
                case "filter": Line(g, Ink,3,4,21,4,14,12,14,19,10,21,10,12,3,4); break;
                case "reset":
                    Arc(g, Ink,4,4,16,16,-50,290); Line(g, Ink,20,4,20,10,14,10); break;
                case "up": Line(g, Ink,12,20,12,4); Line(g, Ink,5,11,12,4,19,11); break;
                case "down":
                    Line(g, Ink,12,4,12,20);
                    Line(g, Ink,5,13,12,20,19,13);
                    break;
                case "check":
                    Line(g, Color.White,5,12,10,17,19,7);
                    break;
                case "chevron-down":
                    Line(g, Ink,6,9,12,15,18,9);
                    break;
                case "chevron-right":
                    Line(g, Ink,9,6,15,12,9,18);
                    break;
                case "trash":
                    Line(g, Ink,5,7,19,7);
                    Line(g, Ink,9,7,9,4,15,4,15,7);
                    Line(g, Ink,7,7,8,21,16,21,17,7);
                    Line(g, Ink,10,10,10,18);
                    Line(g, Ink,14,10,14,18);
                    break;
                case "lock":
                    Arc(g, Ink,7,3,10,12,180,180); Box(g, Ink,5,10,14,11); Circle(g, Ink,10.5f,14,3,true); break;
                case "save":
                    Line(g, Ink,4,3,18,3,21,6,21,21,3,21,3,3,4,3);
                    Box(g, Ink,7,3,9,6); Box(g, Ink,7,14,10,7); break;
                case "export":
                    Line(g, Ink,10,4,4,4,4,20,20,20,20,14);
                    Line(g, Ink,11,13,21,3); Line(g, Ink,14,3,21,3,21,10); break;
                case "play": Polygon(g, Green,7,4,21,12,7,20); break;
                case "pause": Box(g, Gold,6,5,4,14,true); Box(g, Gold,14,5,4,14,true); break;
                case "stop": Box(g, Color.FromArgb(224,137,146),5,5,14,14,true); break;
                case "move":
                    Line(g, Ink,12,2,12,22); Line(g, Ink,2,12,22,12);
                    Line(g, Ink,8,6,12,2,16,6); Line(g, Ink,8,18,12,22,16,18);
                    Line(g, Ink,6,8,2,12,6,16); Line(g, Ink,18,8,22,12,18,16); break;
                case "rotate":
                    Arc(g, Ink,4,4,16,16,-45,295); Line(g, Ink,20,3,20,10,13,10); break;
                case "scale":
                    Box(g, Ink,3,14,7,7); Line(g, Ink,10,14,21,3); Line(g, Ink,13,3,21,3,21,11); break;
                case "add": Line(g, Ink,12,4,12,20); Line(g, Ink,4,12,20,12); break;
                case "more":
                    Circle(g, Ink,3,10,4,true); Circle(g, Ink,10,10,4,true); Circle(g, Ink,17,10,4,true); break;
                case "minimize": Line(g, Ink,5,13,19,13); break;
                case "maximize": Box(g, Ink,5,5,14,14); break;
                case "close": Line(g, Ink,6,6,18,18); Line(g, Ink,6,18,18,6); break;
                case "all":
                    Box(g, Ink,4,4,6,6); Box(g, Ink,14,4,6,6); Box(g, Ink,4,14,6,6); Box(g, Ink,14,14,6,6); break;
                case "list":
                    Box(g, Ink,3,4,4,4,true); Box(g, Ink,3,10,4,4,true); Box(g, Ink,3,16,4,4,true);
                    Line(g, Ink,10,6,21,6); Line(g, Ink,10,12,21,12); Line(g, Ink,10,18,21,18); break;
                case "engine":
                    for (int i=0; i<8; i++)
                    {
                        g.TranslateTransform(12,12); g.RotateTransform(45);
                        Box(g, Ink,-1,-10,2,4,true); g.TranslateTransform(-12,-12);
                    }
                    Circle(g, Ink,5,5,14); Circle(g, Ink,9,9,6); break;
                case "camera":
                    Box(g, Blue,3,6,13,12); Line(g, Blue,16,10,22,6,22,18,16,14); break;
                case "light":
                    Circle(g, Gold,8,8,8);
                    for (int i=0; i<8; i++)
                    {
                        g.TranslateTransform(12,12); g.RotateTransform(45);
                        Line(g, Gold,0,-8,0,-10); g.TranslateTransform(-12,-12);
                    }
                    break;
            }
        }
        public static void Build(string directory)
        {
            Directory.CreateDirectory(directory);
            string[] names = { "folder","mesh","material","world","ui","style","texture","audio","data","file",
                "search","filter","reset","up","lock","save","export","play","pause","stop","move","rotate","scale",
                "add","more","minimize","maximize","close","all","list","engine","camera","light",
                "down","check","chevron-down","chevron-right","trash",
                "control-container", "control-horizontal", "control-vertical", "control-text",
                "control-button", "control-input", "control-checkbox", "control-slider",
                "control-progress", "control-reference", "control-slot" };
            foreach (string name in names)
            {
                using (var bitmap = new Bitmap(48,48,PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.ScaleTransform(2,2);
                        Draw(g, name);
                    }
                    using (var writer = new BinaryWriter(File.Create(Path.Combine(directory,name+".tga"))))
                    {
                        // True-color BGRA, top-left origin, eight alpha bits.
                        writer.Write(new byte[] { 0,0,2,0,0,0,0,0,0,0,0,0,48,0,48,0,32,0x28 });
                        for (int y=0; y<48; y++) for (int x=0; x<48; x++)
                        {
                            Color c=bitmap.GetPixel(x,y);
                            writer.Write(c.B); writer.Write(c.G); writer.Write(c.R); writer.Write(c.A);
                        }
                    }
                }
            }
        }
    }
}
'@
}
[SharpEngine.Tools.EditorIconBuilder]::Build([System.IO.Path]::GetFullPath($OutputDirectory))
Write-Output "Generated 49 editor icons in $([System.IO.Path]::GetFullPath($OutputDirectory))"
