using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Duplify.Models;
using Duplify.ViewModels;

namespace Duplify;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FilesListBox.MouseDoubleClick += OnFilesListBoxMouseDoubleClick;
    }

    private void OnFilesListBoxMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != FilesListBox)
        {
            // Do not trigger open if user was clicking a checkbox or button
            if (source is CheckBox || source is Button)
            {
                return;
            }

            if (source is ListBoxItem item && item.DataContext is DuplicateFileItem fileItem)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenInExplorerCommand.Execute(fileItem);
                }
                e.Handled = true;
                return;
            }

            source = VisualTreeHelper.GetParent(source);
        }
    }


    private void OnFileItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DuplicateFileItem fileItem)
        {
            RequestThumbnailLoad(fileItem);
        }
    }

    private void OnFileItemDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is DuplicateFileItem fileItem)
        {
            RequestThumbnailLoad(fileItem);
        }
    }

    private void RequestThumbnailLoad(DuplicateFileItem fileItem)
    {
        if (DataContext is MainViewModel vm && fileItem.IsImage && fileItem.Thumbnail == null)
        {
            vm.RequestThumbnailLoad(fileItem);
        }
    }
}
