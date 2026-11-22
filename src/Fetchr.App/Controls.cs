using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Fetchr.Core;

namespace Fetchr.App;

/// <summary>
/// Right-to-left only for text that contains Arabic. Engine messages are English until they are
/// localised (M3), and laid out RTL their punctuation lands on the wrong side.
/// </summary>
public sealed class TextDirectionConverter : System.Windows.Data.IValueConverter
{
    public static FlowDirection Of(string? text) =>
        text is not null && text.Any(c => c is >= '؀' and <= 'ۿ') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => Of(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Draws a Lucide stroke icon (24×24 grid) in the inherited foreground colour.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public IconView()
    {
        Width = Height = 16;
        SnapsToDevicePixels = true;
        // Icons keep their drawn direction in Arabic (play, download, archive mean the same either way).
        // A directional icon such as a "next" chevron would set FlowDirection back to inherit.
        FlowDirection = FlowDirection.LeftToRight;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}

/// <summary>
/// One bar showing what each connection has fetched. Segments are separated by a 1px gap so
/// they read without relying on colour; the facts are also in text next to it.
/// </summary>
public sealed class SegmentMap : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentView>), typeof(SegmentMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(SegmentMap), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentView>? Segments { get => (IReadOnlyList<SegmentView>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public long Total { get => (long)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Res("Brush.Track"), null, new Rect(0, 0, w, h));
        if (Segments is not { Count: > 0 } segs || Total <= 0) return;

        // In RTL, WPF mirrors the element for us, so the map fills from the right with no extra code.
        string[] colours = ["Brush.Seg1", "Brush.Seg2", "Brush.Seg3", "Brush.Seg4"];
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var x0 = s.Start * w / Total;
            var x1 = (Math.Min(s.Current, s.End + 1)) * w / Total;
            if (x1 > x0) dc.DrawRectangle(Res(colours[i % colours.Length]), null, new Rect(x0, 0, x1 - x0, h));
            if (i > 0) dc.DrawRectangle(Res("Brush.Surface"), null, new Rect(x0 - 0.5, 0, 1, h));
        }
    }

    private Brush Res(string key) => (TryFindResource(key) as Brush) ?? Brushes.Gray;
}
