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

        public IPInputWindow()
        {
            InitializeComponent();
        }

        private void HandleIPInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text);
        }


        private static readonly Regex IPRegex = new Regex(@"^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$");

        private static bool IsTextAllowed(string text)
        {
            return !IPRegex.IsMatch(text);
        }

    }
}
