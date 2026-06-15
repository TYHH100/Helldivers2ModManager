using System.Windows;
using System.Windows.Controls;

namespace Helldivers2ModManager.Views
{
	internal partial class SettingsPageView : Page
	{
        private bool _isUpdatingPassword = false;

		public SettingsPageView()
		{
			InitializeComponent();
            DataContextChanged += SettingsPageView_DataContextChanged;
		}

        private void SettingsPageView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (DataContext is ViewModels.SettingsPageViewModel vm && vm.NexusApiKey != null)
            {
                _isUpdatingPassword = true;
                //NexusApiKeyPasswordBox.Password = vm.NexusApiKey;
                _isUpdatingPassword = false;
            }
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

        private void TextBox_TextChanged_1(object sender, TextChangedEventArgs e)
        {

        }

        //private void NexusApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        //{
        //    if (_isUpdatingPassword)
        //        return;

        //    if (DataContext is ViewModels.SettingsPageViewModel vm)
        //    {
        //        vm.NexusApiKey = NexusApiKeyPasswordBox.Password;
        //    }
        //}
    }
}
