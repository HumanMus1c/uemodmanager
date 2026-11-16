using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UEManager.UI.ViewModels;

namespace UEManager.UI.Views;

public partial class ModsView : UserControl
{
    /// <summary>The room the mod's name keeps in the table; below it, the table drops what it can do without.</summary>
    private const double MinNameWidth = 200;

    /// <summary>The row's padding on both sides and the card's border, around the columns.</summary>
    private const double TableFrame = 2 * 22 + 2;

    /// <summary>
    /// The narrow table, as in a window snapped to half a 1080p screen: the switch without its words, the integrity
    /// icon without its word, and no install date (the mod's details show it).
    /// </summary>
    private static readonly Dictionary<string, object> Narrow = new()
    {
        ["Col.Status"] = new GridLength(96),
        ["Col.Integrity"] = new GridLength(36),
        ["Col.Date"] = new GridLength(0),
        ["Mods.WideOnly"] = Visibility.Collapsed,
    };

    private readonly Dictionary<string, object> wide;
    private readonly double wideColumns;
    private bool isNarrow;
    private MainViewModel? subscribedModel;

    public ModsView()
    {
        InitializeComponent();
        wide = Narrow.Keys.ToDictionary(key => key, key => Resources[key]);
        wideColumns = new[] { "Col.Status", "Col.Type", "Col.Integrity", "Col.Date", "Col.Actions" }
            .Sum(key => ((GridLength)Resources[key]).Value);
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (subscribedModel is not null)
            subscribedModel.ScrollToModRequested -= OnScrollToModRequested;
        subscribedModel = e.NewValue as MainViewModel;
        if (subscribedModel is not null)
            subscribedModel.ScrollToModRequested += OnScrollToModRequested;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (subscribedModel is not null)
            subscribedModel.ScrollToModRequested -= OnScrollToModRequested;
    }

    private void OnScrollToModRequested(ModItemViewModel target) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToMod(target));

    private void ScrollToMod(ModItemViewModel target)
    {
        UpdateLayout();
        var row = FindRowElement(target);
        if (row is null)
            return;

        if (ModsScroll is { } scroll && row.ActualHeight > 0)
        {
            var topInViewport = row.TranslatePoint(new Point(0, 0), scroll).Y;
            var desiredOffset = scroll.VerticalOffset + topInViewport - Math.Max(0, (scroll.ViewportHeight - row.ActualHeight) * 0.35);
            scroll.ScrollToVerticalOffset(Math.Clamp(desiredOffset, 0, scroll.ScrollableHeight));
        }
        else
        {
            row.BringIntoView();
        }
    }

    private FrameworkElement? FindRowElement(ModItemViewModel target)
    {
        if (ModsList.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement container)
            return container;
        return FindVisualChild<FrameworkElement>(this, element => ReferenceEquals(element.DataContext, target) && element.Name == "Row");
    }

    private static T? FindVisualChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && predicate(typed))
                return typed;
            if (FindVisualChild(child, predicate) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>Ctrl+F: the search box, with its text selected to type over.</summary>
    internal void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnTableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width - TableFrame - wideColumns < MinNameWidth;
        if (narrow == isNarrow)
            return;
        isNarrow = narrow;
        // The header and every row take their widths from these resources, so all of them follow at once.
        foreach (var (key, value) in narrow ? Narrow : wide)
            Resources[key] = value;
    }
}
