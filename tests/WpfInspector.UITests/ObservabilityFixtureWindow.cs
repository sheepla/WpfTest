using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using WpfInspector.App.Controls;
using WpfInspector.App.Validation;
using WpfInspector.App.ViewModels;

namespace WpfInspector.UITests;

internal sealed class ObservabilityFixtureWindow : Window
{
    private readonly FixtureLabViewModel _viewModel = new();
    private readonly TextBox _validationFixtureTextBox;

    public ObservabilityFixtureWindow()
    {
        Title = "WpfInspector";
        Width = 1280;
        Height = 900;
        DataContext = _viewModel;

        var buttonTemplate = CreateSimpleButtonTemplate();

        var stack = new StackPanel
        {
            Margin = new Thickness(16)
        };

        var runAsyncButton = new Button
        {
            Name = "RunAsyncFixtureButton",
            Content = "非同期コマンド",
            Margin = new Thickness(0, 0, 0, 12),
            Width = 160,
            Template = buttonTemplate
        };
        runAsyncButton.SetBinding(Button.CommandProperty, new Binding(nameof(FixtureLabViewModel.RunAsyncCommand))
        {
            Mode = BindingMode.OneWay
        });
        stack.Children.Add(runAsyncButton);

        _validationFixtureTextBox = new TextBox
        {
            Name = "ValidationFixtureTextBox",
            Margin = new Thickness(0, 0, 0, 12),
            Width = 240,
            Template = CreateSimpleTextBoxTemplate()
        };
        var validationBinding = new Binding(nameof(FixtureLabViewModel.ValidatedToken))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            NotifyOnValidationError = true
        };
        validationBinding.ValidationRules.Add(new TokenFormatValidationRule());
        _validationFixtureTextBox.SetBinding(TextBox.TextProperty, validationBinding);

        var virtualizedList = new ListBox
        {
            Name = "VirtualizedItemsListBox",
            Height = 220,
            Margin = new Thickness(0, 0, 0, 12),
            Template = CreateSimpleListBoxTemplate()
        };
        ScrollViewer.SetCanContentScroll(virtualizedList, true);
        VirtualizingPanel.SetIsVirtualizing(virtualizedList, true);
        VirtualizingPanel.SetVirtualizationMode(virtualizedList, VirtualizationMode.Recycling);
        virtualizedList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FixtureLabViewModel.VirtualizedItems))
        {
            Mode = BindingMode.OneWay
        });
        virtualizedList.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(FixtureLabViewModel.SelectedVirtualizedItem))
        {
            Mode = BindingMode.TwoWay
        });
        stack.Children.Add(virtualizedList);

        var priorityBindingText = new TextBlock
        {
            Name = "PriorityBindingFixtureText",
            Margin = new Thickness(0, 0, 0, 12)
        };
        var priorityBinding = new PriorityBinding();
        priorityBinding.Bindings.Add(new Binding(nameof(FixtureLabViewModel.SlowPriorityValue))
        {
            IsAsync = true
        });
        priorityBinding.Bindings.Add(new Binding(nameof(FixtureLabViewModel.PriorityFallbackValue)));
        BindingOperations.SetBinding(priorityBindingText, TextBlock.TextProperty, priorityBinding);
        stack.Children.Add(priorityBindingText);
        stack.Children.Add(_validationFixtureTextBox);

        var contextMenuButton = new Button
        {
            Name = "ContextMenuFixtureButton",
            Content = "ContextMenu",
            Margin = new Thickness(0, 0, 0, 12),
            Width = 160,
            Template = buttonTemplate
        };
        contextMenuButton.SetBinding(Button.CommandProperty, new Binding(nameof(FixtureLabViewModel.ToggleContextMenuCommand))
        {
            Mode = BindingMode.OneWay
        });
        var contextMenu = new ContextMenu
        {
            DataContext = _viewModel
        };
        contextMenu.Items.Add(new MenuItem
        {
            Header = "Inspect visual"
        });
        contextMenu.Items.Add(new MenuItem
        {
            Header = "Snapshot bindings"
        });
        BindingOperations.SetBinding(contextMenu, ContextMenu.IsOpenProperty, new Binding(nameof(FixtureLabViewModel.IsContextMenuOpen))
        {
            Mode = BindingMode.TwoWay,
            Source = _viewModel
        });
        contextMenuButton.ContextMenu = contextMenu;
        stack.Children.Add(contextMenuButton);

        var popupAnchor = new Button
        {
            Name = "PopupAnchorButton",
            Content = "Popup anchor",
            Margin = new Thickness(0, 0, 0, 12),
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Template = buttonTemplate
        };
        stack.Children.Add(popupAnchor);

        var popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = popupAnchor,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = "Detached popup surface"
                }
            }
        };
        BindingOperations.SetBinding(popup, Popup.IsOpenProperty, new Binding(nameof(FixtureLabViewModel.IsPopupOpen))
        {
            Mode = BindingMode.TwoWay
        });
        stack.Children.Add(popup);

        var simulateDispatcherBusyButton = new Button
        {
            Name = "SimulateDispatcherBusyButton",
            Content = "UI スレッドをブロック",
            Margin = new Thickness(0, 0, 0, 12),
            Width = 180,
            Template = buttonTemplate
        };
        simulateDispatcherBusyButton.SetBinding(Button.CommandProperty, new Binding(nameof(FixtureLabViewModel.SimulateDispatcherBusyCommand))
        {
            Mode = BindingMode.OneWay
        });
        stack.Children.Add(simulateDispatcherBusyButton);

        var interopHost = new FixtureHwndHost
        {
            Name = "FixtureHwndHostSurface",
            Height = 76
        };
        interopHost.SetBinding(FixtureHwndHost.CaptionProperty, new Binding(nameof(FixtureLabViewModel.HwndHostCaption))
        {
            Mode = BindingMode.OneWay
        });
        stack.Children.Add(interopHost);

        Content = stack;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ResetState();
    }

    public void ResetState()
    {
        _viewModel.IsContextMenuOpen = false;
        _viewModel.IsPopupOpen = true;
        _viewModel.SelectedVirtualizedItem = _viewModel.VirtualizedItems.FirstOrDefault();
        _validationFixtureTextBox.Text = "bad";
        _validationFixtureTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static ControlTemplate CreateSimpleButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.SlateGray);
        border.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate CreateSimpleTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.SlateGray);
        border.SetValue(Border.PaddingProperty, new Thickness(4));

        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer));
        contentHost.Name = "PART_ContentHost";
        border.AppendChild(contentHost);
        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate CreateSimpleListBoxTemplate()
    {
        var template = new ControlTemplate(typeof(ListBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.SlateGray);

        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(itemsPresenter);
        template.VisualTree = border;
        return template;
    }
}
