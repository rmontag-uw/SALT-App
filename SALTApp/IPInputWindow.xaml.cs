/* IP Input Window
 * S.A.L.T Project Application
 * Written by Maurice Montag, 2019
 * Developed as a collaboration between GRIDLab and BioRobotics Lab, University of Washington, Seattle
 * Copyright 2019 University of Washington
 * See included LICENSE.TXT for license information
 */

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace SALTApp
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class IPInputWindow : Window
    {
        private bool IPsGiven;
        public IPInputWindow()
        {
            InitializeComponent();
        }

        private void HandleIPInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text);
        }


        private static readonly Regex IPRegex = new Regex("[^0-9.]+");
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
