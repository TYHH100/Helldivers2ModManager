using Helldivers2ModManager.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Helldivers2ModManager.Components;

internal partial class FirstRunTutorialOverlay : UserControl
{
	private MainViewModel? _viewModel;
	private bool _dataContextHooked;
	private const double TutorialMargin = 16;

	public FirstRunTutorialOverlay()
	{
		InitializeComponent();

		DataContextChanged += FirstRunTutorialOverlay_DataContextChanged;
		IsVisibleChanged += FirstRunTutorialOverlay_IsVisibleChanged;
		SizeChanged += FirstRunTutorialOverlay_SizeChanged;
	}

	private void FirstRunTutorialOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		ScheduleSpotlightUpdate();
	}

	private void FirstRunTutorialOverlay_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		UnhookDataContext();
		_viewModel = DataContext as MainViewModel;
		HookDataContext();
		ScheduleSpotlightUpdate();
	}

	private void FirstRunTutorialOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (IsVisible)
			ScheduleSpotlightUpdate();
	}

	private void HookDataContext()
	{
		if (_dataContextHooked || _viewModel is null)
			return;

		_viewModel.PropertyChanged += ViewModel_PropertyChanged;
		_dataContextHooked = true;
	}

	private void UnhookDataContext()
	{
		if (!_dataContextHooked || _viewModel is null)
			return;

		_viewModel.PropertyChanged -= ViewModel_PropertyChanged;
		_dataContextHooked = false;
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(MainViewModel.FirstRunTutorialStep) ||
			e.PropertyName == nameof(MainViewModel.IsFirstRunTutorialVisible) ||
			e.PropertyName == nameof(MainViewModel.FirstRunTutorialTargetName))
		{
			ScheduleSpotlightUpdate();
		}
	}

	private void ScheduleSpotlightUpdate()
	{
		Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateSpotlight);
	}

	private void UpdateSpotlight()
	{
		if (_viewModel is null || !_viewModel.IsFirstRunTutorialVisible || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
		{
			HighlightBorder.Visibility = Visibility.Collapsed;
			DimPath.Data = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1)));
			Card.HorizontalAlignment = HorizontalAlignment.Center;
			Card.VerticalAlignment = VerticalAlignment.Center;
			Card.Margin = new Thickness(0);
			return;
		}

		var targetBounds = GetTargetBounds(_viewModel.FirstRunTutorialTargetName);
		if (targetBounds is Rect rect && rect.Width > 0 && rect.Height > 0)
		{
			var fullRect = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1)));
			var holeRect = new RectangleGeometry(rect);
			var spotlight = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, holeRect);
			DimPath.Data = spotlight;

			HighlightBorder.Width = rect.Width + 6;
			HighlightBorder.Height = rect.Height + 6;
			HighlightBorder.HorizontalAlignment = HorizontalAlignment.Left;
			HighlightBorder.VerticalAlignment = VerticalAlignment.Top;
			HighlightBorder.Margin = new Thickness(rect.Left - 3, rect.Top - 3, 0, 0);
			HighlightBorder.Visibility = Visibility.Visible;

			PositionCard(rect);
		}
		else
		{
			HighlightBorder.Visibility = Visibility.Collapsed;
			DimPath.Data = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1)));
			Card.HorizontalAlignment = HorizontalAlignment.Center;
			Card.VerticalAlignment = VerticalAlignment.Center;
			Card.Margin = new Thickness(0);
		}
	}

	private Rect? GetTargetBounds(string? targetName)
	{
		if (string.IsNullOrEmpty(targetName))
			return null;

		var host = Window.GetWindow(this);
		if (host is null)
			return null;

		var target = FindVisualChild(host, targetName);
		if (target is not FrameworkElement element || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
			return null;

		try
		{
			var transform = element.TransformToVisual(this);
			var origin = transform.Transform(new Point(0, 0));
			return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
		}
		catch
		{
			// 某些模板内元素无法用 TransformToVisual 直接转换时，退回屏幕坐标换算。
			try
			{
				var targetScreen = element.PointToScreen(new Point(0, 0));
				var overlayScreen = PointToScreen(new Point(0, 0));
				var dpi = VisualTreeHelper.GetDpi(this);
				var left = (targetScreen.X - overlayScreen.X) / dpi.DpiScaleX;
				var top = (targetScreen.Y - overlayScreen.Y) / dpi.DpiScaleY;
				return new Rect(left, top, element.ActualWidth, element.ActualHeight);
			}
			catch
			{
				return null;
			}
		}
	}

	private void PositionCard(Rect target)
	{
		if (ActualWidth <= 0 || ActualHeight <= 0)
		{
			Card.HorizontalAlignment = HorizontalAlignment.Center;
			Card.VerticalAlignment = VerticalAlignment.Center;
			Card.Margin = new Thickness(0);
			return;
		}

		Card.Measure(new Size(ActualWidth, ActualHeight));
		var cardWidth = Math.Min(Card.DesiredSize.Width, Math.Max(0, ActualWidth - 32));
		var cardHeight = Math.Min(Card.DesiredSize.Height, Math.Max(0, ActualHeight - 32));

		var candidates = new List<Rect>
		{
			ClampToBounds(new Rect(target.Right + TutorialMargin, target.Top + (target.Height - cardHeight) / 2, cardWidth, cardHeight)),
			ClampToBounds(new Rect(target.Left - cardWidth - TutorialMargin, target.Top + (target.Height - cardHeight) / 2, cardWidth, cardHeight)),
			ClampToBounds(new Rect(target.Left + (target.Width - cardWidth) / 2, target.Bottom + TutorialMargin, cardWidth, cardHeight)),
			ClampToBounds(new Rect(target.Left + (target.Width - cardWidth) / 2, target.Top - cardHeight - TutorialMargin, cardWidth, cardHeight))
		};

		var targetCenter = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);

		Rect? best = null;
		var bestDistance = double.PositiveInfinity;

		// 第一优先：完全不遮挡目标的位置中，选离目标最近的。
		foreach (var candidate in candidates)
		{
			var intersection = Rect.Intersect(candidate, target);
			if (!intersection.IsEmpty)
				continue;

			var cardCenter = new Point(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2);
			var distance = Math.Sqrt(Math.Pow(cardCenter.X - targetCenter.X, 2) + Math.Pow(cardCenter.Y - targetCenter.Y, 2));
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = candidate;
			}
		}

		// 没有任何位置能完全避开目标时，退而选择遮挡面积最小（并尽可能近）的位置。
		if (best is null)
		{
			var bestIntersectionArea = double.PositiveInfinity;
			foreach (var candidate in candidates)
			{
				var intersection = Rect.Intersect(candidate, target);
				var intersectionArea = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
				var cardCenter = new Point(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2);
				var distance = Math.Sqrt(Math.Pow(cardCenter.X - targetCenter.X, 2) + Math.Pow(cardCenter.Y - targetCenter.Y, 2));

				if (intersectionArea < bestIntersectionArea ||
					(intersectionArea == bestIntersectionArea && distance < bestDistance))
				{
					bestIntersectionArea = intersectionArea;
					bestDistance = distance;
					best = candidate;
				}
			}
		}

		if (best is null)
		{
			Card.HorizontalAlignment = HorizontalAlignment.Center;
			Card.VerticalAlignment = VerticalAlignment.Center;
			Card.Margin = new Thickness(0);
			return;
		}

		Card.HorizontalAlignment = HorizontalAlignment.Left;
		Card.VerticalAlignment = VerticalAlignment.Top;
		Card.Margin = new Thickness(best.Value.Left, best.Value.Top, 0, 0);
	}

	private Rect ClampToBounds(Rect rect)
	{
		var maxLeft = Math.Max(TutorialMargin, ActualWidth - rect.Width - TutorialMargin);
		var maxTop = Math.Max(TutorialMargin, ActualHeight - rect.Height - TutorialMargin);
		var left = Math.Clamp(rect.Left, TutorialMargin, maxLeft);
		var top = Math.Clamp(rect.Top, TutorialMargin, maxTop);
		return new Rect(left, top, rect.Width, rect.Height);
	}

	private static FrameworkElement? FindVisualChild(DependencyObject parent, string name)
	{
		var count = VisualTreeHelper.GetChildrenCount(parent);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
				return element;

			var result = FindVisualChild(child, name);
			if (result is not null)
				return result;
		}

		return null;
	}

	private void Overlay_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _viewModel is not null)
		{
			e.Handled = true;
			_viewModel.SkipFirstRunTutorialCommand.Execute(null);
		}
	}
}