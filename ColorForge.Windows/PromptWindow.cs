using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ColorForge.Windows;

public sealed class PromptWindow : Window
{
	private readonly TextBox _input;

	public string Answer => _input.Text;

	public PromptWindow(string title, string label, string initial)
	{
		base.Title = title;
		base.Width = 430.0;
		base.Height = 190.0;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		base.Background = new SolidColorBrush(Color.FromRgb(20, 15, 27));
		base.Foreground = Brushes.White;
		base.AllowsTransparency = true;
		Border border = new Border
		{
			CornerRadius = new CornerRadius(16.0),
			BorderBrush = new SolidColorBrush(Color.FromRgb(93, 60, 112)),
			BorderThickness = new Thickness(1.0),
			Background = base.Background,
			Padding = new Thickness(24.0)
		};
		StackPanel stackPanel = new StackPanel
		{
			Children = { (UIElement)new TextBlock
			{
				Text = label,
				FontSize = 13.0,
				Foreground = new SolidColorBrush(Color.FromRgb(190, 177, 199)),
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			} }
		};
		_input = new TextBox
		{
			Text = initial,
			FontSize = 14.0,
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0)
		};
		stackPanel.Children.Add(_input);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
		};
		Button element = new Button
		{
			Content = "Cancel",
			Width = 96.0,
			Height = 36.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			IsCancel = true
		};
		Button button = new Button
		{
			Content = "Save",
			Width = 110.0,
			Height = 36.0,
			IsDefault = true
		};
		button.Click += delegate
		{
			base.DialogResult = true;
			Close();
		};
		stackPanel2.Children.Add(element);
		stackPanel2.Children.Add(button);
		stackPanel.Children.Add(stackPanel2);
		border.Child = stackPanel;
		base.Content = border;
		base.Loaded += delegate
		{
			_input.SelectAll();
			_input.Focus();
		};
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (e.ButtonState == MouseButtonState.Pressed)
			{
				DragMove();
			}
		};
	}
}
