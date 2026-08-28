using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HelmsleyDrive.App;

/// <summary>
/// The port of Mac/HelmsleyDrive/ContentView.swift: one small fixed window that says whether the
/// two things this app is for — a credential and a mounted drive — are currently true, with the
/// one button that fixes whichever is not. Built in code rather than XAML so the whole app stays
/// one greppable language; redrawn wholesale from the model, which for six controls costs nothing.
/// </summary>
public sealed class MainWindow : Window
{
    readonly AppModel _model;

    readonly TextBlock _signedInAs = new() { FontSize = 14 };
    readonly TextBlock _email = new() { FontSize = 11 };
    readonly TextBlock _mountState = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _mountButton = new() { Content = "Mount in File Explorer", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
    readonly Button _signOutButton = new() { Content = "Sign Out and Unmount", Padding = new Thickness(14, 5, 14, 5) };
    readonly Button _signInButton = new() { Content = "Sign In and Mount", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
    readonly StackPanel _connected = new();
    readonly StackPanel _disconnected = new();
    readonly TextBlock _error = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0x5D, 0x00)),
        Margin = new Thickness(0, 12, 0, 0),
    };
    readonly ProgressBar _busy = new() { IsIndeterminate = true, Width = 120, Height = 4, HorizontalAlignment = HorizontalAlignment.Left };

    static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    static readonly Brush Tertiary = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public MainWindow(AppModel model)
    {
        _model = model;

        Title = "Helmsley Drive";
        Width = 480;
        Height = 400;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemColors.WindowBrush;

        Content = Build();
        Render();

        // InvokeAsync, never Invoke: the poll loop raises changes from the thread pool, and a
        // blocking marshal from there can meet a dispatcher that is itself blocked in Shutdown.
        model.PropertyChanged += (_, _) => Dispatcher.InvokeAsync(Render);
        Loaded += async (_, _) => await model.Startup();

        _signInButton.Click += async (_, _) => await model.Connect();
        _mountButton.Click += async (_, _) => await model.Mount();
        _signOutButton.Click += async (_, _) => await model.Disconnect();
    }

    UIElement Build()
    {
        // Header: the mark and the sentence. A stock cloud glyph until the app has an icon of its
        // own — the same placeholder standing the sync root's Explorer entry uses.
        var glyph = new TextBlock
        {
            Text = "\uE753", // Cloud
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 38,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Helmsley Drive", FontSize = 19, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = "The client portal's documents, in File Explorer.",
            FontSize = 12,
            Foreground = Secondary,
        });
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(glyph);
        header.Children.Add(titles);

        // The signed-in panel: identity, mount state, and the buttons.
        var check = new TextBlock
        {
            Text = "\uE930", // Completed, the circled check
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
            Margin = new Thickness(0, 2, 8, 0),
        };
        var identity = new StackPanel();
        identity.Children.Add(_signedInAs);
        _email.Foreground = Secondary;
        identity.Children.Add(_email);
        var identityRow = new StackPanel { Orientation = Orientation.Horizontal };
        identityRow.Children.Add(check);
        identityRow.Children.Add(identity);

        _mountState.Foreground = Secondary;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_mountButton);
        buttons.Children.Add(_signOutButton);

        _connected.Children.Add(identityRow);
        _connected.Children.Add(_mountState);
        _connected.Children.Add(buttons);

        // The signed-out panel: what signing in is for, and the button that starts it.
        _disconnected.Children.Add(new TextBlock
        {
            Text = "Sign in with your Helmsley administrator account to mount the document tree as a drive in File Explorer.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Secondary,
        });
        _disconnected.Children.Add(new TextBlock
        {
            Text = "Your browser will open to the portal's sign-in page. You will be asked for your password and the code texted to you, exactly as on the portal.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Tertiary,
            Margin = new Thickness(0, 6, 0, 0),
        });
        _signInButton.Margin = new Thickness(0, 14, 0, 0);
        _disconnected.Children.Add(_signInButton);

        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // separator
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // panels
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // error
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // spacer
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // footer

        var separator = new Separator { Margin = new Thickness(0, 16, 0, 16) };
        Grid.SetRow(header, 0);
        Grid.SetRow(separator, 1);
        var panels = new Grid();
        panels.Children.Add(_connected);
        panels.Children.Add(_disconnected);
        Grid.SetRow(panels, 2);
        Grid.SetRow(_error, 3);
        Grid.SetRow(_busy, 5);

        layout.Children.Add(header);
        layout.Children.Add(separator);
        layout.Children.Add(panels);
        layout.Children.Add(_error);
        layout.Children.Add(_busy);
        return layout;
    }

    void Render()
    {
        var signedIn = _model.IsSignedIn;
        _connected.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _disconnected.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;

        _signedInAs.Text = $"Signed in as {_model.Admin?.Name ?? _model.Admin?.Email ?? "an administrator"}";
        _email.Text = _model.Admin?.Email ?? "";
        _email.Visibility = _model.Admin is { Name: not null, Email: not null } ? Visibility.Visible : Visibility.Collapsed;

        _mountState.Text = _model.IsMounted
            ? "Mounted — look for “Helmsley Drive” in File Explorer's sidebar. Files download the first time they are opened."
            : "Not mounted yet.";

        // Only when the credential is good but the drive is not up — in that state it is the one
        // thing to do, which is why it and not Sign Out holds the default-button accent.
        _mountButton.Visibility = _model.IsMounted ? Visibility.Collapsed : Visibility.Visible;

        _signInButton.IsEnabled = !_model.IsWorking;
        _mountButton.IsEnabled = !_model.IsWorking;
        _signOutButton.IsEnabled = !_model.IsWorking;

        _error.Text = _model.ErrorMessage ?? "";
        _error.Visibility = _model.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        _busy.Visibility = _model.IsWorking ? Visibility.Visible : Visibility.Hidden;
    }
}
