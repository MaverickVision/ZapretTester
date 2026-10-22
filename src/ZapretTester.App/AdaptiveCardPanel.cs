using System.Windows;
using System.Windows.Controls;

namespace ZapretTester.App;

/// <summary>Fits as many complete cards as possible, then shares the remaining row width equally.</summary>
public sealed class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(
        nameof(MinimumCardWidth), typeof(double), typeof(AdaptiveCardPanel), new FrameworkPropertyMetadata(388d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public int ColumnCount { get; private set; } = 1;
    private double rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var minimum = Math.Max(1, MinimumCardWidth) + 14; // Card template includes a 14 DIP gap.
        var width = double.IsInfinity(availableSize.Width) ? minimum : availableSize.Width;
        ColumnCount = Math.Max(1, (int)(width / minimum));
        var cellWidth = width / ColumnCount;
        rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return new Size(width, Math.Ceiling((double)InternalChildren.Count / ColumnCount) * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = finalSize.Width / ColumnCount;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i % ColumnCount * cellWidth, i / ColumnCount * rowHeight, cellWidth, rowHeight));
        return finalSize;
    }
}
