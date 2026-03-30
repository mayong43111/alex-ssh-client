using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SSHClient.App.Controls;

/// <summary>
/// 轻量级折线图控件。绑定两个 ObservableCollection&lt;double&gt; 作为上/下行数据点。
/// 不依赖任何第三方图表库。
/// </summary>
public sealed class SparklineChart : Control
{
    static SparklineChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SparklineChart),
            new FrameworkPropertyMetadata(typeof(SparklineChart)));
    }

    // ── 依赖属性 ──────────────────────────────────────────────────

    public static readonly DependencyProperty UpSeriesProperty =
        DependencyProperty.Register(nameof(UpSeries), typeof(ObservableCollection<double>), typeof(SparklineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty DownSeriesProperty =
        DependencyProperty.Register(nameof(DownSeries), typeof(ObservableCollection<double>), typeof(SparklineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty UpColorProperty =
        DependencyProperty.Register(nameof(UpColor), typeof(Color), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Color.FromRgb(0x22, 0xC5, 0x5E), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownColorProperty =
        DependencyProperty.Register(nameof(DownColor), typeof(Color), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Color.FromRgb(0x3B, 0x82, 0xF6), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridLineCountProperty =
        DependencyProperty.Register(nameof(GridLineCount), typeof(int), typeof(SparklineChart),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? UpSeries
    {
        get => (ObservableCollection<double>?)GetValue(UpSeriesProperty);
        set => SetValue(UpSeriesProperty, value);
    }

    public ObservableCollection<double>? DownSeries
    {
        get => (ObservableCollection<double>?)GetValue(DownSeriesProperty);
        set => SetValue(DownSeriesProperty, value);
    }

    public Color UpColor
    {
        get => (Color)GetValue(UpColorProperty);
        set => SetValue(UpColorProperty, value);
    }

    public Color DownColor
    {
        get => (Color)GetValue(DownColorProperty);
        set => SetValue(DownColorProperty, value);
    }

    public int GridLineCount
    {
        get => (int)GetValue(GridLineCountProperty);
        set => SetValue(GridLineCountProperty, value);
    }

    // ── 订阅集合变更 ──────────────────────────────────────────────

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (SparklineChart)d;
        if (e.OldValue is ObservableCollection<double> oldCol)
            oldCol.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<double> newCol)
            newCol.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    // ── 渲染 ──────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;

        var upPts = UpSeries?.ToArray() ?? Array.Empty<double>();
        var downPts = DownSeries?.ToArray() ?? Array.Empty<double>();

        // 背景
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), null, new Rect(0, 0, w, h));

        double max = 1024; // 最低刻度 1 KB/s，避免除零
        foreach (var v in upPts) if (v > max) max = v;
        foreach (var v in downPts) if (v > max) max = v;
        max *= 1.2; // 留顶部余量

        // 网格线
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x94, 0xA3, 0xB8)), 0.5);
        int gridLines = Math.Max(1, GridLineCount);
        for (int i = 1; i <= gridLines; i++)
        {
            var y = h - h * i / (gridLines + 1);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        // 绘制一条折线
        DrawLine(dc, upPts, w, h, max, UpColor);
        DrawLine(dc, downPts, w, h, max, DownColor);

        // 边框
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD2, 0xD8, 0xE2)), 1);
        dc.DrawRectangle(null, borderPen, new Rect(0.5, 0.5, w - 1, h - 1));
    }

    private static void DrawLine(DrawingContext dc, double[] pts, double w, double h, double max, Color color)
    {
        if (pts.Length < 2) return;

        var brush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        var pen = new Pen(new SolidColorBrush(color), 1.5) { LineJoin = PenLineJoin.Round };

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double xStep = w / (pts.Length - 1);
            var firstY = h - h * (pts[0] / max);
            ctx.BeginFigure(new Point(0, firstY), isFilled: true, isClosed: false);

            for (int i = 1; i < pts.Length; i++)
            {
                var x = xStep * i;
                var y = h - h * (pts[i] / max);
                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }

            // 填充区域
            ctx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(0, h), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(0, firstY), isStroked: false, isSmoothJoin: false);
        }

        geo.Freeze();
        dc.DrawGeometry(brush, pen, geo);
    }
}
