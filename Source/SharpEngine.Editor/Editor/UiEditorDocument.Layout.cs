using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal sealed record UiLayoutMetrics(float X, float Y, float Width, float Height,
    float ParentWidth, float ParentHeight, float ViewportWidth, float ViewportHeight,
    float FontSize = 16, float RootFontSize = 16, float Density = 1,
    float ContentWidth = 0, float ContentHeight = 0)
{
    internal static UiLayoutMetrics? Read(JsonObject? Source)
    {
        if (Source?["schemaVersion"]?.GetValue<int>() != 1)
        {
            return null;
        }
        float Number(string Key) => Source[Key]!.GetValue<float>();
        return new(Number("x"), Number("y"), Number("width"), Number("height"),
            Number("parentWidth"), Number("parentHeight"), Number("viewportWidth"), Number("viewportHeight"),
            Number("fontSize"), Number("rootFontSize"), Number("density"), Number("contentWidth"), Number("contentHeight"));
    }
}

internal readonly record struct UiLayoutLength(float Value, string Unit, float PixelsPerUnit)
{
    internal float Pixels
    {
        get
        {
            return Value * PixelsPerUnit;
        }
    }

    internal string Offset(float Delta) => Format(Value + Delta / PixelsPerUnit, Unit);

    internal static string Format(float Value, string Unit = "px") =>
        Math.Round(Value, 4).ToString("0.####", CultureInfo.InvariantCulture) + Unit;

    internal static UiLayoutLength? Parse(string Text, string Property, UiLayoutMetrics Metrics)
    {
        var Match = Regex.Match(Text.Trim(), @"^(-?\d+(?:\.\d+)?)(px|%|dp|em|rem|vw|vh|pt)?$", RegexOptions.CultureInvariant);
        var Valid = float.TryParse(Match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var Value);
        if (!Match.Success || !Valid || !float.IsFinite(Value))
        {
            return null;
        }
        var Unit = Match.Groups[2].Value;
        Unit = Unit.Length == 0 ? "px" : Unit;
        var Vertical = Property is "top" or "bottom" or "height" or "min-height" or "max-height";
        // RCSS percentage margins (including top/bottom) refer to containing-block width.
        var Ratio = Unit switch
        {
            "%" => (Vertical ? Metrics.ParentHeight : Metrics.ParentWidth) / 100,
            "dp" => Metrics.Density,
            "em" => Metrics.FontSize,
            "rem" => Metrics.RootFontSize,
            "vw" => Metrics.ViewportWidth / 100,
            "vh" => Metrics.ViewportHeight / 100,
            "pt" => 96f / 72,
            _ => 1,
        };
        return Ratio > 0 && float.IsFinite(Ratio) ? new(Value, Unit, Ratio) : null;
    }
}

internal sealed partial class UiEditorDocument
{
    private bool ApplyGestureChanges(XElement Element, string Handle, Dictionary<string, string> Changes, out string Message)
    {
        Message = string.Empty;
        Element.SetAttributeValue("style", Gesture!.OriginalStyle);
        foreach (var Change in Changes)
        {
            WriteDeclaration(Element, Change.Key, Change.Value);
        }
        Gesture!.Handle = Handle;
        Gesture.Changed = true;
        Revision++;
        return true;
    }

    private static void OffsetLength(Dictionary<string, string> Changes, UiEditorGestureBase Base, string Name, float Delta, float Default = 0)
    {
        var Length = Base.Lengths!.GetValueOrDefault(Name, new UiLayoutLength(Default, "px", 1));
        Changes[Name] = Length.Offset(Delta);
    }

