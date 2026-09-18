using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Helldivers2ModManager.Views;

public partial class FloatingMusicPlayer : UserControl
{
	private bool _pointerDown;
	private bool _isDragging;
	private Point _pointerStart;
	private Point _controlStart;
	private UIElement? _capturedSurface;
	private ViewModels.FloatingMusicPlayerViewModel? _observedViewModel;
	private int _animationVersion;

	public FloatingMusicPlayer()
	{
		InitializeComponent();
		DataContextChanged += FloatingMusicPlayer_DataContextChanged;
		Unloaded += FloatingMusicPlayer_Unloaded;
	}

	private void FloatingMusicPlayer_Loaded(object sender, RoutedEventArgs e)
	{
		ObserveViewModel(DataContext as ViewModels.FloatingMusicPlayerViewModel);
		ApplyExpansionState(_observedViewModel?.IsExpanded == true, animate: false);
		Dispatcher.BeginInvoke(ApplySavedPosition);
	}

	private void FloatingMusicPlayer_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
		ObserveViewModel(e.NewValue as ViewModels.FloatingMusicPlayerViewModel);

	private void FloatingMusicPlayer_Unloaded(object sender, RoutedEventArgs e) => ObserveViewModel(null);

	private void ObserveViewModel(ViewModels.FloatingMusicPlayerViewModel? viewModel)
	{
		if (ReferenceEquals(_observedViewModel, viewModel))
			return;
		if (_observedViewModel is not null)
			_observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		_observedViewModel = viewModel;
		if (_observedViewModel is not null)
			_observedViewModel.PropertyChanged += ViewModel_PropertyChanged;
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ViewModels.FloatingMusicPlayerViewModel.IsExpanded)
			&& sender is ViewModels.FloatingMusicPlayerViewModel vm)
			ApplyExpansionState(vm.IsExpanded, animate: true);
	}

	private void ApplyExpansionState(bool expanded, bool animate)
	{
		var version = ++_animationVersion;
		if (!animate || !SystemParameters.ClientAreaAnimation)
		{
			SetExpansionState(expanded);
			return;
		}

		var duration = TimeSpan.FromMilliseconds(180);
		var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
		var shown = expanded ? ExpandedPlayer : CollapsedPlayer;
		var hidden = expanded ? CollapsedPlayer : ExpandedPlayer;
		var shownScale = (ScaleTransform)shown.RenderTransform;
		var hiddenScale = (ScaleTransform)hidden.RenderTransform;

		shown.Visibility = Visibility.Visible;
		shown.IsHitTestVisible = true;
		hidden.IsHitTestVisible = false;
		shown.Opacity = 0;
		shownScale.ScaleX = shownScale.ScaleY = 0.94;

		Animate(shown, UIElement.OpacityProperty, 0, 1, duration, easing);
		Animate(shownScale, ScaleTransform.ScaleXProperty, 0.94, 1, duration, easing);
		Animate(shownScale, ScaleTransform.ScaleYProperty, 0.94, 1, duration, easing);
		Animate(
			hidden,
			UIElement.OpacityProperty,
			hidden.Opacity,
			0,
			duration,
			easing,
			(_, _) =>
			{
				if (version != _animationVersion)
					return;
				hidden.Visibility = Visibility.Collapsed;
				hidden.Opacity = 0;
			});
		Animate(hiddenScale, ScaleTransform.ScaleXProperty, hiddenScale.ScaleX, 0.94, duration, easing);
		Animate(hiddenScale, ScaleTransform.ScaleYProperty, hiddenScale.ScaleY, 0.94, duration, easing);
	}

	private void SetExpansionState(bool expanded)
	{
		var shown = expanded ? ExpandedPlayer : CollapsedPlayer;
		var hidden = expanded ? CollapsedPlayer : ExpandedPlayer;
		shown.Visibility = Visibility.Visible;
		shown.IsHitTestVisible = true;
		shown.Opacity = 1;
		((ScaleTransform)shown.RenderTransform).ScaleX = ((ScaleTransform)shown.RenderTransform).ScaleY = 1;
		hidden.Visibility = Visibility.Collapsed;
		hidden.IsHitTestVisible = false;
		hidden.Opacity = 0;
	}

	private static DoubleAnimation Animate(
		UIElement target,
		DependencyProperty property,
		double from,
		double to,
		TimeSpan duration,
		IEasingFunction easing,
		EventHandler? completed = null)
	{
		var animation = new DoubleAnimation(from, to, duration)
		{
			EasingFunction = easing,
			FillBehavior = FillBehavior.Stop,
		};
		if (completed is not null)
			animation.Completed += completed;
		target.SetValue(property, to);
		target.BeginAnimation(property, animation);
		return animation;
	}

	private static DoubleAnimation Animate(
		Animatable target,
		DependencyProperty property,
		double from,
		double to,
		TimeSpan duration,
		IEasingFunction easing,
		EventHandler? completed = null)
	{
		var animation = new DoubleAnimation(from, to, duration)
		{
			EasingFunction = easing,
			FillBehavior = FillBehavior.Stop,
		};
		if (completed is not null)
			animation.Completed += completed;
		target.SetValue(property, to);
		target.BeginAnimation(property, animation);
		return animation;
	}

	private void FloatingMusicPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (!_isDragging && IsLoaded)
			Dispatcher.BeginInvoke(ApplySavedPosition);
	}

	private void DragSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (Parent is not Canvas canvas || sender is not UIElement surface)
			return;

		_pointerDown = true;
		_isDragging = false;
		_pointerStart = e.GetPosition(canvas);
		_controlStart = GetCurrentPosition(canvas);
		_capturedSurface = surface;
		surface.CaptureMouse();
		e.Handled = true;
	}

	private void DragSurface_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed || Parent is not Canvas canvas)
			return;

		var current = e.GetPosition(canvas);
		var offset = current - _pointerStart;
		if (!_isDragging && Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
			&& Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
			return;

		_isDragging = true;
		var maxX = Math.Max(0, canvas.ActualWidth - ActualWidth);
		var maxY = Math.Max(0, canvas.ActualHeight - ActualHeight);
		Canvas.SetLeft(this, Math.Clamp(_controlStart.X + offset.X, 0, maxX));
		Canvas.SetTop(this, Math.Clamp(_controlStart.Y + offset.Y, 0, maxY));
		e.Handled = true;
	}

	private async void DragSurface_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!_pointerDown)
			return;

		_pointerDown = false;
		_capturedSurface?.ReleaseMouseCapture();
		_capturedSurface = null;

		if (_isDragging)
		{
			_isDragging = false;
			if (Parent is Canvas canvas && DataContext is ViewModels.FloatingMusicPlayerViewModel vm)
			{
				var maxX = Math.Max(0, canvas.ActualWidth - ActualWidth);
				var maxY = Math.Max(0, canvas.ActualHeight - ActualHeight);
				var horizontal = maxX > 0 ? Canvas.GetLeft(this) / maxX : 1;
				var vertical = maxY > 0 ? Canvas.GetTop(this) / maxY : 1;
				try
				{
					await vm.SavePositionAsync(horizontal, vertical);
				}
				catch
				{
					// 位置保存失败不应中断播放器交互；下次拖动会再次尝试。
				}
			}
		}
		else if (DataContext is ViewModels.FloatingMusicPlayerViewModel { IsExpanded: false } collapsedVm)
		{
			collapsedVm.ExpandCommand.Execute(null);
		}

		e.Handled = true;
	}

	private Point GetCurrentPosition(Canvas canvas)
	{
		var left = Canvas.GetLeft(this);
		var top = Canvas.GetTop(this);
		if (double.IsNaN(left) || double.IsNaN(top))
		{
			ApplySavedPosition();
			left = Canvas.GetLeft(this);
			top = Canvas.GetTop(this);
		}
		return new Point(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
	}

	private void ApplySavedPosition()
	{
		if (Parent is not Canvas canvas || DataContext is not ViewModels.FloatingMusicPlayerViewModel vm)
			return;
		var maxX = Math.Max(0, canvas.ActualWidth - ActualWidth);
		var maxY = Math.Max(0, canvas.ActualHeight - ActualHeight);
		Canvas.SetLeft(this, vm.SavedHorizontalPosition * maxX);
		Canvas.SetTop(this, vm.SavedVerticalPosition * maxY);
	}

	private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (DataContext is ViewModels.FloatingMusicPlayerViewModel vm)
			vm.IsSeeking = true;
	}

	private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (DataContext is ViewModels.FloatingMusicPlayerViewModel vm)
			vm.IsSeeking = false;
	}
}
