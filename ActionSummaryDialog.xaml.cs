using System.Windows;

namespace Duplify;

public partial class ActionSummaryDialog : Window
{
    public bool OpenJournalRequested { get; private set; }

    public ActionSummaryDialog(string title, string message)
    {
        InitializeComponent();

        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
    }

    private void OnOpenJournalClick(object sender, RoutedEventArgs e)
    {
        OpenJournalRequested = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        OpenJournalRequested = false;
        Close();
    }
}
