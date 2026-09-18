using System.Windows;
using System.Windows.Controls;

namespace Helldivers2ModManager.Behaviors;

internal static class PasswordBoxBinding
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not PasswordBox passwordBox)
            return;

        passwordBox.PasswordChanged -= OnPasswordChanged;
        var value = args.NewValue as string ?? string.Empty;
        if (passwordBox.Password != value)
            passwordBox.Password = value;
        passwordBox.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        SetBoundPassword(passwordBox, passwordBox.Password);
    }
}