    private static void BuildMoveAxis(Dictionary<string, string> Changes, UiEditorGestureBase Base, bool Horizontal, float Delta)
    {
        if (Delta == 0)
        {
            return;
        }
        var Start = Horizontal ? "left" : "top";
        var End = Horizontal ? "right" : "bottom";
        if (!Base.Absolute || Base.ParentMode == "overlay")
        {
            var AlignedToEnd = Base.ParentMode == "overlay" && (Horizontal ? Base.AlignmentX : Base.AlignmentY) == "end";
            OffsetLength(Changes, Base, "margin-" + (AlignedToEnd ? End : Start), AlignedToEnd ? -Delta : Delta);
            return;
        }
        var HasStart = Base.Lengths!.ContainsKey(Start);
        var HasEnd = Base.Lengths.ContainsKey(End);
        var Fill = (Horizontal ? Base.WidthMode : Base.HeightMode) == "fill";
        if (HasStart || !HasEnd)
        {
            OffsetLength(Changes, Base, Start, Delta, Horizontal ? Base.Left : Base.Top);
        }
        if (HasEnd && (!HasStart || Fill))
        {
            OffsetLength(Changes, Base, End, -Delta);
        }
    }

    private static bool BuildResizeAxis(Dictionary<string, string> Changes, UiEditorGestureBase Base, bool Horizontal,
        bool Leading, float Delta, out string Message)
    {
        Message = string.Empty;
        if (Delta == 0)
        {
            return true;
        }
        var Mode = Horizontal ? Base.WidthMode : Base.HeightMode;
        var Start = Horizontal ? "left" : "top";
        var End = Horizontal ? "right" : "bottom";
        var Axis = Horizontal ? "width" : "height";
        var HasStart = Base.Lengths!.ContainsKey(Start);
        var HasEnd = Base.Lengths.ContainsKey(End);
        var Extent = (Horizontal ? Base.Width : Base.Height) + (Leading ? -Delta : Delta);
        if (Extent < 8)
        {
            Message = "Controls cannot be resized below 8px";
            return false;
        }
        if (Mode == "fill" && Base.Absolute && HasStart && HasEnd)
        {
            OffsetLength(Changes, Base, Leading ? Start : End, Leading ? Delta : -Delta);
            return true;
        }
        if (Mode == "fill")
        {
            Message = "Fill size is owned by the parent layout; edit its spacing or change sizing mode first";
            return false;
        }
        if (!Base.Absolute && Leading)
        {
            Message = "Flow controls resize from the trailing edge; move them by editing slot spacing";
            return false;
        }
        // Preserve the authored content-box size; the measured border box is only a constraint.
        OffsetLength(Changes, Base, Axis, Leading ? -Delta : Delta);
        if (Base.Absolute && Leading && HasStart)
        {
            OffsetLength(Changes, Base, Start, Delta);
        }
        else if (Base.Absolute && !Leading && !HasStart && HasEnd)
        {
            OffsetLength(Changes, Base, End, -Delta);
        }
        return true;
    }

    internal int PreviewWidth
    {
        get;
        private set;
    } = DesignWidth;
    internal int PreviewHeight
    {
        get;
        private set;
    } = DesignHeight;
    internal float PreviewZoom
    {
        get;
        private set;
    } = 1;

    internal bool SetPreviewZoom(float Zoom)
    {
        if (!float.IsFinite(Zoom) || Zoom is < 0.01f or > 2 || IsGesturing)
        {
            return false;
        }
        PreviewZoom = Zoom;
        return true;
    }

    internal bool SetPreviewSize(int Width, int Height, out string Message)
    {
        Message = string.Empty;
        if (Width is < 160 or > 7680 || Height is < 120 or > 4320 || IsGesturing)
        {
            Message = "Preview size must be 160–7680 by 120–4320; finish the current drag first";
            return false;
        }
        PreviewWidth = Width;
        PreviewHeight = Height;
        RefreshDerivedState();
        return true;
    }

    internal string LayoutOf(XElement? Element)
    {
        if (Element is null || Element == Body())
        {
            return "free";
        }
        var Mode = AttributeOf(Element, "data-ui-layout");
        var Style = Sheet.Compute(Element);
        if (Mode is "free" or "horizontal" or "vertical" or "overlay")
        {
            var Display = Style.GetValueOrDefault("display")?.Value ?? "block";
            var Direction = Style.GetValueOrDefault("flex-direction")?.Value ?? "row";
            var Position = Style.GetValueOrDefault("position")?.Value ?? "static";
            var Matches = Mode switch
            {
                "horizontal" => Display == "flex" && Direction == "row",
                "vertical" => Display == "flex" && Direction == "column",
                _ => Display == "block" && Position != "static",
            };
            return Matches ? Mode : "custom";
        }
        if (Style.GetValueOrDefault("display")?.Value == "flex")
        {
            var Direction = Style.GetValueOrDefault("flex-direction")?.Value ?? "row";
            return Direction == "column" ? "vertical" : Direction == "row" ? "horizontal" : "custom";
        }
        return "custom";
    }

