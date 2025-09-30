using System.Windows;
using System.Windows.Input;

namespace Paimon
{
    public partial class AdditionalPromptWindow : Window
    {
        public string? ExtraText { get; private set; }

        public AdditionalPromptWindow()
        {
            InitializeComponent();
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            ExtraText = ExtraBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DragMoveWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}

