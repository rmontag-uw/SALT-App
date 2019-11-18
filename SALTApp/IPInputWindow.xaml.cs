using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace SaltApp
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class IPInputWindow : Window
    {
        bool IPsGiven;
        public IPInputWindow()
        {
            InitializeComponent();
        }

        private void HandleIPInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text);
        }


        private static readonly Regex IPRegex = new Regex("[^0-9.-]+");
        private static bool IsTextAllowed(string text)
        {
            return !IPRegex.IsMatch(text);
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            IPsGiven = true;
            Close();  // close the window
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!IPsGiven)
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();  // a bit brutal but it works
            }
        }       
    }
}
