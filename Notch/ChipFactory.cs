using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Slate.Clipboard;
using Slate.Shelf;

namespace Slate.Notch;

/// <summary>
/// Builds the shelf and clipboard chips in code rather than as data templates.
/// </summary>
/// <remarks>
/// A binding that silently fails renders nothing and says nothing, which is a poor
/// trade in a project written without a machine to run it on. Everything here is
/// explicit and steps through in a debugger.
/// </remarks>
internal static class ChipFactory
{
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Bright = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    public static UIElement Shelf(ShelfItem item, ShelfStore store)
    {
        var icon = new Image
        {
            Source = item.Icon,
            Width = 42,
            Height = 42,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 9,
            Foreground = Bright,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 26,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        };

        var size = new TextBlock
        {
            Text = item.SizeLabel,
            FontSize = 8,
            Foreground = Faint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var stack = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
        stack.Children.Add(icon);
        stack.Children.Add(name);
        stack.Children.Add(size);

        var chip = Shell(stack, 88, 104);
        chip.ToolTip = item.Path;

        chip.MouseLeftButtonUp += (_, e) =>
        {
            if (e.ClickCount == 2) store.Open(item);
            e.Handled = true;
        };

        // Drag straight back out to Explorer or any app that takes files.
        Point origin = default;
        chip.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(chip);
        chip.PreviewMouseMove += (s, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(chip);
            if (Math.Abs(now.X - origin.X) < 6 && Math.Abs(now.Y - origin.Y) < 6) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop((DependencyObject)s, data, DragDropEffects.Copy);
        };

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", () => store.Open(item)));
        menu.Items.Add(Item("Reveal in Explorer", () => store.RevealInExplorer(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from Shelf", () => store.Remove(item)));
        chip.ContextMenu = menu;

        return chip;
    }

    public static UIElement Clip(ClipItem item, ClipboardStore store, Color accent)
    {
        var body = new Grid();

        if (item.Image is not null)
        {
            body.Children.Add(new Image
            {
                Source = item.Image,
                Height = 58,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = item.Preview,
                FontSize = 10,
                Foreground = Bright,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 58
            });
        }

        var footer = new TextBlock
        {
            Text = string.IsNullOrEmpty(item.SourceName)
                ? item.SizeLabel
                : $"{item.SourceName} · {item.SizeLabel}",
            FontSize = 8,
            Foreground = Faint,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var stack = new StackPanel { Margin = new Thickness(9) };
        stack.Children.Add(body);
        stack.Children.Add(new Border { Height = 6 });
        stack.Children.Add(footer);

        var chip = Shell(stack, 132, 104);
        chip.ToolTip = item.Text ?? "Image";

        var copied = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x47, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(11),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Copied",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ((Grid)chip.Child!).Children.Add(copied);

        chip.MouseLeftButtonUp += async (_, e) =>
        {
            store.CopyBack(item);
            copied.Visibility = Visibility.Visible;
            e.Handled = true;
            await Task.Delay(900);
            copied.Visibility = Visibility.Collapsed;
        };

        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy", () => store.CopyBack(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () => store.Remove(item)));
        chip.ContextMenu = menu;

        return chip;
    }

    /// <summary>The common chip body: a rounded panel with a hover lift.</summary>
    private static Border Shell(UIElement content, double width, double height)
    {
        var host = new Grid();
        host.Children.Add(content);

        var chip = new Border
        {
            Width = width,
            Height = height,
            Margin = new Thickness(0, 0, 10, 0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Child = host
        };

        chip.MouseEnter += (_, _) =>
            chip.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        chip.MouseLeave += (_, _) =>
            chip.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));

        return chip;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
