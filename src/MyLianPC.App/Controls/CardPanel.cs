using System.Windows;
using System.Windows.Controls;

namespace MyLianPC.App.Controls;

/// <summary>
/// Arranges dashboard cards in rows that always fill the available width. Each row's cards share the width equally,
/// so a partly filled row stretches instead of leaving an empty block beside the last card, and the cards in a row
/// are given the same height so their bottoms line up.
/// </summary>
/// <remarks>
/// Without this, a <see cref="WrapPanel"/> leaves a hole whenever the number of cards is not a multiple of the
/// columns that fit; with the connection card on a row of its own that hole sat next to the storage card.
/// </remarks>
public sealed class CardPanel : Panel
{
    /// <summary>The narrowest a card may get before the panel uses one more row.</summary>
    public static readonly DependencyProperty MinCardWidthProperty = DependencyProperty.Register(
        nameof(MinCardWidth), typeof(double), typeof(CardPanel),
        new FrameworkPropertyMetadata(230.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The gap between cards, horizontally and vertically.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(CardPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinCardWidth
    {
        get => (double)GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        if (count == 0) return default;

        double width = double.IsInfinity(availableSize.Width)
            ? MinCardWidth * count + Spacing * (count - 1)
            : availableSize.Width;
        int perRow = PerRow(count, width);
        double cardWidth = CardWidth(width, perRow);

        double height = 0;
        for (int i = 0; i < count; i += perRow)
        {
            height += RowHeight(i, perRow, cardWidth, out _);
            if (i + perRow < count) height += Spacing;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        if (count == 0) return finalSize;

        int perRow = PerRow(count, finalSize.Width);
        double cardWidth = CardWidth(finalSize.Width, perRow);
        double y = 0;
        for (int i = 0; i < count; i += perRow)
        {
            double rowHeight = RowHeight(i, perRow, cardWidth, out int inRow);
            for (int j = 0; j < inRow; j++)
                InternalChildren[i + j].Arrange(new Rect(j * (cardWidth + Spacing), y, cardWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }

    /// <summary>How many cards go in one row: all of them when they fit, otherwise the most balanced split.</summary>
    private int PerRow(int count, double width)
    {
        int max = Math.Max(1, (int)((width + Spacing) / (MinCardWidth + Spacing)));
        if (count <= max) return count;
        // 5 cards with room for 4 read better as 3 + 2 than as 4 + 1.
        int rows = (int)Math.Ceiling(count / (double)max);
        return (int)Math.Ceiling(count / (double)rows);
    }

    private double CardWidth(double width, int perRow) => Math.Max(1, (width - Spacing * (perRow - 1)) / perRow);

    /// <summary>Measures one row and returns the tallest card in it, so the row can be aligned on one baseline.</summary>
    private double RowHeight(int first, int perRow, double cardWidth, out int inRow)
    {
        inRow = Math.Min(perRow, InternalChildren.Count - first);
        double height = 0;
        for (int j = 0; j < inRow; j++)
        {
            UIElement child = InternalChildren[first + j];
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return height;
    }
}
