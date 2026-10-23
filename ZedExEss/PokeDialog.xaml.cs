using System.Windows;

namespace ZedExEss
{
    /// <summary>Collects one or more address/value poke specifications for the main window.</summary>
    public partial class PokeDialog : Window
    {
        public PokeDialog()
        {
            InitializeComponent();
            PokeInput.Focus();
        }

        public string PokeText => PokeInput.Text;
        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
