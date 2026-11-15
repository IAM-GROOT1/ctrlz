using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Licensing;
using CtrlZ.Core.Pro;

namespace CtrlZ.App.ViewModels;

public sealed partial class ProViewModel : PageViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private bool _disposed;

    public ProViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base("Pro", "Optional extensions for CTRL+Z. The free core works fully without Pro.")
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        Features = new ObservableCollection<ProFeatureItemViewModel>();
        _services.License.LicenseChanged += OnLicenseChanged;
        LoadModule();
        RefreshLicense();
    }

    public ObservableCollection<ProFeatureItemViewModel> Features { get; }
    public bool HasModule => _services.ProModule.IsLoaded;
    public bool HasFeatures => Features.Count > 0;
    public string ModuleName => _services.ProModule.Module?.DisplayName ?? "Pro module not installed";
    public string ModuleVersion => _services.ProModule.Module is { } module
        ? $"v{module.Version}"
        : string.Empty;
    public string ModuleDescription => _services.ProModule.Module?.Description ??
        "The optional Pro module is not installed. The free core is fully available.";
    public string ModuleError => _services.ProModule.ErrorMessage ?? string.Empty;
    public bool HasModuleError => !string.IsNullOrWhiteSpace(ModuleError);
    public string LastCheckText => _services.License.Snapshot.LastCheckUtc is { } last
        ? $"Last license check: {last.ToLocalTime():g}"
        : "No license check has been made.";
    public string LicenseStateText => _services.License.Snapshot.Message;
    public bool IsProEnabled => _services.License.Snapshot.IsProEnabled;
    public bool IsLicenseMissing => _services.License.Snapshot.Status == LicenseStatus.Missing;
    public string StateTitle => _services.License.Snapshot.Status switch
    {
        LicenseStatus.Active => "Pro is active",
        LicenseStatus.OfflineGrace => "Pro is active in offline grace",
        LicenseStatus.MachineMismatch => "License is for another PC",
        LicenseStatus.Expired => "License needs renewal",
        LicenseStatus.Invalid => "License is not valid",
        _ => "Pro is locked"
    };

    public override Task ActivateAsync()
    {
        RefreshLicense();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void UnlockPro()
    {
        try
        {
            _services.License.OpenPurchasePage();
        }
        catch (Exception ex)
        {
            _dialogs.Error("The Pro purchase page could not be opened.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private void ManageLicense() => _main.SelectSettings();

    [RelayCommand]
    private void OpenFeature(ProFeatureItemViewModel? item)
    {
        if (item is null || !item.CanOpen)
        {
            return;
        }

        try
        {
            var content = item.Feature.CreatePage();
            if (content is not FrameworkElement element)
            {
                _dialogs.Info("This Pro feature is service-only and has no page yet.");
                return;
            }

            var window = new Window
            {
                Title = item.DisplayName,
                Content = element,
                Width = 900,
                Height = 640,
                MinWidth = 640,
                MinHeight = 480,
                Owner = _main.GetWindow(),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Background"]
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _services.Log.Write(Core.Logging.LogLevel.Warning, "Pro feature page failed.", ex);
            _dialogs.Error("This Pro feature could not be opened.\n\n" + ex.Message);
        }
    }

    private void LoadModule()
    {
        Features.Clear();
        if (_services.ProModule.Module is not { } module)
        {
            return;
        }

        foreach (var feature in module.Features ?? Array.Empty<IProFeature>())
        {
            Features.Add(new ProFeatureItemViewModel(feature));
        }
    }

    private void RefreshLicense()
    {
        var snapshot = _services.License.Snapshot;
        foreach (var feature in Features)
        {
            feature.IsAvailable = snapshot.HasFeature(feature.Id);
        }

        OnPropertyChanged(nameof(HasModule));
        OnPropertyChanged(nameof(HasFeatures));
        OnPropertyChanged(nameof(ModuleName));
        OnPropertyChanged(nameof(ModuleVersion));
        OnPropertyChanged(nameof(ModuleDescription));
        OnPropertyChanged(nameof(ModuleError));
        OnPropertyChanged(nameof(HasModuleError));
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(LicenseStateText));
        OnPropertyChanged(nameof(IsProEnabled));
        OnPropertyChanged(nameof(IsLicenseMissing));
        OnPropertyChanged(nameof(StateTitle));
    }

    private void OnLicenseChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshLicense();
        }
        else
        {
            dispatcher.BeginInvoke(RefreshLicense);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.License.LicenseChanged -= OnLicenseChanged;
    }
}

public sealed partial class ProFeatureItemViewModel : ObservableObject
{
    public ProFeatureItemViewModel(IProFeature feature)
    {
        Feature = feature;
    }

    public IProFeature Feature { get; }
    public string Id => Feature.Id;
    public string DisplayName => Feature.DisplayName;
    public string Description => Feature.Description;
    public string Glyph => Feature.Glyph;

    [ObservableProperty]
    private bool _isAvailable;

    public bool CanOpen => IsAvailable;
    public string StateLabel => IsAvailable ? "Available" : "Pro";
    public string StateDescription => IsAvailable
        ? "Included in your license."
        : "Unlock Pro to use this feature.";

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateDescription));
    }
}