    internal string SlotLayoutOf(XElement Element)
    {
        var Parent = LayoutOf(Element.Parent);
        if (Element.Parent == Body())
        {
            return Parent;
        }
        var Position = Sheet.Compute(Element).GetValueOrDefault("position")?.Value ?? "static";
        var Absolute = Position is "absolute" or "fixed";
        if (Parent is "horizontal" or "vertical" && Absolute || Parent is "free" or "overlay" && !Absolute)
        {
            return "custom";
        }
        return Parent;
    }

    private bool HasCustomTransform(XElement Element)
    {
        foreach (var Candidate in Element.AncestorsAndSelf().TakeWhile(Candidate => Candidate != Body()))
        {
            var Transform = Sheet.Compute(Candidate).GetValueOrDefault("transform")?.Value;
            if (Transform is null or "none")
            {
                continue;
            }
            var X = AttributeOf(Candidate, "data-ui-align-x") == "center" ? "-50%" : "0px";
            var Y = AttributeOf(Candidate, "data-ui-align-y") == "center" ? "-50%" : "0px";
            var Expected = "translate(" + X + "," + Y + ")";
            if (Transform.Replace(" ", string.Empty, StringComparison.Ordinal) != Expected)
            {
                return true;
            }
        }
        return false;
    }

    internal string AlignmentOf(XElement Element, bool Horizontal)
    {
        var Marked = AttributeOf(Element, Horizontal ? "data-ui-align-x" : "data-ui-align-y");
        if (Marked.Length > 0)
        {
            return Marked;
        }
        var Style = Sheet.Compute(Element);
        var Start = Style.GetValueOrDefault(Horizontal ? "left" : "top")?.Value ?? "auto";
        var End = Style.GetValueOrDefault(Horizontal ? "right" : "bottom")?.Value ?? "auto";
        if (Start == "auto" && End != "auto")
        {
            return "end";
        }
        return Style.GetValueOrDefault("align-self")?.Value switch
        {
            "center" => "center",
            "flex-end" => "end",
            _ => "start",
        };
    }

    private static bool HasLayoutBox(XElement Element) => !Element.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal) || Element.Name.LocalName == "ui-use";

    internal string AxisMode(XElement Element, bool Horizontal)
    {
        var Style = Sheet.Compute(Element);
        var Axis = Horizontal ? "width" : "height";
        var Value = Style.GetValueOrDefault(Axis)?.Value ?? "auto";
        var Parent = SlotLayoutOf(Element);
        var MainAxis = Horizontal ? Parent == "horizontal" : Parent == "vertical";
        var Absolute = Style.GetValueOrDefault("position")?.Value == "absolute";
        var Start = Style.GetValueOrDefault(Horizontal ? "left" : "top")?.Value ?? "auto";
        var End = Style.GetValueOrDefault(Horizontal ? "right" : "bottom")?.Value ?? "auto";
        var Grow = Style.GetValueOrDefault("flex-grow")?.Value;
        if (MainAxis && Grow is not null && Grow != "0" ||
            Absolute && Value == "auto" && Start != "auto" && End != "auto" ||
            !MainAxis && Parent is "horizontal" or "vertical" && Value == "auto" &&
            Style.GetValueOrDefault("align-self")?.Value == "stretch")
        {
            return "fill";
        }
        return Value == "auto" ? "content" : Value.EndsWith('%') ? "percent" : "fixed";
    }

    internal string LayoutStyleValue(XElement Element, string Name)
    {
        var Style = Sheet.Compute(Element);
        var Direct = Style.GetValueOrDefault(Name)?.Value;
        if (Direct is not null)
        {
            return Direct;
        }
        var Separator = Name.IndexOf('-');
        var Group = Separator > 0 ? Name[..Separator] : Name;
        if (Group is not ("margin" or "padding"))
        {
            return "auto";
        }
        var Parts = (Style.GetValueOrDefault(Group)?.Value ?? "0px").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var Index = Name[(Separator + 1)..] switch
        {
            "top" => 0,
            "right" => Parts.Length > 1 ? 1 : 0,
            "bottom" => Parts.Length > 2 ? 2 : 0,
            _ => Parts.Length > 3 ? 3 : Parts.Length > 1 ? 1 : 0,
        };
        return Parts.Length > Index ? Parts[Index] : "0px";
    }

    internal bool SetContainerLayout(string Mode, IReadOnlyDictionary<string, UiLayoutMetrics> Measurements, out string Message)
    {
        var Element = SelectedElement();
        Message = string.Empty;
        if (Element is null || !ContainerTags.Contains(Element.Name.LocalName) || Mode is not ("free" or "horizontal" or "vertical" or "overlay"))
        {
            Message = "Select a container and a supported layout";
            return false;
        }
        if (Element.Elements().Any(HasCustomTransform))
        {
            Message = "A child uses a custom transform; edit it explicitly before converting the container layout";
            return false;
        }
        return Edit("container layout:" + Mode, () =>
        {
            Element.SetAttributeValue("data-ui-layout", Mode);
            WriteDeclaration(Element, "box-sizing", "border-box");
            WriteDeclaration(Element, "display", Mode is "horizontal" or "vertical" ? "flex" : "block");
            WriteDeclaration(Element, "flex-direction", Mode == "horizontal" ? "row" : Mode == "vertical" ? "column" : null);
            var Position = Sheet.Compute(Element).GetValueOrDefault("position")?.Value;
            if (Position is null or "static")
            {
                WriteDeclaration(Element, "position", "relative");
            }
            foreach (var Child in Element.Elements())
            {
                var Measured = Measurements.GetValueOrDefault(KeyOf(Child));
                ConfigureLayoutChild(Child, Mode, Measured);
            }
        }, out Message);
    }

    private static void ConfigureLayoutChild(XElement Child, string Mode, UiLayoutMetrics? Measured = null)
    {
        foreach (var Property in new[] { "left", "right", "top", "bottom", "transform" })
        {
            WriteDeclaration(Child, Property, null);
        }
        Child.SetAttributeValue("data-ui-align-x", null);
        Child.SetAttributeValue("data-ui-align-y", null);
        if (Mode is "free" or "overlay")
        {
            WriteDeclaration(Child, "position", "absolute");
            WriteDeclaration(Child, "left", UiLayoutLength.Format(Mode == "free" ? Measured?.X ?? 0 : 0));
            WriteDeclaration(Child, "top", UiLayoutLength.Format(Mode == "free" ? Measured?.Y ?? 0 : 0));
            if (Mode == "free" && Measured is not null)
            {
                WriteDeclaration(Child, "box-sizing", "border-box");
                WriteDeclaration(Child, "width", UiLayoutLength.Format(Measured.Width));
                WriteDeclaration(Child, "height", UiLayoutLength.Format(Measured.Height));
            }
        }
        else
        {
            WriteDeclaration(Child, "position", "relative");
        }
    }

    internal bool SetAxisMode(bool Horizontal, string Mode, UiLayoutMetrics Metrics, out string Message)
    {
        var Element = SelectedElement();
        Message = string.Empty;
        if (Element is null || !HasLayoutBox(Element) || Mode is not ("fixed" or "content" or "fill" or "percent"))
        {
            Message = "Select a control and a supported sizing mode";
            return false;
        }
        var ParentMode = SlotLayoutOf(Element);
        if (Mode == "fill" && HasCustomTransform(Element))
        {
            Message = "Custom transforms must be edited explicitly before adopting Fill";
            return false;
        }
        if (Mode == "fill" && ParentMode == "custom")
        {
            Message = "Adopt a supported parent layout before using Fill";
            return false;
        }
        var Axis = Horizontal ? "width" : "height";
        var Main = Horizontal ? ParentMode == "horizontal" : ParentMode == "vertical";
        var Extent = Horizontal ? Metrics.Width : Metrics.Height;
        var ParentExtent = Horizontal ? Metrics.ParentWidth : Metrics.ParentHeight;
        var PreviousMode = AxisMode(Element, Horizontal);
        return Edit("size " + Axis + ":" + Mode, () =>
        {
            if (Main)
            {
                WriteDeclaration(Element, "flex-grow", Mode == "fill" ? "1" : "0");
                WriteDeclaration(Element, "flex-shrink", Mode == "fill" ? "1" : "0");
                WriteDeclaration(Element, "flex-basis", Mode == "fill" ? "0px" : "auto");
            }
            else if (ParentMode is "horizontal" or "vertical")
            {
                WriteDeclaration(Element, "align-self", Mode == "fill" ? "stretch" : "flex-start");
            }
            else if (ParentMode is "free" or "overlay")
            {
                WriteDeclaration(Element, "position", "absolute");
                if (Mode == "fill")
                {
                    WriteDeclaration(Element, Horizontal ? "left" : "top", "0px");
                    WriteDeclaration(Element, Horizontal ? "right" : "bottom", "0px");
                    Element.SetAttributeValue(Horizontal ? "data-ui-align-x" : "data-ui-align-y", "start");
                    WriteAlignmentTransform(Element);
                }
                else if (PreviousMode == "fill")
                {
                    WriteDeclaration(Element, Horizontal ? "right" : "bottom", "auto");
                }
            }
            WriteDeclaration(Element, "box-sizing", "border-box");
            WriteDeclaration(Element, Axis, Mode switch
            {
                "fixed" => UiLayoutLength.Format(Extent),
                "percent" => UiLayoutLength.Format(100 * Extent / Math.Max(1, ParentExtent), "%"),
                _ => "auto",
            });
        }, out Message);
    }

    internal bool SetLayoutAlignment(bool Horizontal, string Alignment, out string Message)
    {
        var Element = SelectedElement();
        Message = string.Empty;
        if (Element is null || !HasLayoutBox(Element) || Alignment is not ("start" or "center" or "end"))
        {
            Message = "Choose start, center or end alignment";
            return false;
        }
        var Parent = SlotLayoutOf(Element);
        if (HasCustomTransform(Element))
        {
            Message = "Custom transforms must be edited explicitly before adopting alignment";
            return false;
        }
        var Cross = Horizontal ? Parent == "vertical" : Parent == "horizontal";
        if (Parent == "custom" || Parent is "horizontal" or "vertical" && !Cross)
        {
            Message = "Main-axis alignment is controlled by the parent; use its Justify setting";
            return false;
        }
        return Edit("alignment", () =>
        {
            Element.SetAttributeValue(Horizontal ? "data-ui-align-x" : "data-ui-align-y", Alignment);
            if (Cross)
            {
                WriteDeclaration(Element, "align-self", Alignment == "center" ? "center" : Alignment == "end" ? "flex-end" : "flex-start");
                return;
            }
            WriteDeclaration(Element, "position", "absolute");
            WriteDeclaration(Element, Horizontal ? "left" : "top", Alignment == "end" ? "auto" : Alignment == "center" ? "50%" : "0px");
            WriteDeclaration(Element, Horizontal ? "right" : "bottom", Alignment == "end" ? "0px" : "auto");
            WriteAlignmentTransform(Element);
        }, out Message);
    }

    private static void WriteAlignmentTransform(XElement Element)
    {
        var X = AttributeOf(Element, "data-ui-align-x");
        var Y = AttributeOf(Element, "data-ui-align-y");
        WriteDeclaration(Element, "transform", X == "center" || Y == "center"
            ? "translate(" + (X == "center" ? "-50%" : "0px") + ", " + (Y == "center" ? "-50%" : "0px") + ")" : "none");
    }
}
