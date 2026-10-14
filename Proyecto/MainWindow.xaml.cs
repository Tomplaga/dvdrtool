using CdExtraStudio.Models;
using CdExtraStudio.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CdExtraStudio;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CdTrack> _tracks = new();
    private readonly ObservableCollection<DataFileEntry> _files = new();
    private readonly ObservableCollection<string> _burnAudio = new();
    private readonly ObservableCollection<CompressedAudioItem> _burnCompressedAudio = new();
    private readonly ObservableCollection<DataProjectEntry> _burnData = new();
    private readonly ObservableCollection<DataProjectEntry> _dataOnly = new();
    private readonly ObservableCollection<CdrtoolsDevice> _cdrtoolsDevices = new();
    private readonly string _dataProjectRoot = Path.Combine(Path.GetTempPath(), $"dvdrtool_DataProject_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private readonly string _dataOnlyProjectRoot = Path.Combine(Path.GetTempPath(), $"dvdrtool_DataOnly_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private readonly CdAudioService _audio = new();
    private readonly AudioConversionService _audioConverter = new();
    private readonly CdExtraBurner _burner = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _capacityRefreshCts;
    private readonly DispatcherTimer _mediaCapacityRetryTimer;
    private bool _busy;
    private DiscCapacityInfo? _lastCapacity;
    private string? _bluRayIsoPath;
    private string? _bluRayFolderPath;

    private sealed class UserPreferences
    {
        public string BurnSpeed { get; set; } = "4x";
        public int StartPageIndex { get; set; } = 0;
        public int InterfaceModeIndex { get; set; } = 0;
        public bool ShowCdText { get; set; } = true;
        public bool ShowTechnicalLog { get; set; } = true;
        public bool SimulationMode { get; set; }
        public bool ConfirmBeforeBurn { get; set; } = true;
        public int SimulationMediaIndex { get; set; } = 0;
    }

    private UserPreferences _preferences = new();
    private static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dvdrtool",
        "settings.ini");

    private const long SimulatedCdSectors = 359845;
    private const long SimulationSafetySectors = 750;
    private const long SimulationMultisessionSectors = 11400;
    private const long SimulatedDvdBytes = 4_700_000_000L;
    private const long SimulatedDvdDlBytes = 8_500_000_000L;
    private const long SimulatedBd25Bytes = 25_000_000_000L;
    private const long SimulatedBd50Bytes = 50_000_000_000L;

    private OpticalDriveInfo? SelectedDrive => DriveCombo.SelectedItem as OpticalDriveInfo;
    private CdrtoolsDevice? SelectedCdrtoolsDevice => CdrtoolsDeviceCombo.SelectedItem as CdrtoolsDevice;
    private string DataAudioProjectDirectory => Path.Combine(_dataProjectRoot, "AUDIO");

    private void ApplyResponsiveWindowSize()
    {
        // SystemParameters.WorkArea ya descuenta la barra de tareas y trabaja
        // en unidades independientes de DPI, así que WPF escala correctamente
        // tanto por resolución como por el zoom configurado en Windows.
        Rect area = SystemParameters.WorkArea;

        double minWidth = Math.Min(820, Math.Max(720, area.Width * 0.78));
        double minHeight = Math.Min(520, Math.Max(480, area.Height * 0.72));

        MinWidth = minWidth;
        MinHeight = minHeight;
        MaxWidth = area.Width;
        MaxHeight = area.Height;

        double targetWidth;
        if (area.Width <= 1280)
            targetWidth = area.Width * 0.97;
        else if (area.Width <= 1600)
            targetWidth = Math.Min(1200, area.Width * 0.92);
        else
            targetWidth = Math.Min(1440, area.Width * 0.86);

        double targetHeight;
        if (area.Height <= 720)
            targetHeight = area.Height * 0.97;
        else if (area.Height <= 900)
            targetHeight = Math.Min(800, area.Height * 0.94);
        else
            targetHeight = Math.Min(900, area.Height * 0.90);

        Width = Math.Max(MinWidth, Math.Min(targetWidth, MaxWidth));
        Height = Math.Max(MinHeight, Math.Min(targetHeight, MaxHeight));

        // Centrar dentro del área útil, sin quedar detrás de la barra de tareas.
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Sin botón de actualización: si el CD todavía no estaba listo cuando se
        // hizo la primera consulta, reintentamos automáticamente. El temporizador
        // solo lanza una nueva lectura cuando la capacidad sigue siendo desconocida.
        _mediaCapacityRetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _mediaCapacityRetryTimer.Tick += (_, _) =>
        {
            if (!_busy && MainTabs.SelectedIndex == 3 && (_lastCapacity is null || !_lastCapacity.IsKnown))
            {
                RefreshDrives();
                ScheduleAutomaticCapacityRefresh(0);
            }
        };

        TracksGrid.ItemsSource = _tracks;
        FilesGrid.ItemsSource = _files;
        BurnAudioList.ItemsSource = _burnAudio;
        AudioOnlyList.ItemsSource = _burnAudio;
        BurnMp3List.ItemsSource = _burnCompressedAudio;
        BurnDataGrid.ItemsSource = _burnData;
        DataOnlyGrid.ItemsSource = _dataOnly;
        CdrtoolsDeviceCombo.ItemsSource = _cdrtoolsDevices;

        DataTargetCombo.ItemsSource = new[] { "CDPLUS", "PICTURES", "Raíz del CD" };
        DataTargetCombo.SelectedIndex = 0;
        DataOnlyTargetCombo.ItemsSource = new[] { "Raíz del disco" };
        DataOnlyTargetCombo.SelectedIndex = 0;
        InitializeDataProject();
        InitializeDataOnlyProject();
        _burnAudio.CollectionChanged += (_, _) => UpdateStandaloneSummaries();
        _burnData.CollectionChanged += (_, _) => UpdateStandaloneSummaries();
        _dataOnly.CollectionChanged += (_, _) => UpdateStandaloneSummaries();
        UpdateStandaloneSummaries();

        LoadPreferences();
        ApplyPreferencesToControls();
        ApplyVisualOptions();

        Loaded += async (_, _) =>
        {
            ApplyResponsiveWindowSize();
            RefreshDrives();
            await InitializeCdrtoolsAsync();
            await InitializeFfmpegAsync();
            RefreshOptionsSummary();
            _mediaCapacityRetryTimer.Start();
            ScheduleAutomaticCapacityRefresh(150);
        };
        Activated += (_, _) =>
        {
            // El selector de unidad está oculto en la interfaz limpia.
            // Actualizamos la unidad y la capacidad al volver a la aplicación.
            if (!_busy)
            {
                RefreshDrives();
                if (MainTabs.SelectedIndex == 3) ScheduleAutomaticCapacityRefresh(80);
            }
        };
        Closed += (_, _) =>
        {
            SavePreferencesCore(showMessage: false);
            _mediaCapacityRetryTimer.Stop();
            _cts?.Cancel();
            CancelAutomaticCapacityRefresh();
            _audio.Dispose();
            TryDeleteDataProjects();
        };

        InvalidateCapacity();
        NavigateTo(Math.Clamp(_preferences.StartPageIndex, 0, 4));
        Log("dvdrtool iniciado.");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(Convert.ToString(button.Tag), out int index)) return;
        NavigateTo(index);
    }

    private void NavigateTo(int index)
    {
        index = Math.Clamp(index, 0, 6);
        MainTabs.SelectedIndex = index;
        UpdateNavigationSelection(index);

        (PageTitle.Text, PageSubtitle.Text) = index switch
        {
            0 => ("Inicio", "Audio CD, Datos, CD Extra y Blu-ray"),
            1 => ("Grabar Audio", "Crea solamente un Audio CD tradicional"),
            2 => ("Grabar Datos", "Crea solamente un CD/DVD de datos ISO9660/Joliet"),
            3 => ("Crear CD Extra", "Grabación multisesión estable con cdrtools"),
            4 => ("Películas Blu-ray", "Graba ISO o carpetas BDMV en BD-R/BD-RE"),
            5 => ("Opciones", "Interfaz, preferencias y modo prueba"),
            6 => ("Registro", "Salida de dvdrtool, cdrecord y mkisofs"),
            _ => ("dvdrtool", "")
        };

        if (index is 0 or 1 or 2 or 4) RefreshDrives();
        if (index == 3) ScheduleAutomaticCapacityRefresh(120);
        if (index == 5) RefreshOptionsSummary();
    }

    private void UpdateNavigationSelection(int selectedIndex)
    {
        Button[] buttons =
        {
            NavHomeButton, NavAudioButton, NavDataButton, NavCreateButton, NavBluRayButton, NavOptionsButton, NavLogButton
        };

        var normalForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C8D6E6"));
        var activeBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B6BFF"));

        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = i == selectedIndex;
            buttons[i].Background = active ? activeBackground : Brushes.Transparent;
            buttons[i].Foreground = active ? Brushes.White : normalForeground;
            buttons[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void LoadPreferences()
    {
        _preferences = new UserPreferences();
        try
        {
            if (!File.Exists(PreferencesPath)) return;

            foreach (string rawLine in File.ReadAllLines(PreferencesPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                int equals = line.IndexOf('=');
                if (equals <= 0) continue;

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();

                switch (key)
                {
                    case "BurnSpeed":
                        if (!string.IsNullOrWhiteSpace(value)) _preferences.BurnSpeed = value;
                        break;
                    case "StartPageIndex":
                        if (int.TryParse(value, out int startPage)) _preferences.StartPageIndex = Math.Clamp(startPage, 0, 4);
                        break;
                    case "InterfaceModeIndex":
                        if (int.TryParse(value, out int mode)) _preferences.InterfaceModeIndex = Math.Clamp(mode, 0, 1);
                        break;
                    case "ShowCdText":
                        if (bool.TryParse(value, out bool showCdText)) _preferences.ShowCdText = showCdText;
                        break;
                    case "ShowTechnicalLog":
                        if (bool.TryParse(value, out bool showLog)) _preferences.ShowTechnicalLog = showLog;
                        break;
                    case "SimulationMode":
                        if (bool.TryParse(value, out bool simulation)) _preferences.SimulationMode = simulation;
                        break;
                    case "ConfirmBeforeBurn":
                        if (bool.TryParse(value, out bool confirm)) _preferences.ConfirmBeforeBurn = confirm;
                        break;
                    case "SimulationMediaIndex":
                        if (int.TryParse(value, out int media)) _preferences.SimulationMediaIndex = Math.Clamp(media, 0, 2);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"No se pudieron cargar las preferencias: {ex.Message}");
        }
    }

    private void ApplyPreferencesToControls()
    {
        BurnSpeedCombo.Text = _preferences.BurnSpeed;
        StartPageCombo.SelectedIndex = Math.Clamp(_preferences.StartPageIndex, 0, 4);
        InterfaceModeCombo.SelectedIndex = Math.Clamp(_preferences.InterfaceModeIndex, 0, 1);
        ShowCdTextCheck.IsChecked = _preferences.ShowCdText;
        ShowTechnicalLogCheck.IsChecked = _preferences.ShowTechnicalLog;
        SimulationModeCheck.IsChecked = _preferences.SimulationMode;
        ConfirmBeforeBurnCheck.IsChecked = _preferences.ConfirmBeforeBurn;
        SimulationMediaCombo.SelectedIndex = Math.Clamp(_preferences.SimulationMediaIndex, 0, 2);
    }

    private void ApplyVisualOptions()
    {
        bool advanced = InterfaceModeCombo.SelectedIndex == 1;
        bool showCdText = ShowCdTextCheck.IsChecked == true;
        bool showLog = ShowTechnicalLogCheck.IsChecked == true;
        bool simulation = SimulationModeCheck.IsChecked == true;

        CdrtoolsAdvancedExpander.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        FfmpegAdvancedExpander.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        AudioCdTextPanel.Visibility = showCdText ? Visibility.Visible : Visibility.Collapsed;
        ExtraCdTextPanel.Visibility = showCdText ? Visibility.Visible : Visibility.Collapsed;
        NavLogButton.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;

        if (!showLog && MainTabs.SelectedIndex == 6)
            NavigateTo(0);

        BurnAudioOnlyButton.Content = simulation ? "SIMULAR AUDIO CD" : "GRABAR SOLO AUDIO";
        BurnDataOnlyButton.Content = simulation ? "SIMULAR DATOS" : "GRABAR DATOS CD/DVD";
        BurnButton.Content = simulation ? "SIMULAR CD EXTRA" : "GRABAR CD EXTRA";
        BluRayBurnButton.Content = simulation ? "SIMULAR BLU-RAY" : "GRABAR BLU-RAY";

        OptionsModeText.Text = simulation ? "MODO PRUEBA · NO GRABA" : "MODO NORMAL";
        OptionsModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            simulation ? "#B45309" : "#0B5FD7"));

        if (simulation)
        {
            SetStatus("Modo prueba activo · no se enviarán órdenes de escritura", 0);
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
        }
        else if (!_busy)
        {
            SetStatus("Listo", 0);
        }
    }

    private void SaveOptions_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BurnSpeedCombo.Text))
            BurnSpeedCombo.Text = "4x";

        ApplyVisualOptions();
        SavePreferencesCore(showMessage: true);
        RefreshOptionsSummary();
    }

    private void RestoreOptions_Click(object sender, RoutedEventArgs e)
    {
        _preferences = new UserPreferences();
        ApplyPreferencesToControls();
        ApplyVisualOptions();
        SavePreferencesCore(showMessage: false);
        RefreshOptionsSummary();
        Info("Se restauraron las opciones predeterminadas.");
    }

    private void SavePreferencesCore(bool showMessage)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);

            _preferences.BurnSpeed = string.IsNullOrWhiteSpace(BurnSpeedCombo.Text) ? "4x" : BurnSpeedCombo.Text.Trim();
            _preferences.StartPageIndex = Math.Clamp(StartPageCombo.SelectedIndex, 0, 4);
            _preferences.InterfaceModeIndex = Math.Clamp(InterfaceModeCombo.SelectedIndex, 0, 1);
            _preferences.ShowCdText = ShowCdTextCheck.IsChecked == true;
            _preferences.ShowTechnicalLog = ShowTechnicalLogCheck.IsChecked == true;
            _preferences.SimulationMode = SimulationModeCheck.IsChecked == true;
            _preferences.ConfirmBeforeBurn = ConfirmBeforeBurnCheck.IsChecked != false;
            _preferences.SimulationMediaIndex = Math.Clamp(SimulationMediaCombo.SelectedIndex, 0, 2);

            File.WriteAllLines(PreferencesPath, new[]
            {
                "# dvdrtool preferences",
                $"BurnSpeed={_preferences.BurnSpeed}",
                $"StartPageIndex={_preferences.StartPageIndex}",
                $"InterfaceModeIndex={_preferences.InterfaceModeIndex}",
                $"ShowCdText={_preferences.ShowCdText}",
                $"ShowTechnicalLog={_preferences.ShowTechnicalLog}",
                $"SimulationMode={_preferences.SimulationMode}",
                $"ConfirmBeforeBurn={_preferences.ConfirmBeforeBurn}",
                $"SimulationMediaIndex={_preferences.SimulationMediaIndex}"
            });

            if (showMessage)
            {
                Log("Preferencias guardadas.");
                Info("Opciones aplicadas y guardadas.");
            }
        }
        catch (Exception ex)
        {
            Log($"No se pudieron guardar las preferencias: {ex.Message}");
            if (showMessage) Info("No se pudieron guardar las preferencias.");
        }
    }

    private void RefreshOptionsSummary()
    {
        if (OptionsCdrtoolsText is null || OptionsFfmpegText is null) return;

        string cdrtools = CdrtoolsPathText?.Text?.Trim() ?? "";
        OptionsCdrtoolsText.Text = CdExtraBurner.IsValidToolsDirectory(cdrtools)
            ? $"Listo · {cdrtools}"
            : "No disponible todavía. dvdrtool puede instalarlo automáticamente.";

        string ffmpeg = FfmpegPathText?.Text?.Trim() ?? "";
        OptionsFfmpegText.Text = File.Exists(ffmpeg)
            ? $"Listo · {ffmpeg}"
            : "No disponible todavía. Se instalará cuando un formato de audio lo necesite.";
    }

    private bool IsSimulationMode => SimulationModeCheck.IsChecked == true;
    private bool ShouldConfirmBeforeBurn => ConfirmBeforeBurnCheck.IsChecked != false;
    private bool CdTextEnabled => ShowCdTextCheck.IsChecked == true;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrives();
        if (CdExtraBurner.IsValidToolsDirectory(CdrtoolsPathText.Text))
            await RefreshCdrtoolsDevicesAsync(silent: true);
    }

    private void RefreshDrives()
    {
        string? previous = SelectedDrive?.RootPath;
        var drives = OpticalDriveService.GetDrives();
        DriveCombo.ItemsSource = drives;
        DriveCombo.SelectedItem = drives.FirstOrDefault(d => d.RootPath.Equals(previous, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {

            HomeDriveText.Text = "Sin unidad";
            HomeMediaText.Text = "Conecta una unidad CD/DVD compatible";
        }

        Log($"Unidades ópticas de Windows detectadas: {drives.Count}.");
    }

    private async Task InitializeCdrtoolsAsync()
    {
        string? tools = CdExtraBurner.FindToolsDirectory();
        if (string.IsNullOrWhiteSpace(tools))
        {
            CdrtoolsPathText.Text = "";
            CdrtoolsStatusText.Text = "Motor cdrtools no instalado en esta copia de dvdrtool.";
            Log("cdrtools no se encontró automáticamente.");
            RefreshAudioSupportStatus();

            MessageBoxResult answer = MessageBox.Show(
                this,
                "dvdrtool necesita cdrecord.exe y mkisofs.exe para detectar y usar la grabadora.\n\n" +
                "¿Quieres descargar ahora el motor oficial cdrtools?\n\n" +
                "Se descarga el paquete portable oficial de cdrtfe desde SourceForge (aprox. 10,6 MB), " +
                "se verifica su SHA-256 y se extrae únicamente cdrtools dentro de dvdrtool. " +
                "No se instala cdrtfe en Windows.",
                "Instalar motor cdrtools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
                await InstallCdrtoolsEngineAsync(showErrors: true);
            return;
        }

        CdrtoolsPathText.Text = tools;
        CdrtoolsStatusText.Text = "cdrtools detectado. Buscando grabadoras…";
        Log($"cdrtools detectado: {tools}");
        RefreshAudioSupportStatus();
        await RefreshCdrtoolsDevicesAsync(silent: true);
    }

    private async Task<bool> InstallCdrtoolsEngineAsync(bool showErrors)
    {
        if (_busy) return false;

        try
        {
            SetStatus("Descargando motor cdrtools…", 10);
            CdrtoolsStatusText.Text = "Descargando motor cdrtools desde SourceForge…";
            var installProgress = new Progress<string>(message =>
            {
                CdrtoolsStatusText.Text = message;
                Log(message);
            });

            string tools = await CdrtoolsBootstrapper.InstallAsync(installProgress);
            CdrtoolsPathText.Text = tools;
            CdrtoolsStatusText.Text = "Motor cdrtools instalado localmente. Buscando grabadoras…";
            Log($"cdrtools instalado localmente: {tools}");
            RefreshAudioSupportStatus();
            await RefreshCdrtoolsDevicesAsync(silent: false);
            return true;
        }
        catch (Exception ex)
        {
            CdrtoolsStatusText.Text = "No se pudo instalar automáticamente cdrtools.";
            SetStatus("Error instalando cdrtools", 0, error: true);
            Log($"Error al instalar cdrtools: {ex}");
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    "No se pudo descargar o instalar cdrtools automáticamente.\n\n" +
                    ex.Message +
                    "\n\nComprueba Internet o usa Examinar para seleccionar una carpeta cdrtools manualmente.",
                    "Motor cdrtools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return false;
        }
    }

    private async void InstallCdrtools_Click(object sender, RoutedEventArgs e)
    {
        if (CdrtoolsBootstrapper.IsLocalEngineComplete())
        {
            CdrtoolsPathText.Text = CdrtoolsBootstrapper.LocalToolsDirectory;
            await RefreshCdrtoolsDevicesAsync(silent: false);
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            "Descargar el motor cdrtools oficial y guardarlo dentro de dvdrtool?\n\n" +
            "El paquete se verificará con SHA-256 antes de extraerlo.",
            "Instalar motor cdrtools",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await InstallCdrtoolsEngineAsync(showErrors: true);
    }

    private async void BrowseCdrtools_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta que contiene cdrecord.exe y mkisofs.exe"
        };

        if (dlg.ShowDialog(this) != true) return;
        if (!CdExtraBurner.IsValidToolsDirectory(dlg.FolderName))
        {
            Info("Esa carpeta no contiene cdrecord.exe y mkisofs.exe. Puedes usar Instalar motor o seleccionar una carpeta cdrtools válida.");
            return;
        }

        CdrtoolsPathText.Text = dlg.FolderName;
        InvalidateCapacity();
        Log($"Carpeta de cdrtools seleccionada: {dlg.FolderName}");
        RefreshAudioSupportStatus();
        await RefreshCdrtoolsDevicesAsync(silent: false);
    }

    private async void ScanCdrtools_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCdrtoolsDevicesAsync(silent: false);
    }

    private async Task RefreshCdrtoolsDevicesAsync(bool silent)
    {
        string tools = CdrtoolsPathText.Text.Trim();
        if (!CdExtraBurner.IsValidToolsDirectory(tools))
        {
            CdrtoolsStatusText.Text = "Instala el motor cdrtools o selecciona una carpeta cdrtools válida.";
            if (!silent) Info("Instala el motor cdrtools o selecciona una carpeta válida primero.");
            return;
        }

        string? previous = SelectedCdrtoolsDevice?.DeviceSpec;
        CdrtoolsStatusText.Text = "Buscando grabadoras con cdrecord (SPTI / modo automático)…";
        SetStatus("Buscando grabadoras cdrtools…", 5);

        try
        {
            var logProgress = new Progress<string>(LogCdrtoolsLine);
            var devices = await _burner.ScanDevicesAsync(tools, logProgress);
            _cdrtoolsDevices.Clear();
            foreach (var device in devices) _cdrtoolsDevices.Add(device);

            CdrtoolsDeviceCombo.SelectedItem = _cdrtoolsDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceSpec, previous, StringComparison.OrdinalIgnoreCase))
                ?? _cdrtoolsDevices.FirstOrDefault();

            CdrtoolsStatusText.Text = devices.Count switch
            {
                0 => "No se encontraron grabadoras ópticas con SPTI.",
                1 => "1 grabadora detectada y seleccionada automáticamente.",
                _ => $"{devices.Count} grabadoras detectadas. Selecciona la que quieras usar."
            };
            SetStatus(devices.Count > 0 ? "Grabadora cdrtools lista" : "Sin grabadora cdrtools", 0, error: devices.Count == 0);
            Log($"Grabadoras cdrtools detectadas: {devices.Count}.");
            ScheduleAutomaticCapacityRefresh(180);
        }
        catch (Exception ex)
        {
            _cdrtoolsDevices.Clear();
            CdrtoolsStatusText.Text = "No se pudo ejecutar cdrecord. Revisa cdrtools y sus DLL de Cygwin.";
            SetStatus("Error al detectar cdrtools", 0, error: true);
            LogError(ex);
            if (!silent) MessageBox.Show(this, ex.Message, "cdrtools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var drive = SelectedDrive;

        HomeDriveText.Text = drive?.RootPath.TrimEnd('\\') ?? "—";
        HomeMediaText.Text = drive is null
            ? "Sin información"
            : drive.IsReady
                ? (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Disco insertado" : drive.VolumeLabel)
                : "Sin disco";

        if (drive?.IsReady == true)
        {
            await ReloadDiscInfoAsync(silent: true);
            LoadDataFiles();
        }
        else
        {
            _tracks.Clear();
            _files.Clear();
            UpdateHomeCounters();
        }
    }

    private async void LoadDisc_Click(object sender, RoutedEventArgs e) => await ReloadDiscInfoAsync(silent: false);

    private async Task ReloadDiscInfoAsync(bool silent)
    {
        var drive = SelectedDrive;
        if (drive is null) return;
        try
        {
            SetStatus("Leyendo tabla de contenidos…", 12);
            var tracks = await Task.Run(() => _audio.ReadTracks(drive.RootPath));
            _tracks.Clear();
            foreach (var t in tracks) _tracks.Add(t);
            UpdateHomeCounters();
            SetStatus($"{tracks.Count} pista(s) detectada(s)", 0);
            Log($"TOC leído en {drive.RootPath}: {tracks.Count} pistas ({tracks.Count(t => t.IsAudio)} audio, {tracks.Count(t => !t.IsAudio)} datos).");
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo leer el audio del disco", 0, error: true);
            LogError(ex);
            if (!silent) MessageBox.Show(this, ex.Message, "Lectura del CD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadData_Click(object sender, RoutedEventArgs e) => LoadDataFiles();

    private void LoadDataFiles()
    {
        var drive = SelectedDrive;
        _files.Clear();
        if (drive?.IsReady != true)
        {
            UpdateHomeCounters();
            return;
        }

        var files = OpticalDriveService.ReadDataFiles(drive.RootPath);
        foreach (var file in files) _files.Add(file);
        UpdateHomeCounters();
        Log($"Sesión de datos: {_files.Count} archivo(s) visibles en {drive.RootPath}.");
    }

    private void UpdateHomeCounters()
    {
        HomeTracksText.Text = _burnAudio.Count.ToString();
        HomeFilesText.Text = _burnData.Count(item => !item.IsDirectory).ToString();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null || TracksGrid.SelectedItem is not CdTrack track)
        {
            Info("Selecciona una pista.");
            return;
        }
        if (!track.IsAudio)
        {
            Info("La pista seleccionada es de datos.");
            return;
        }

        try
        {
            _audio.PlayTrack(SelectedDrive.RootPath, track.Number);
            SetStatus($"Reproduciendo pista {track.Number}", 0);
            Log($"Reproducción: pista {track.Number} en {SelectedDrive.RootPath}.");
        }
        catch (Exception ex)
        {
            LogError(ex);
            Info(ex.Message);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _audio.Stop();
        SetStatus("Reproducción detenida", 0);
    }

    private async void RipSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null || TracksGrid.SelectedItem is not CdTrack track || !track.IsAudio)
        {
            Info("Selecciona una pista de audio.");
            return;
        }

        var dlg = new SaveFileDialog { Filter = "Audio WAV|*.wav", FileName = $"Track{track.Number:00}.wav" };
        if (dlg.ShowDialog(this) != true) return;
        await RipOneAsync(track, dlg.FileName);
    }

    private async void RipAll_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null)
        {
            Info("Selecciona una unidad.");
            return;
        }

        var audioTracks = _tracks.Where(t => t.IsAudio).ToList();
        if (audioTracks.Count == 0)
        {
            Info("No hay pistas de audio cargadas.");
            return;
        }

        var dlg = new OpenFolderDialog { Title = "Carpeta para guardar las pistas WAV" };
        if (dlg.ShowDialog(this) != true) return;

        await WithBusyAsync(async token =>
        {
            for (int i = 0; i < audioTracks.Count; i++)
            {
                var track = audioTracks[i];
                var perTrack = new Progress<double>(p =>
                {
                    double overall = (i + p) / audioTracks.Count;
                    SetStatus($"Extrayendo pista {track.Number} ({i + 1}/{audioTracks.Count})…", overall * 100);
                });
                await _audio.RipTrackAsync(
                    SelectedDrive.RootPath,
                    track,
                    Path.Combine(dlg.FolderName, $"Track{track.Number:00}.wav"),
                    perTrack,
                    token);
            }
            SetStatus("Extracción completada", 100);
            Log($"Extracción completa: {audioTracks.Count} pistas guardadas en {dlg.FolderName}.");
        }, "Extracción de audio");
    }

    private async Task RipOneAsync(CdTrack track, string fileName)
    {
        if (SelectedDrive is null) return;
        await WithBusyAsync(async token =>
        {
            var progress = new Progress<double>(p => SetStatus($"Extrayendo pista {track.Number}…", p * 100));
            await _audio.RipTrackAsync(SelectedDrive.RootPath, track, fileName, progress, token);
            SetStatus("Pista extraída", 100);
            Log($"Pista {track.Number} extraída a {fileName}.");
        }, "Extracción de audio");
    }

    private void OpenDataFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not DataFileEntry file)
        {
            Info("Selecciona un archivo.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(file.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ex);
            Info(ex.Message);
        }
    }

    private void CopyDataFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not DataFileEntry file)
        {
            Info("Selecciona un archivo.");
            return;
        }

        var dlg = new OpenFolderDialog { Title = "Copiar archivo a…" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var destination = Path.Combine(dlg.FolderName, file.Name);
            File.Copy(file.FullPath, destination, overwrite: true);
            Log($"Copiado: {file.FullPath} -> {destination}");
            SetStatus("Archivo copiado", 0);
        }
        catch (Exception ex)
        {
            LogError(ex);
            Info(ex.Message);
        }
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", SelectedDrive.RootPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private void UpdateStandaloneSummaries()
    {
        if (AudioOnlySummaryText is not null)
            AudioOnlySummaryText.Text = $"{_burnAudio.Count} pista(s) seleccionada(s)";

        if (DataOnlySummaryText is not null)
        {
            long bytes = _dataOnly.Where(item => !item.IsDirectory).Sum(item => item.Size);
            int files = _dataOnly.Count(item => !item.IsDirectory);
            int folders = _dataOnly.Count(item => item.IsDirectory);
            DataOnlySummaryText.Text = $"{files} archivo(s) · {folders} carpeta(s) · {FormatBytes(bytes)}";
        }
        UpdateHomeCounters();
    }

    private void RemoveAudioOnly_Click(object sender, RoutedEventArgs e)
    {
        if (AudioOnlyList.SelectedItem is not string file) return;
        _burnAudio.Remove(file);
        InvalidateCapacity();
    }

    private void ClearAudioOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_burnAudio.Count == 0) return;
        var result = MessageBox.Show(
            this,
            $"¿Quitar las {_burnAudio.Count} pistas de la lista de Audio CD?\n\nLos archivos originales no se modificarán.",
            "Quitar todas las pistas",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _burnAudio.Clear();
        InvalidateCapacity();
        Log("Audio CD: se quitaron todas las pistas de la lista.");
    }

    private void MoveAudioOnlyUp_Click(object sender, RoutedEventArgs e) => MoveAudioOnly(-1);
    private void MoveAudioOnlyDown_Click(object sender, RoutedEventArgs e) => MoveAudioOnly(1);

    private void MoveAudioOnly(int delta)
    {
        int index = AudioOnlyList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _burnAudio.Count) return;
        _burnAudio.Move(index, target);
        AudioOnlyList.SelectedIndex = target;
        BurnAudioList.SelectedIndex = target;
        InvalidateCapacity();
    }

    private bool TryValidateStandaloneBurn(out string tools, out CdrtoolsDevice? device)
    {
        tools = CdrtoolsPathText.Text.Trim();
        device = SelectedCdrtoolsDevice;
        if (!CdExtraBurner.IsValidToolsDirectory(tools))
        {
            Info("No se encontró una instalación válida de cdrtools. Revisa Opciones avanzadas en Crear CD Extra.");
            return false;
        }
        if (device is null)
        {
            Info("No se detectó una grabadora con cdrtools. Vuelve a abrir el programa con la grabadora conectada.");
            return false;
        }
        return true;
    }

    private bool TryGetBurnSpeed(out int? speed)
    {
        speed = null;
        string raw = (BurnSpeedCombo.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("Automática", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Automatica", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        raw = raw.TrimEnd('x', 'X').Trim();
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > 99)
        {
            Info("Velocidad no válida. Elige Automática o escribe un número entre 1 y 99, por ejemplo 4, 8, 12 o 16.");
            return false;
        }

        speed = parsed;
        return true;
    }

    private string BurnSpeedDisplay()
    {
        return TryGetBurnSpeedSilently(out int? speed)
            ? (!speed.HasValue ? "Automática" : $"{speed.Value}x manual")
            : "No válida";
    }

    private bool TryGetBurnSpeedSilently(out int? speed)
    {
        speed = null;
        string raw = (BurnSpeedCombo.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("Automática", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Automatica", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return true;

        raw = raw.TrimEnd('x', 'X').Trim();
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > 99)
            return false;

        speed = parsed;
        return true;
    }

    private static bool AudioProjectNeedsFfmpeg(IEnumerable<string> audioFiles)
    {
        // Los WAV comunes pueden pasar por el normalizador interno.
        // El resto usa FFmpeg como ruta universal.
        return audioFiles.Any(path =>
            !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> EnsureFfmpegForAudioBurnAsync(IEnumerable<string> audioFiles)
    {
        var files = audioFiles.Where(File.Exists).ToList();
        if (!AudioProjectNeedsFfmpeg(files))
            return true;

        string tools = CdrtoolsPathText.Text.Trim();
        string? ffmpeg = AudioConversionService.FindFfmpeg(tools);

        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            FfmpegPathText.Text = ffmpeg;
            RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
            return true;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            "El proyecto contiene audio que necesita FFmpeg para convertirse a CD Audio.\n\n" +
            "FFmpeg no está disponible.\n\n" +
            "¿Quieres instalar FFmpeg + ffprobe automáticamente y continuar?",
            "Falta FFmpeg",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return false;

        bool installed = await InstallFfmpegAsync(showErrors: true);
        if (!installed)
            return false;

        ffmpeg = AudioConversionService.FindFfmpeg(tools);
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            Info("FFmpeg se instaló, pero dvdrtool todavía no puede localizar ffmpeg.exe. Cierra y abre el programa o selecciónalo manualmente.");
            return false;
        }

        FfmpegPathText.Text = ffmpeg;
        RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
        Log($"FFmpeg preparado antes de grabar: {ffmpeg}");
        return true;
    }

    private async Task<(long sectors, int unknown)> EstimateAudioSectorsForSimulationAsync(
        IReadOnlyList<string> audioFiles,
        IProgress<(double progress, string status)> progress,
        CancellationToken token)
    {
        long sectors = 0;
        int unknown = 0;
        string ffmpeg = FfmpegPathText.Text.Trim();

        for (int i = 0; i < audioFiles.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string file = audioFiles[i];
            var measured = await AudioDurationEstimator.TryGetCddaSectorCountAsync(file, ffmpeg, token);
            if (measured.Success)
            {
                sectors += measured.Sectors;
                Log($"SIMULACIÓN | {Path.GetFileName(file)} = {measured.Detail}");
            }
            else
            {
                unknown++;
                Log($"SIMULACIÓN | No se pudo medir {Path.GetFileName(file)}: {measured.Detail}");
            }

            progress.Report(((i + 1) / (double)Math.Max(1, audioFiles.Count),
                $"Simulando audio {i + 1}/{audioFiles.Count}…"));
        }

        return (sectors, unknown);
    }

    private static long CalculateDirectoryBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { }
        }
        return total;
    }

    private bool HasBluRayIso =>
        !string.IsNullOrWhiteSpace(_bluRayIsoPath) && File.Exists(_bluRayIsoPath);

    private bool HasBluRayFolder =>
        !string.IsNullOrWhiteSpace(_bluRayFolderPath) && Directory.Exists(_bluRayFolderPath);

    private void SelectBluRayIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imagen Blu-ray / ISO (*.iso)|*.iso|Todos los archivos (*.*)|*.*",
            Multiselect = false,
            Title = "Selecciona una imagen Blu-ray Video ISO"
        };
        if (dlg.ShowDialog(this) != true) return;

        _bluRayIsoPath = dlg.FileName;
        _bluRayFolderPath = null;
        UpdateBluRaySourceUi();
        Log($"Blu-ray: ISO seleccionada: {_bluRayIsoPath}");
    }

    private void SelectBluRayFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Selecciona la carpeta raíz Blu-ray o la carpeta BDMV"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _bluRayFolderPath = BluRayImageBuilder.NormalizeBluRayRoot(dlg.FolderName);
            _bluRayIsoPath = null;
            UpdateBluRaySourceUi();
            Log($"Blu-ray: carpeta seleccionada: {_bluRayFolderPath}");
        }
        catch (Exception ex)
        {
            _bluRayFolderPath = null;
            UpdateBluRaySourceUi();
            Info(ex.Message);
        }
    }

    private void ClearBluRaySource_Click(object sender, RoutedEventArgs e)
    {
        _bluRayIsoPath = null;
        _bluRayFolderPath = null;
        UpdateBluRaySourceUi();
        Log("Blu-ray: origen quitado.");
    }

    private void UpdateBluRaySourceUi()
    {
        if (BluRaySourcePathText is null ||
            BluRaySourceStatusText is null ||
            BluRaySourceSizeText is null ||
            BluRaySourceTypeText is null) return;

        if (HasBluRayIso)
        {
            var info = new FileInfo(_bluRayIsoPath!);
            BluRaySourcePathText.Text = _bluRayIsoPath!;
            BluRaySourceSizeText.Text = FormatBytes(info.Length);
            BluRaySourceTypeText.Text = "ISO";

            if (!Path.GetExtension(_bluRayIsoPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                BluRaySourceStatusText.Text = "Advertencia: el archivo no tiene extensión .ISO.";
            else if (info.Length < 32L * 1024 * 1024)
                BluRaySourceStatusText.Text = "La ISO parece muy pequeña; comprueba que sea una imagen Blu-ray válida.";
            else
                BluRaySourceStatusText.Text = "ISO lista. Se grabará sin modificar su contenido.";

            UpdateBluRayFitIndicator();
            return;
        }

        if (HasBluRayFolder)
        {
            try
            {
                string root = BluRayImageBuilder.NormalizeBluRayRoot(_bluRayFolderPath!);
                long bytes = BluRayImageBuilder.CalculateDirectoryBytes(root);
                bool certificate = BluRayImageBuilder.HasCertificateFolder(root);

                BluRaySourcePathText.Text = root;
                BluRaySourceSizeText.Text = FormatBytes(bytes);
                BluRaySourceTypeText.Text = "CARPETA BDMV";
                BluRaySourceStatusText.Text = certificate
                    ? "Estructura BDMV + CERTIFICATE detectada. Se creará UDF 2.50 temporal."
                    : "Estructura BDMV detectada. CERTIFICATE no está presente (es opcional).";
            }
            catch (Exception ex)
            {
                BluRaySourcePathText.Text = _bluRayFolderPath!;
                BluRaySourceSizeText.Text = "—";
                BluRaySourceTypeText.Text = "CARPETA";
                BluRaySourceStatusText.Text = ex.Message.Replace("\n", " ");
            }

            UpdateBluRayFitIndicator();
            return;
        }

        BluRaySourcePathText.Text = "Ningún origen seleccionado";
        BluRaySourceStatusText.Text = "Selecciona una ISO o una carpeta Blu-ray con BDMV.";
        BluRaySourceSizeText.Text = "—";
        BluRaySourceTypeText.Text = "SIN SELECCIÓN";
        UpdateBluRayFitIndicator();
    }

    private void BluRaySimulationMediaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBluRayFitIndicator();
    }

    private void UpdateBluRayFitIndicator()
    {
        if (BluRayFitText is null) return;
        var medium = GetBluRaySimulationMedium();
        long size = 0;
        bool hasSource = false;
        try
        {
            if (HasBluRayIso)
            {
                size = new FileInfo(_bluRayIsoPath!).Length;
                hasSource = true;
            }
            else if (HasBluRayFolder)
            {
                string root = BluRayImageBuilder.NormalizeBluRayRoot(_bluRayFolderPath!);
                long raw = BluRayImageBuilder.CalculateDirectoryBytes(root);
                long overhead = Math.Max(128L * 1024 * 1024, raw / 100);
                size = raw + overhead;
                hasSource = true;
            }
        }
        catch { }

        if (!hasSource)
        {
            BluRayFitText.Text = "SIN MEDIO";
            BluRayFitText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B5FD7"));
            return;
        }

        bool fits = size <= medium.capacity;
        BluRayFitText.Text = fits
            ? $"CABE · {(medium.capacity == SimulatedBd50Bytes ? "50 GB" : "25 GB")}"
            : $"EXCEDE · {(medium.capacity == SimulatedBd50Bytes ? "50 GB" : "25 GB")}";
        BluRayFitText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fits ? "#15803D" : "#B91C1C"));
    }

    private (long capacity, string name) GetBluRaySimulationMedium()
    {
        bool dualLayer = BluRaySimulationMediaCombo.SelectedIndex == 1;
        return dualLayer
            ? (SimulatedBd50Bytes, "BD-R DL / BD-RE DL 50 GB")
            : (SimulatedBd25Bytes, "BD-R / BD-RE 25 GB");
    }

    private async Task RunBluRaySimulationAsync(int? writeSpeed)
    {
        await WithBusyAsync(async token =>
        {
            Log("=== SIMULACIÓN BLU-RAY VIDEO · NO SE ESCRIBIRÁ NINGÚN DISCO ===");
            SetStatus("Simulando Blu-ray Video…", 20);

            var medium = GetBluRaySimulationMedium();
            long size;
            string sourceText;
            string commandText;

            if (HasBluRayIso)
            {
                size = new FileInfo(_bluRayIsoPath!).Length;
                sourceText = $"ISO: {_bluRayIsoPath}";
                commandText =
                    $"cdrecord ... {(!writeSpeed.HasValue ? "" : $"speed={writeSpeed.Value} ")}-dao -data \"{_bluRayIsoPath}\"";
            }
            else if (HasBluRayFolder)
            {
                string root = BluRayImageBuilder.NormalizeBluRayRoot(_bluRayFolderPath!);
                SetStatus("Calculando carpeta Blu-ray…", 35);
                long raw = await Task.Run(() => BluRayImageBuilder.CalculateDirectoryBytes(root), token);

                // La imagen UDF añade metadatos y alineación. En simulación usamos
                // un pequeño margen para no crear una ISO temporal de decenas de GB.
                long overhead = Math.Max(128L * 1024 * 1024, raw / 100);
                size = raw + overhead;
                sourceText = $"Carpeta BDMV: {root}";
                commandText =
                    $"IMAPI2FS UDF 2.50 -> <imagen temporal> -> cdrecord ... {(!writeSpeed.HasValue ? "" : $"speed={writeSpeed.Value} ")}-dao -data <imagen temporal>";
            }
            else
            {
                throw new InvalidOperationException("Selecciona una ISO o una carpeta Blu-ray.");
            }

            bool fits = size <= medium.capacity;

            Log($"SIMULACIÓN | Origen: {sourceText}");
            Log($"SIMULACIÓN | Tamaño estimado: {FormatBytes(size)}");
            Log($"SIMULACIÓN | Medio: {medium.name}");
            Log($"SIMULACIÓN | Capacidad nominal: {FormatBytes(medium.capacity)}");
            Log($"SIMULACIÓN | Velocidad solicitada: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x")}");
            Log($"SIMULACIÓN | Flujo previsto: {commandText}");
            Log("SIMULACIÓN | No se creó una imagen grande ni se ejecutó cdrecord.");

            SetStatus("Simulación Blu-ray completada", 100);
            MessageBox.Show(
                this,
                $"Simulación terminada.\n\nOrigen: {(HasBluRayIso ? "ISO" : "Carpeta BDMV")}\nTamaño estimado: {FormatBytes(size)}\nMedio: {medium.name}\nResultado: {(fits ? "CABE" : "EXCEDE")}\n\nNo se escribió ningún disco.",
                "Simulación Blu-ray Video",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Simulación Blu-ray Video");
    }

    private async void BurnBluRay_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!HasBluRayIso && !HasBluRayFolder)
        {
            Info("Selecciona primero una imagen ISO o una carpeta Blu-ray con BDMV.");
            return;
        }

        if (HasBluRayIso &&
            !Path.GetExtension(_bluRayIsoPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            Info("La imagen seleccionada debe tener extensión .ISO.");
            return;
        }

        if (!TryGetBurnSpeed(out int? writeSpeed)) return;

        if (IsSimulationMode)
        {
            await RunBluRaySimulationAsync(writeSpeed);
            return;
        }

        if (!TryValidateStandaloneBurn(out string tools, out CdrtoolsDevice? device)) return;

        string sourceLabel;
        long bytes;
        if (HasBluRayIso)
        {
            sourceLabel = $"ISO: {Path.GetFileName(_bluRayIsoPath)}";
            bytes = new FileInfo(_bluRayIsoPath!).Length;
        }
        else
        {
            string root = BluRayImageBuilder.NormalizeBluRayRoot(_bluRayFolderPath!);
            sourceLabel = $"Carpeta: {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))}";
            bytes = BluRayImageBuilder.CalculateDirectoryBytes(root);
        }

        if (ShouldConfirmBeforeBurn)
        {
            string extra = HasBluRayFolder
                ? $"\nEtiqueta: {(string.IsNullOrWhiteSpace(BluRayVolumeNameTextBox.Text) ? "BLURAY_VIDEO" : BluRayVolumeNameTextBox.Text.Trim())}\nSe creará primero una imagen temporal UDF 2.50 en el disco duro."
                : "\nLa ISO se grabará tal como está.";

            var confirm = MessageBox.Show(
                this,
                $"Se grabará el Blu-ray seleccionado.\n\n{sourceLabel}\nTamaño: {FormatBytes(bytes)}\nGrabadora: {device}\nVelocidad: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x manual")}{extra}\n\n¿Continuar?",
                "Grabar película Blu-ray",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        string? isoPath = _bluRayIsoPath;
        string? folderPath = _bluRayFolderPath;

        await WithBusyAsync(async token =>
        {
            var progress = new Progress<(double progress, string status)>(p =>
            {
                SetStatus(p.status, p.progress * 100);
                Log(p.status);
            });
            var toolLog = new Progress<string>(LogCdrtoolsLine);

            if (!string.IsNullOrWhiteSpace(isoPath))
            {
                await _burner.BurnBluRayIsoAsync(
                    tools,
                    device!.DeviceSpec,
                    isoPath,
                    writeSpeed,
                    progress,
                    toolLog,
                    token);
            }
            else
            {
                await _burner.BurnBluRayFolderAsync(
                    tools,
                    device!.DeviceSpec,
                    folderPath!,
                    string.IsNullOrWhiteSpace(BluRayVolumeNameTextBox.Text) ? "BLURAY_VIDEO" : BluRayVolumeNameTextBox.Text.Trim(),
                    writeSpeed,
                    progress,
                    toolLog,
                    token);
            }

            MessageBox.Show(
                this,
                "Blu-ray grabado correctamente.",
                "dvdrtool",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Grabación Blu-ray Video");
    }

    private async Task RunAudioSimulationAsync(int? writeSpeed)
    {
        var audioFiles = _burnAudio.ToList();
        await WithBusyAsync(async token =>
        {
            Log("=== SIMULACIÓN AUDIO CD · NO SE ESCRIBIRÁ NINGÚN DISCO ===");
            SetStatus("Simulando Audio CD…", 5);

            var progress = new Progress<(double progress, string status)>(p =>
                SetStatus(p.status, 5 + p.progress * 80));

            var estimate = await EstimateAudioSectorsForSimulationAsync(audioFiles, progress, token);
            long required = estimate.sectors + SimulationSafetySectors;
            bool fits = estimate.unknown == 0 && required <= SimulatedCdSectors;

            string artist = CdTextEnabled ? AudioOnlyArtistText.Text.Trim() : "";
            string title = CdTextEnabled ? AudioOnlyAlbumText.Text.Trim() : "";
            bool cdText = !string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title);

            Log($"SIMULACIÓN | Pistas: {audioFiles.Count}");
            Log($"SIMULACIÓN | Audio estimado: {DiscCapacityInfo.FormatCdTime(estimate.sectors)}");
            Log($"SIMULACIÓN | Velocidad solicitada: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x")}");
            Log($"SIMULACIÓN | CD-TEXT: {(cdText ? $"{artist} · {title}" : "desactivado")}");
            Log($"SIMULACIÓN | Comando previsto: cdrecord ... {(!writeSpeed.HasValue ? "" : $"speed={writeSpeed.Value} ")}{(cdText ? "-dao -pad -useinfo -text " : "-tao -pad ")}-audio <pistas WAV>");
            Log("SIMULACIÓN | cdrecord NO fue ejecutado.");

            SetStatus("Simulación de Audio CD completada", 100);
            string result = estimate.unknown > 0
                ? $"Simulación terminada.\n\nNo se pudo medir {estimate.unknown} pista(s), por lo que la capacidad no es definitiva."
                : $"Simulación terminada.\n\nAudio: {DiscCapacityInfo.FormatCdTime(estimate.sectors)}\nCD-R 80 min: {(fits ? "CABE" : "EXCEDE")}\n\nNo se escribió ningún disco.";
            MessageBox.Show(this, result, "Simulación de Audio CD", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Simulación de Audio CD");
    }

    private async Task RunDataSimulationAsync(int? writeSpeed)
    {
        await WithBusyAsync(async token =>
        {
            Log("=== SIMULACIÓN DATOS CD/DVD · NO SE ESCRIBIRÁ NINGÚN DISCO ===");
            SetStatus("Simulando disco de datos…", 20);
            await Task.Yield();
            token.ThrowIfCancellationRequested();

            long bytes = CalculateDirectoryBytes(_dataOnlyProjectRoot);
            long estimatedIsoBytes = bytes + Math.Max(2L * 1024 * 1024, bytes / 100);
            int mediaIndex = Math.Clamp(SimulationMediaCombo.SelectedIndex, 0, 2);
            string mediaName;
            long capacity;

            switch (mediaIndex)
            {
                case 2:
                    mediaName = "DVD doble capa 8.5 GB (DVD±R DL)";
                    capacity = SimulatedDvdDlBytes;
                    break;
                case 1:
                    mediaName = "DVD 4.7 GB";
                    capacity = SimulatedDvdBytes;
                    break;
                default:
                    mediaName = "CD-R 80 min / 700 MB";
                    capacity = SimulatedCdSectors * 2048L;
                    break;
            }

            bool fits = estimatedIsoBytes <= capacity;

            string volume = string.IsNullOrWhiteSpace(DataOnlyVolumeLabelText.Text) ? "DVDRTOOL" : DataOnlyVolumeLabelText.Text.Trim();
            Log($"SIMULACIÓN | Datos reales: {FormatBytes(bytes)}");
            Log($"SIMULACIÓN | ISO estimada: {FormatBytes(estimatedIsoBytes)}");
            Log($"SIMULACIÓN | Medio: {mediaName}");
            Log($"SIMULACIÓN | Capacidad nominal: {FormatBytes(capacity)}");
            Log($"SIMULACIÓN | Etiqueta: {volume}");
            Log($"SIMULACIÓN | Velocidad solicitada: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x")}");
            Log($"SIMULACIÓN | Comando previsto: mkisofs -J -R -iso-level 3 -V \"{volume}\" -o <imagen.iso> <proyecto>");
            Log($"SIMULACIÓN | Comando previsto: cdrecord ... {(!writeSpeed.HasValue ? "" : $"speed={writeSpeed.Value} ")}-data <imagen.iso>");
            Log("SIMULACIÓN | mkisofs y cdrecord NO fueron ejecutados.");

            SetStatus("Simulación de datos completada", 100);
            MessageBox.Show(
                this,
                $"Simulación terminada.\n\nDatos: {FormatBytes(bytes)}\nISO estimada: {FormatBytes(estimatedIsoBytes)}\nMedio: {mediaName}\nResultado: {(fits ? "CABE" : "EXCEDE")}\n\nNo se escribió ningún disco.",
                "Simulación de Datos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Simulación de Datos");
    }

    private async Task RunCdExtraSimulationAsync(int? writeSpeed)
    {
        var audioFiles = _burnAudio.ToList();
        await WithBusyAsync(async token =>
        {
            Log("=== SIMULACIÓN CD EXTRA · NO SE ESCRIBIRÁ NINGÚN DISCO ===");
            SetStatus("Simulando CD Extra…", 5);

            var progress = new Progress<(double progress, string status)>(p =>
                SetStatus(p.status, 5 + p.progress * 60));
            var audio = await EstimateAudioSectorsForSimulationAsync(audioFiles, progress, token);

            long dataBytes = CalculateDirectoryBytes(_dataProjectRoot);
            long dataSectors = (dataBytes + 2047L) / 2048L;
            long total = audio.sectors + dataSectors + SimulationMultisessionSectors + SimulationSafetySectors;
            bool fits = audio.unknown == 0 && total <= SimulatedCdSectors;

            string artist = CdTextEnabled ? ExtraArtistText.Text.Trim() : "";
            string title = CdTextEnabled ? ExtraAlbumText.Text.Trim() : "";
            bool cdText = !string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title);

            Log($"SIMULACIÓN | Audio CD: {DiscCapacityInfo.FormatCdTime(audio.sectors)}");
            Log($"SIMULACIÓN | Datos: {FormatBytes(dataBytes)}");
            Log($"SIMULACIÓN | Margen multisesión: {SimulationMultisessionSectors} sectores");
            Log($"SIMULACIÓN | Margen de seguridad: {SimulationSafetySectors} sectores");
            Log($"SIMULACIÓN | Total estimado: {DiscCapacityInfo.FormatCdTime(total)} equivalentes");
            Log($"SIMULACIÓN | Velocidad solicitada: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x")}");
            Log($"SIMULACIÓN | CD-TEXT: {(cdText ? $"{artist} · {title}" : "desactivado")}");
            Log($"SIMULACIÓN | Sesión 1 prevista: cdrecord ... -multi {(cdText ? "-dao -pad -useinfo -text " : "-tao -pad ")}-audio <pistas WAV>");
            Log("SIMULACIÓN | Sesión 2 prevista: mkisofs ... -C <msinfo> ... && cdrecord ... -data <imagen.iso>");
            Log("SIMULACIÓN | cdrecord y mkisofs NO fueron ejecutados.");

            SetStatus("Simulación de CD Extra completada", 100);
            string capacityText = audio.unknown > 0
                ? $"No se pudo medir {audio.unknown} pista(s); la capacidad no es definitiva."
                : $"Resultado para CD-R 80 min: {(fits ? "CABE" : "EXCEDE")}.";
            MessageBox.Show(
                this,
                $"Simulación terminada.\n\nAudio: {DiscCapacityInfo.FormatCdTime(audio.sectors)}\nDatos: {FormatBytes(dataBytes)}\n{capacityText}\n\nNo se escribió ningún disco.",
                "Simulación de CD Extra",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Simulación de CD Extra");
    }

    private async void BurnAudioOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_burnAudio.Count == 0)
        {
            Info("Agrega al menos una pista para grabar un Audio CD.");
            return;
        }
        if (!TryGetBurnSpeed(out int? writeSpeed)) return;

        if (IsSimulationMode)
        {
            await RunAudioSimulationAsync(writeSpeed);
            return;
        }

        if (!TryValidateStandaloneBurn(out string tools, out CdrtoolsDevice? device)) return;
        if (!await EnsureFfmpegForAudioBurnAsync(_burnAudio.ToList())) return;

        string albumArtist = CdTextEnabled ? AudioOnlyArtistText.Text.Trim() : "";
        string albumTitle = CdTextEnabled ? AudioOnlyAlbumText.Text.Trim() : "";
        string cdTextInfo = string.IsNullOrWhiteSpace(albumArtist) && string.IsNullOrWhiteSpace(albumTitle)
            ? "CD-TEXT: sin etiquetas"
            : $"CD-TEXT: {albumArtist} · {albumTitle}";

        if (ShouldConfirmBeforeBurn)
        {
            var confirm = MessageBox.Show(
                this,
                $"Se grabarán {_burnAudio.Count} pista(s) como Audio CD tradicional.\n\nNo se grabará ninguna sesión de datos.\nGrabadora: {device}\nVelocidad: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x manual")}\n{cdTextInfo}\n\n¿Continuar?",
                "Grabar solamente Audio CD",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var audioFiles = _burnAudio.ToList();
        await WithBusyAsync(async token =>
        {
            var progress = new Progress<(double progress, string status)>(p =>
            {
                SetStatus(p.status, p.progress * 100);
                Log(p.status);
            });
            var toolLog = new Progress<string>(LogCdrtoolsLine);
            await _burner.BurnAudioOnlyAsync(
                tools,
                device!.DeviceSpec,
                audioFiles,
                FfmpegPathText.Text.Trim(),
                albumArtist,
                albumTitle,
                writeSpeed,
                progress,
                toolLog,
                token);

            MessageBox.Show(this, "Audio CD grabado correctamente.", "dvdrtool", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Grabación de Audio CD");
    }

    private void AddDataOnlyFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Agregar archivos al disco de datos",
            Filter = "Todos los archivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        string targetDirectory = GetDataOnlyTargetDirectory();
        Directory.CreateDirectory(targetDirectory);
        int added = 0;
        foreach (string source in dlg.FileNames)
        {
            string destination = Path.Combine(targetDirectory, Path.GetFileName(source));
            if (!ConfirmOverwriteIfNeeded(destination)) continue;
            File.Copy(source, destination, overwrite: true);
            added++;
        }

        RefreshDataOnlyList();
        Log($"Datos CD/DVD: se agregaron {added} archivo(s) a {GetDataOnlyRelativePath(targetDirectory)}.");
    }

    private void AddDataOnlyFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Agregar carpeta al disco de datos" };
        if (dlg.ShowDialog(this) != true) return;

        string source = Path.GetFullPath(dlg.FolderName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (source.StartsWith(Path.GetFullPath(_dataOnlyProjectRoot), StringComparison.OrdinalIgnoreCase))
        {
            Info("Esa carpeta ya pertenece al proyecto de Datos CD/DVD.");
            return;
        }

        string targetDirectory = GetDataOnlyTargetDirectory();
        string folderName = new DirectoryInfo(source).Name;
        string destination = Path.Combine(targetDirectory, folderName);
        if (Directory.Exists(destination))
        {
            var result = MessageBox.Show(
                this,
                $"La carpeta '{folderName}' ya existe. ¿Combinarla y reemplazar archivos con el mismo nombre?",
                "Agregar carpeta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        CopyDirectory(source, destination);
        RefreshDataOnlyList();
        Log($"Datos CD/DVD: carpeta agregada: {folderName}.");
    }

    private void RemoveDataOnlyItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataOnlyGrid.SelectedItem is not DataProjectEntry item)
        {
            Info("Selecciona un archivo o carpeta.");
            return;
        }

        var result = MessageBox.Show(
            this,
            $"¿Quitar '{item.RelativePath}' del proyecto de datos?\n\nEl original no se modificará.",
            "Quitar del disco de datos",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (item.IsDirectory && Directory.Exists(item.FullPath)) Directory.Delete(item.FullPath, recursive: true);
        else if (File.Exists(item.FullPath)) File.Delete(item.FullPath);
        RefreshDataOnlyList();
    }

    private void ClearDataOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_dataOnly.Count == 0) return;
        var result = MessageBox.Show(
            this,
            "¿Vaciar por completo el proyecto de Datos CD/DVD?\n\nSe quitarán todos los archivos y carpetas del proyecto temporal. Los originales no se modificarán.",
            "Vaciar Datos CD/DVD",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        ClearDirectoryContents(_dataOnlyProjectRoot);
        RefreshDataOnlyList();
        Log("Datos CD/DVD: proyecto vaciado completamente.");
    }

    private async void BurnDataOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_dataOnly.Any(item => !item.IsDirectory))
        {
            Info("Agrega al menos un archivo antes de grabar el disco de datos.");
            return;
        }
        if (!TryGetBurnSpeed(out int? writeSpeed)) return;

        if (IsSimulationMode)
        {
            await RunDataSimulationAsync(writeSpeed);
            return;
        }

        if (!TryValidateStandaloneBurn(out string tools, out CdrtoolsDevice? device)) return;

        if (ShouldConfirmBeforeBurn)
        {
            var confirm = MessageBox.Show(
                this,
                $"Se grabarán solamente los archivos y carpetas que aparecen en Datos CD/DVD.\n\nNo se crearán carpetas AUDIO, CDPLUS ni PICTURES automáticamente.\nGrabadora: {device}\nVelocidad: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x manual")}\n\n¿Continuar?",
                "Grabar Datos CD/DVD",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        string volume = string.IsNullOrWhiteSpace(DataOnlyVolumeLabelText.Text) ? "DVDRTOOL" : DataOnlyVolumeLabelText.Text;
        await WithBusyAsync(async token =>
        {
            var progress = new Progress<(double progress, string status)>(p =>
            {
                SetStatus(p.status, p.progress * 100);
                Log(p.status);
            });
            var toolLog = new Progress<string>(LogCdrtoolsLine);
            await _burner.BurnDataOnlyAsync(
                tools,
                device!.DeviceSpec,
                _dataOnlyProjectRoot,
                volume,
                writeSpeed,
                progress,
                toolLog,
                token);

            MessageBox.Show(this, "Disco de datos grabado correctamente.", "dvdrtool", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Grabación de datos CD/DVD");
    }

    private void AddBurnAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = AudioConversionService.OpenFileFilter,
            Multiselect = true,
            Title = "Agregar pistas de audio"
        };
        if (dlg.ShowDialog(this) != true) return;

        int added = 0;
        int unsupported = 0;
        foreach (var file in dlg.FileNames)
        {
            if (!AudioConversionService.IsSupportedSource(file))
            {
                unsupported++;
                continue;
            }
            if (_burnAudio.Contains(file) || _burnCompressedAudio.Any(item => string.Equals(item.SourcePath, file, StringComparison.OrdinalIgnoreCase))) continue;
            _burnAudio.Add(file);
            added++;
        }

        Log($"Se agregaron {added} pista(s) de audio a la cola de grabación.");
        if (added > 0) InvalidateCapacity();
        if (unsupported > 0) Info($"Se omitieron {unsupported} archivo(s) con formato no compatible.");
    }

    private async Task InitializeFfmpegAsync()
    {
        string? detected = AudioConversionService.FindFfmpeg(CdrtoolsPathText.Text.Trim());
        if (!string.IsNullOrWhiteSpace(detected))
        {
            FfmpegPathText.Text = detected;
            RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
            Log($"FFmpeg detectado: {detected}");
            return;
        }

        FfmpegPathText.Text = "";
        RefreshAudioSupportStatus(keepSelectedFfmpeg: true);

        MessageBoxResult answer = MessageBox.Show(
            this,
            "Para que dvdrtool pueda convertir FLAC, MP3, M4A/AAC, WMA, Opus, OGG, APE, AIFF y cualquier otro formato que FFmpeg pueda decodificar, se recomienda instalar FFmpeg dentro del programa.\n\n" +
            "¿Quieres descargar e instalar ahora FFmpeg + ffprobe automáticamente?\n\n" +
            "Se descarga el build release essentials para Windows, se verifica su SHA-256 publicado y se guarda en tools\\ffmpeg.",
            "Instalar soporte universal de audio",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            await InstallFfmpegAsync(showErrors: true);
    }

    private async Task<bool> InstallFfmpegAsync(bool showErrors)
    {
        try
        {
            SetStatus("Instalando soporte universal de audio…", 15);
            AudioSupportText.Text = "Descargando FFmpeg…";

            var installProgress = new Progress<string>(message =>
            {
                AudioSupportText.Text = message;
                Log(message);
            });

            string ffmpeg = await FfmpegBootstrapper.InstallAsync(installProgress);
            FfmpegPathText.Text = ffmpeg;
            RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
            Log($"FFmpeg instalado localmente: {ffmpeg}");
            SetStatus("FFmpeg listo", 100);
            return true;
        }
        catch (Exception ex)
        {
            AudioSupportText.Text = "No se pudo instalar FFmpeg automáticamente.";
            SetStatus("Error instalando FFmpeg", 0, error: true);
            Log($"Error al instalar FFmpeg: {ex}");
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    "No se pudo descargar o instalar FFmpeg automáticamente.\n\n" +
                    ex.Message +
                    "\n\nPuedes volver a intentarlo o seleccionar ffmpeg.exe manualmente.",
                    "FFmpeg",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return false;
        }
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (FfmpegBootstrapper.IsLocalInstallComplete())
        {
            FfmpegPathText.Text = FfmpegBootstrapper.LocalFfmpegPath;
            RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
            Info("FFmpeg ya está instalado dentro de dvdrtool.");
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            "¿Descargar FFmpeg + ffprobe y guardarlos dentro de dvdrtool?\n\n" +
            "La descarga se verificará con el SHA-256 publicado por el proveedor antes de extraerla.",
            "Instalar FFmpeg",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await InstallFfmpegAsync(showErrors: true);
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|Ejecutables (*.exe)|*.exe",
            Title = "Selecciona ffmpeg.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        if (!string.Equals(Path.GetFileName(dlg.FileName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            Info("Selecciona el ejecutable ffmpeg.exe.");
            return;
        }

        FfmpegPathText.Text = dlg.FileName;
        InvalidateCapacity();
        Log($"FFmpeg seleccionado: {dlg.FileName}");
        RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
    }

    private void ClearFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        FfmpegPathText.Text = "";
        InvalidateCapacity();
        RefreshAudioSupportStatus(keepSelectedFfmpeg: true);
    }

    private void RefreshAudioSupportStatus(bool keepSelectedFfmpeg = false)
    {
        string tools = CdrtoolsPathText.Text.Trim();
        if (!keepSelectedFfmpeg)
        {
            string? detected = AudioConversionService.FindFfmpeg(tools);
            FfmpegPathText.Text = detected ?? "";
        }

        bool ffmpeg = File.Exists(FfmpegPathText.Text.Trim());
        var nativeFormats = AudioConversionService.GetAvailableCdrtfeFormats(tools);

        AudioSupportText.Text = ffmpeg
            ? "FFmpeg activo (conversor principal): cualquier audio que FFmpeg pueda decodificar se convierte automáticamente a CD Audio 44,1 kHz / 16-bit / estéreo."
            : nativeFormats.Count > 1
                ? $"Soporte detectado con herramientas de audio: {string.Join(", ", nativeFormats)}. Puedes agregar otros formatos; si Windows puede leer su duración se podrán comprobar, y FFmpeg añade la conversión universal al grabar."
                : "FFmpeg no está instalado. Instálalo automáticamente para obtener soporte universal de audio; los decodificadores antiguos quedan solo como respaldo.";
    }

    private void RemoveBurnAudio_Click(object sender, RoutedEventArgs e)
    {
        if (BurnAudioList.SelectedItem is not string file) return;
        _burnAudio.Remove(file);
        InvalidateCapacity();
    }

    private string GetSelectedDataAudioFormat()
    {
        if (DataAudioFormatCombo.SelectedItem is ComboBoxItem item)
            return AudioConversionService.NormalizeDataAudioFormat(Convert.ToString(item.Content));
        return "Original";
    }

    private async void MoveAudioToMp3_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (BurnAudioList.SelectedItem is not string source)
        {
            Info("Selecciona una pista de CD Audio para moverla a Audio / Datos.");
            return;
        }

        string format = GetSelectedDataAudioFormat();
        await WithBusyAsync(async token =>
        {
            SetStatus(format == "Original"
                ? $"Copiando a Datos: {Path.GetFileName(source)}"
                : $"Convirtiendo a {format}: {Path.GetFileName(source)}", 20);
            await MoveAudioToDataCoreAsync(source, format, token);
            SetStatus($"Pista movida a Audio / Datos ({format})", 100);
        }, format == "Original" ? "Mover audio a Datos" : $"Conversión a {format}");
    }

    private async Task MoveAudioToDataCoreAsync(string source, string format, CancellationToken cancellationToken)
    {
        if (!_burnAudio.Contains(source)) return;

        Directory.CreateDirectory(DataAudioProjectDirectory);
        string normalizedFormat = AudioConversionService.NormalizeDataAudioFormat(format);
        string target = GetUniqueDataAudioTarget(source, normalizedFormat);
        try
        {
            await _audioConverter.PrepareForDataAudioAsync(
                source,
                target,
                normalizedFormat,
                CdrtoolsPathText.Text.Trim(),
                FfmpegPathText.Text.Trim(),
                new Progress<string>(Log),
                cancellationToken);

            var item = new CompressedAudioItem
            {
                SourcePath = source,
                DataPath = target,
                OutputFormat = normalizedFormat
            };
            _burnAudio.Remove(source);
            _burnCompressedAudio.Add(item);
            RefreshBurnDataList();
            InvalidateCapacity();
            Log($"Audio / Datos: {Path.GetFileName(source)} pasó de CD Audio a {GetProjectRelativePath(target)} ({normalizedFormat}).");
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
    }

    private void MoveMp3ToAudio_Click(object sender, RoutedEventArgs e)
    {
        if (BurnMp3List.SelectedItem is not CompressedAudioItem item)
        {
            Info("Selecciona un archivo de Audio / Datos para devolverlo a CD Audio.");
            return;
        }

        if (!File.Exists(item.SourcePath))
        {
            Info("El archivo original ya no existe. No se puede devolver a CD Audio.");
            return;
        }

        TryDeleteGeneratedDataAudio(item);
        _burnCompressedAudio.Remove(item);
        if (!_burnAudio.Contains(item.SourcePath)) _burnAudio.Add(item.SourcePath);
        RefreshBurnDataList();
        InvalidateCapacity();
        Log($"Audio / Datos: {Path.GetFileName(item.SourcePath)} volvió a CD Audio.");
    }

    private void RemoveMp3Audio_Click(object sender, RoutedEventArgs e)
    {
        if (BurnMp3List.SelectedItem is not CompressedAudioItem item) return;
        TryDeleteGeneratedDataAudio(item);
        _burnCompressedAudio.Remove(item);
        RefreshBurnDataList();
        InvalidateCapacity();
        Log($"Audio / Datos: se quitó {Path.GetFileName(item.DataPath)} del proyecto.");
    }

    private async void AutoFitMp3_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryValidateBurnProject(out string tools, out CdrtoolsDevice? cdrDevice, out string dataFolder)) return;
        if (_burnAudio.Count <= 1)
        {
            Info("Debe quedar al menos una pista de CD Audio para mantener el disco como CD Extra.");
            return;
        }

        string selectedFormat = GetSelectedDataAudioFormat();
        await WithBusyAsync(async token =>
        {
            var toolLog = new Progress<string>(LogCdrtoolsLine);
            int moved = 0;

            DiscCapacityInfo info = _lastCapacity ?? await _burner.AnalyzeCapacityAsync(
                tools, cdrDevice!.DeviceSpec, _burnAudio.ToList(), dataFolder, VolumeLabelText.Text,
                FfmpegPathText.Text.Trim(), null, toolLog, token);
            _lastCapacity = info;
            UpdateCapacityUi(info);

            while (info.IsKnown && !info.Fits && _burnAudio.Count > 1)
            {
                string source = _burnAudio[^1];
                SetStatus($"Ajustando: pasando {Path.GetFileName(source)} a Datos ({selectedFormat})…", 25 + Math.Min(55, moved * 8));
                await MoveAudioToDataCoreAsync(source, selectedFormat, token);
                moved++;

                info = await _burner.AnalyzeCapacityAsync(
                    tools, cdrDevice!.DeviceSpec, _burnAudio.ToList(), dataFolder, VolumeLabelText.Text,
                    FfmpegPathText.Text.Trim(), null, toolLog, token);
                _lastCapacity = info;
                UpdateCapacityUi(info);
            }

            if (!info.IsKnown)
            {
                Info("No se pudo leer la capacidad física del disco. Inserta un CD compatible y espera la actualización automática; si continúa, revisa el Registro.");
            }
            else if (info.Fits)
            {
                SetStatus($"Proyecto ajustado: {moved} pista(s) pasaron a Datos", 100);
                Log($"Ajuste automático terminado: {moved} pista(s) movidas a Audio / Datos usando {selectedFormat}.");
            }
            else
            {
                Info("Aun dejando una pista de CD Audio, el proyecto no cabe. Reduce también los archivos de datos, elige un formato de datos más comprimido o usa un disco de mayor capacidad.");
            }
        }, "Ajuste automático a Datos");
    }

    private string GetUniqueDataAudioTarget(string source, string format)
    {
        Directory.CreateDirectory(DataAudioProjectDirectory);
        string baseName = Path.GetFileNameWithoutExtension(source);
        foreach (char invalid in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Pista";

        string extension = AudioConversionService.GetDataAudioExtension(format, source);
        string candidate = Path.Combine(DataAudioProjectDirectory, baseName + extension);
        int suffix = 2;
        while (File.Exists(candidate) || _burnCompressedAudio.Any(item => string.Equals(item.DataPath, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = Path.Combine(DataAudioProjectDirectory, $"{baseName} ({suffix++}){extension}");
        return candidate;
    }

    private static void TryDeleteGeneratedDataAudio(CompressedAudioItem item)
    {
        try
        {
            if (File.Exists(item.DataPath)) File.Delete(item.DataPath);
        }
        catch
        {
            // La actualización de la interfaz no debe fallar por un archivo temporal bloqueado.
        }
    }

    private void RemoveAllBurnAudio_Click(object sender, RoutedEventArgs e)
    {
        int total = _burnAudio.Count + _burnCompressedAudio.Count;
        if (total == 0) return;
        var result = MessageBox.Show(
            this,
            $"¿Quitar las {total} pistas/archivos de audio del proyecto?\n\nSe vaciarán CD Audio y Audio / Datos; los archivos originales no se modifican.",
            "Quitar todo el audio",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        foreach (var item in _burnCompressedAudio.ToList()) TryDeleteGeneratedDataAudio(item);
        _burnCompressedAudio.Clear();
        _burnAudio.Clear();
        RefreshBurnDataList();
        InvalidateCapacity();
        Log("Se quitó todo el audio de CD Audio y Audio / Datos.");
    }

    private void MoveAudioUp_Click(object sender, RoutedEventArgs e) => MoveAudio(-1);
    private void MoveAudioDown_Click(object sender, RoutedEventArgs e) => MoveAudio(1);

    private void MoveAudio(int delta)
    {
        int index = BurnAudioList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _burnAudio.Count) return;
        _burnAudio.Move(index, target);
        BurnAudioList.SelectedIndex = target;
    }

    private void InitializeDataProject()
    {
        Directory.CreateDirectory(_dataProjectRoot);
        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "CDPLUS"));
        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "PICTURES"));
        Directory.CreateDirectory(DataAudioProjectDirectory);
        RefreshBurnDataList();
    }

    private void InitializeDataOnlyProject()
    {
        Directory.CreateDirectory(_dataOnlyProjectRoot);
        RefreshDataOnlyList();
    }

    private string GetDataOnlyTargetDirectory()
    {
        string selected = DataOnlyTargetCombo.SelectedItem as string ?? "Raíz del disco";
        return selected == "Raíz del disco"
            ? _dataOnlyProjectRoot
            : Path.Combine(_dataOnlyProjectRoot, selected.Replace('/', Path.DirectorySeparatorChar));
    }

    private string GetDataOnlyRelativePath(string fullPath)
    {
        string relative = Path.GetRelativePath(_dataOnlyProjectRoot, fullPath);
        return relative == "." ? "Raíz del disco" : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void RefreshDataOnlyList()
    {
        Directory.CreateDirectory(_dataOnlyProjectRoot);
        _dataOnly.Clear();

        var directories = Directory.EnumerateDirectories(_dataOnlyProjectRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => GetDataOnlyRelativePath(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string directory in directories)
        {
            _dataOnly.Add(new DataProjectEntry
            {
                Name = Path.GetFileName(directory),
                FullPath = directory,
                RelativePath = GetDataOnlyRelativePath(directory),
                IsDirectory = true,
                IsProtected = false
            });
        }

        long totalBytes = 0;
        int fileCount = 0;
        foreach (string file in Directory.EnumerateFiles(_dataOnlyProjectRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => GetDataOnlyRelativePath(path), StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            totalBytes += info.Length;
            fileCount++;
            _dataOnly.Add(new DataProjectEntry
            {
                Name = info.Name,
                FullPath = file,
                RelativePath = GetDataOnlyRelativePath(file),
                IsDirectory = false,
                IsProtected = false,
                Size = info.Length
            });
        }

        int folderCount = _dataOnly.Count(item => item.IsDirectory);
        DataOnlySummaryText.Text = $"{fileCount} archivo(s) · {folderCount} carpeta(s) · {FormatBytes(totalBytes)}";

        string previous = DataOnlyTargetCombo.SelectedItem as string ?? "Raíz del disco";
        var targets = new List<string> { "Raíz del disco" };
        targets.AddRange(directories.Select(GetDataOnlyRelativePath));
        DataOnlyTargetCombo.ItemsSource = targets;
        DataOnlyTargetCombo.SelectedItem = targets.FirstOrDefault(item =>
            string.Equals(item, previous, StringComparison.OrdinalIgnoreCase)) ?? "Raíz del disco";
        UpdateStandaloneSummaries();
    }

    private string GetDataTargetDirectory(ComboBox? targetCombo = null)
    {
        string selected = (targetCombo ?? DataTargetCombo).SelectedItem as string ?? "CDPLUS";
        return selected == "Raíz del CD"
            ? _dataProjectRoot
            : Path.Combine(_dataProjectRoot, selected.Replace('/', Path.DirectorySeparatorChar));
    }

    private void AddDataFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Agregar archivos a la sesión de datos",
            Filter = "Todos los archivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        string targetDirectory = GetDataTargetDirectory();
        Directory.CreateDirectory(targetDirectory);
        int added = 0;
        foreach (string source in dlg.FileNames)
        {
            string destination = Path.Combine(targetDirectory, Path.GetFileName(source));
            if (!ConfirmOverwriteIfNeeded(destination)) continue;
            File.Copy(source, destination, overwrite: true);
            added++;
        }

        RefreshBurnDataList();
        if (added > 0)
        {
            InvalidateCapacity();
            Log($"Datos: se agregaron {added} archivo(s) a {GetProjectRelativePath(targetDirectory)}.");
        }
    }

    private void AddDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Agregar una carpeta a la sesión de datos" };
        if (dlg.ShowDialog(this) != true) return;

        string source = Path.GetFullPath(dlg.FolderName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (source.StartsWith(Path.GetFullPath(_dataProjectRoot), StringComparison.OrdinalIgnoreCase))
        {
            Info("Esa carpeta ya pertenece al proyecto de datos.");
            return;
        }

        string targetDirectory = GetDataTargetDirectory();
        string folderName = new DirectoryInfo(source).Name;
        string destination = Path.Combine(targetDirectory, folderName);

        if (Directory.Exists(destination))
        {
            var result = MessageBox.Show(
                this,
                $"La carpeta '{folderName}' ya existe en ese destino. ¿Combinarla y reemplazar los archivos que tengan el mismo nombre?",
                "Agregar carpeta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        CopyDirectory(source, destination);
        RefreshBurnDataList();
        InvalidateCapacity();
        Log($"Datos: carpeta agregada a {GetProjectRelativePath(targetDirectory)}: {folderName}");
    }

    private void RemoveDataItem_Click(object sender, RoutedEventArgs e)
    {
        if (BurnDataGrid.SelectedItem is not DataProjectEntry item)
        {
            Info("Selecciona un archivo o una carpeta de la sesión de datos.");
            return;
        }

        if (item.IsProtected)
        {
            Info(item.RelativePath.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase)
                ? "La carpeta AUDIO y los archivos enviados desde Audio se administran con la lista 'Audio / Datos'. Muévelos de vuelta a CD Audio o usa 'Quitar de datos'."
                : "CDPLUS y PICTURES son carpetas predeterminadas del proyecto. Puedes quitar su contenido con 'Vaciar datos', pero estas carpetas se conservarán.");
            return;
        }

        var result = MessageBox.Show(
            this,
            $"¿Quitar '{item.RelativePath}' del CD Extra?\n\nEl archivo original no se modifica.",
            "Quitar datos",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (item.IsDirectory)
            Directory.Delete(item.FullPath, recursive: true);
        else if (File.Exists(item.FullPath))
            File.Delete(item.FullPath);

        RefreshBurnDataList();
        InvalidateCapacity();
        Log($"Datos: quitado del proyecto: {item.RelativePath}");
    }

    private void ClearDataItems_Click(object sender, RoutedEventArgs e)
    {
        int removableCount = _burnData.Count(item => !item.IsProtected);
        if (removableCount == 0) return;

        var result = MessageBox.Show(
            this,
            "¿Quitar todos los archivos y carpetas agregados manualmente?\n\nCDPLUS y PICTURES quedarán vacías. La carpeta AUDIO administrada desde Audio se conservará.",
            "Vaciar datos",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        foreach (string entry in Directory.GetFileSystemEntries(_dataProjectRoot))
        {
            string name = Path.GetFileName(entry);
            if (string.Equals(name, "CDPLUS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "PICTURES", StringComparison.OrdinalIgnoreCase))
            {
                ClearDirectoryContents(entry);
                continue;
            }
            if (string.Equals(name, "AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                // AUDIO es administrado desde la sección Audio; no se borra al vaciar datos manuales.
                continue;
            }

            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }

        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "CDPLUS"));
        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "PICTURES"));
        Directory.CreateDirectory(DataAudioProjectDirectory);
        RefreshBurnDataList();
        InvalidateCapacity();
        Log("Datos: proyecto manual vaciado; se conservaron CDPLUS, PICTURES y AUDIO administrado desde Audio.");
    }

    private void OpenDataProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string projectRoot = MainTabs.SelectedIndex == 2 ? _dataOnlyProjectRoot : _dataProjectRoot;
            Directory.CreateDirectory(projectRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", projectRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ex);
            Info(ex.Message);
        }
    }

    private void RefreshDataProject_Click(object sender, RoutedEventArgs e)
    {
        RefreshBurnDataList();
        InvalidateCapacity();
        Log("Datos: lista del proyecto actualizada.");
    }

    private void RefreshBurnDataList()
    {
        Directory.CreateDirectory(_dataProjectRoot);
        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "CDPLUS"));
        Directory.CreateDirectory(Path.Combine(_dataProjectRoot, "PICTURES"));
        Directory.CreateDirectory(DataAudioProjectDirectory);

        // Si alguien eliminó un archivo de AUDIO desde el Explorador, sincronizamos la lista.
        foreach (var stale in _burnCompressedAudio.Where(item => !File.Exists(item.DataPath)).ToList())
            _burnCompressedAudio.Remove(stale);

        _burnData.Clear();
        var directories = Directory.EnumerateDirectories(_dataProjectRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => GetProjectRelativePath(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string directory in directories)
        {
            string relative = GetProjectRelativePath(directory);
            bool isProtected = string.Equals(relative, "CDPLUS", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(relative, "PICTURES", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(relative, "AUDIO", StringComparison.OrdinalIgnoreCase);
            _burnData.Add(new DataProjectEntry
            {
                Name = Path.GetFileName(directory),
                FullPath = directory,
                RelativePath = relative,
                IsDirectory = true,
                IsProtected = isProtected
            });
        }

        var files = Directory.EnumerateFiles(_dataProjectRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => GetProjectRelativePath(path), StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int fileCount = 0;
        foreach (string file in files)
        {
            var info = new FileInfo(file);
            totalBytes += info.Length;
            fileCount++;
            _burnData.Add(new DataProjectEntry
            {
                Name = info.Name,
                FullPath = file,
                RelativePath = GetProjectRelativePath(file),
                IsDirectory = false,
                IsProtected = GetProjectRelativePath(file).StartsWith("AUDIO/", StringComparison.OrdinalIgnoreCase),
                Size = info.Length
            });
        }

        int folderCount = _burnData.Count(item => item.IsDirectory);
        DataProjectSummaryText.Text = $"{fileCount} archivo(s) · {folderCount} carpeta(s) · {FormatBytes(totalBytes)} · CDPLUS, PICTURES y AUDIO incluidas por defecto.";
        UpdateHybridAudioSummary();
        RefreshDataTargets(directories);
    }

    private void RefreshDataTargets(IReadOnlyList<string> directories)
    {
        string previous = DataTargetCombo.SelectedItem as string ?? "CDPLUS";
        var targets = new List<string> { "CDPLUS", "PICTURES", "Raíz del CD" };
        foreach (string directory in directories)
        {
            string relative = GetProjectRelativePath(directory);
            if (string.Equals(relative, "CDPLUS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, "PICTURES", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, "AUDIO", StringComparison.OrdinalIgnoreCase))
                continue;
            targets.Add(relative);
        }

        DataTargetCombo.ItemsSource = targets;
        DataTargetCombo.SelectedItem = targets.FirstOrDefault(item =>
            string.Equals(item, previous, StringComparison.OrdinalIgnoreCase)) ?? "CDPLUS";

        UpdateStandaloneSummaries();
    }

    private string GetProjectRelativePath(string fullPath)
    {
        string relative = Path.GetRelativePath(_dataProjectRoot, fullPath);
        return relative == "." ? "Raíz del CD" : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private bool ConfirmOverwriteIfNeeded(string destination)
    {
        if (!File.Exists(destination)) return true;
        return MessageBox.Show(
            this,
            $"'{Path.GetFileName(destination)}' ya existe en ese destino. ¿Reemplazarlo?",
            "Archivo existente",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ClearDirectoryContents(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        foreach (string path in Directory.GetDirectories(directory)) Directory.Delete(path, recursive: true);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:0.00} GB";
    }

    private void TryDeleteDataProjects()
    {
        foreach (string root in new[] { _dataProjectRoot, _dataOnlyProjectRoot })
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // La carpeta temporal se limpiará posteriormente por Windows si está bloqueada.
            }
        }
    }

    private void CdrtoolsDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateCapacity();
    }

    private async void CheckCapacity_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryValidateBurnProject(out string tools, out CdrtoolsDevice? cdrDevice, out string dataFolder)) return;

        var audioFiles = _burnAudio.ToList();
        string volume = VolumeLabelText.Text;
        string ffmpeg = FfmpegPathText.Text.Trim();

        await WithBusyAsync(async token =>
        {
            var progress = new Progress<(double progress, string status)>(p =>
                SetStatus(p.status, p.progress * 100));
            var toolLog = new Progress<string>(LogCdrtoolsLine);

            DiscCapacityInfo info = await _burner.AnalyzeCapacityAsync(
                tools, cdrDevice!.DeviceSpec, audioFiles, dataFolder, volume, ffmpeg,
                progress, toolLog, token);

            _lastCapacity = info;
            UpdateCapacityUi(info);
            if (!info.AudioEstimateComplete)
                Log($"Comprobación de capacidad: estimación parcial; {info.UnknownAudioCount} pista(s) se medirán exactamente al grabar.");
            else
                Log(info.Fits
                    ? "Comprobación de capacidad: el proyecto cabe en el medio."
                    : "Comprobación de capacidad: el proyecto excede el medio; grabación bloqueada.");
        }, "Comprobación de capacidad");
    }

    private bool TryValidateBurnProject(out string tools, out CdrtoolsDevice? cdrDevice, out string dataFolder)
    {
        tools = CdrtoolsPathText.Text.Trim();
        cdrDevice = SelectedCdrtoolsDevice;
        dataFolder = _dataProjectRoot;

        if (!CdExtraBurner.IsValidToolsDirectory(tools))
        {
            Info("Selecciona una carpeta cdrtools válida que contenga cdrecord.exe y mkisofs.exe.");
            return false;
        }
        if (cdrDevice is null)
        {
            Info("Busca y selecciona una grabadora en 'Dispositivo de grabación'.");
            return false;
        }
        if (_burnAudio.Count == 0)
        {
            Info("Agrega al menos una pista en 'CD Audio (CD-DA)'. Para un CD Extra debe quedar al menos una pista de audio tradicional; el Audio / Datos va en la segunda sesión.");
            return false;
        }
        if (!Directory.Exists(dataFolder))
        {
            Info("No se pudo preparar la sesión de datos.");
            return false;
        }

        return true;
    }

    private void UpdateHybridAudioSummary()
    {
        if (HybridAudioSummaryText is null) return;
        long dataAudioBytes = _burnCompressedAudio.Sum(item => item.SizeBytes);
        HybridAudioSummaryText.Text =
            $"Modo híbrido: {_burnAudio.Count} pista(s) CD Audio · {_burnCompressedAudio.Count} archivo(s) de Audio / Datos ({FormatBytes(dataAudioBytes)}). " +
            "CD Audio consume tiempo; Audio / Datos consume espacio en MB.";
        if (AutoFitMp3Button is not null) AutoFitMp3Button.IsEnabled = !_busy && _burnAudio.Count > 1;
    }

    private void InvalidateCapacity()
    {
        _lastCapacity = null;
        UpdateHybridAudioSummary();
        if (CapacityBar is null) return;

        CapacityBar.Value = 0;
        CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
        CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCE3ED"));
        CapacityMediaText.Text = "Medio: actualizando automáticamente…";
        CapacityProjectText.Text = "Proyecto: calculando audio + datos + reserva multisesión…";
        CapacityRemainingText.Text = "Falta para llenar: calculando…";
        CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
        CapacityStatusText.Text = "La capacidad se actualiza sola al cambiar el proyecto o la grabadora.";
        CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        if (CapacityPercentText is not null) CapacityPercentText.Text = "…";
        if (!_busy) BurnButton.IsEnabled = true;

        ScheduleAutomaticCapacityRefresh();
    }

    private void CancelAutomaticCapacityRefresh()
    {
        var pending = _capacityRefreshCts;
        _capacityRefreshCts = null;
        try { pending?.Cancel(); } catch { }
        // El propio trabajo asíncrono libera su CancellationTokenSource en finally.
    }

    private void ScheduleAutomaticCapacityRefresh(int delayMs = 500)
    {
        if (!IsLoaded || _busy) return;

        CancelAutomaticCapacityRefresh();
        var request = new CancellationTokenSource();
        _capacityRefreshCts = request;
        _ = RefreshCapacityAutomaticallyAsync(request, delayMs);
    }

    private async Task RefreshCapacityAutomaticallyAsync(CancellationTokenSource request, int delayMs)
    {
        try
        {
            await Task.Delay(Math.Max(0, delayMs), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_busy || !IsLoaded || !ReferenceEquals(_capacityRefreshCts, request)) return;

            string tools = CdrtoolsPathText.Text.Trim();
            CdrtoolsDevice? device = SelectedCdrtoolsDevice;

            if (!CdExtraBurner.IsValidToolsDirectory(tools))
            {
                SetAutomaticCapacityUnavailable(
                    "Medio: esperando cdrtools",
                    "Selecciona una instalación válida de cdrtools para detectar la capacidad del CD.");
                return;
            }

            if (device is null)
            {
                SetAutomaticCapacityUnavailable(
                    "Medio: esperando grabadora",
                    "Selecciona o detecta una grabadora para leer la capacidad automáticamente.");
                return;
            }

            Directory.CreateDirectory(_dataProjectRoot);
            CapacityStatusText.Text = "Actualizando capacidad automáticamente…";
            CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));

            var audioFiles = _burnAudio.ToList();
            DiscCapacityInfo info = await _burner.AnalyzeCapacityAsync(
                tools,
                device.DeviceSpec,
                audioFiles,
                _dataProjectRoot,
                VolumeLabelText.Text,
                FfmpegPathText.Text.Trim(),
                progress: null,
                log: null,
                cancellationToken: request.Token);

            request.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_capacityRefreshCts, request)) return;

            _lastCapacity = info;
            UpdateCapacityUi(info);
        }
        catch (OperationCanceledException)
        {
            // Una edición nueva del proyecto reemplazó esta medición.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_capacityRefreshCts, request)) return;
            _lastCapacity = null;
            CapacityBar.Value = 0;
            CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
            CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCD34D"));
            CapacityMediaText.Text = "Medio: no se pudo leer automáticamente";
            CapacityProjectText.Text = _burnAudio.Count == 0
                ? "Proyecto: agrega pistas de CD Audio; los datos se recalcularán automáticamente."
                : $"Proyecto: {_burnAudio.Count} pista(s) CD Audio · {_burnCompressedAudio.Count} archivo(s) de Audio / Datos.";
            CapacityRemainingText.Text = "Falta para llenar: inserta un CD grabable o revisa la grabadora.";
            CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
            CapacityStatusText.Text = "La grabación hará una comprobación exacta antes de escribir. " + ShortCapacityError(ex.Message);
            CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
            if (CapacityPercentText is not null) CapacityPercentText.Text = "—";
        }
        finally
        {
            if (ReferenceEquals(_capacityRefreshCts, request))
                _capacityRefreshCts = null;
            request.Dispose();
        }
    }

    private void SetAutomaticCapacityUnavailable(string mediaText, string statusText)
    {
        CapacityBar.Value = 0;
        CapacityMediaText.Text = mediaText;
        CapacityProjectText.Text = $"Proyecto: {_burnAudio.Count} pista(s) CD Audio · {_burnCompressedAudio.Count} archivo(s) de Audio / Datos.";
        CapacityRemainingText.Text = "Falta para llenar: —";
        CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        CapacityStatusText.Text = statusText;
        CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        if (CapacityPercentText is not null) CapacityPercentText.Text = "—";
    }

    private static string ShortCapacityError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        string first = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (first.Length > 150) first = first[..150] + "…";
        return first;
    }

    private void UpdateCapacityUi(DiscCapacityInfo info)
    {
        double percent = Math.Clamp(info.UsagePercent, 0, 100);
        CapacityBar.Value = percent;
        if (CapacityPercentText is not null)
            CapacityPercentText.Text = info.IsKnown ? $"{info.UsagePercent:0.0}%" : "—";

        if (!info.IsKnown)
        {
            CapacityMediaText.Text = "Medio: esperando que la unidad informe la capacidad…";
            CapacityProjectText.Text = info.AudioEstimateComplete
                ? $"Proyecto: {DiscCapacityInfo.FormatCdTime(info.RequiredSectors)} estimados."
                : $"Proyecto parcial: {DiscCapacityInfo.FormatCdTime(info.RequiredSectors)} conocidos + {info.UnknownAudioCount} pista(s) pendientes.";
            CapacityRemainingText.Text = "Falta para llenar: esperando lectura del disco…";
            CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
            CapacityStatusText.Text = info.AudioEstimateComplete
                ? "dvdrtool volverá a consultar automáticamente cada 3 segundos. Inserta el CD y espera unos segundos; no necesitas pulsar ningún botón."
                : "La comprobación no falló: algunas duraciones y/o la capacidad del medio están pendientes. dvdrtool seguirá reintentando y antes de escribir hará un preflight exacto.";
            CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
            CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCD34D"));
            CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
            BurnButton.IsEnabled = !_busy;
            return;
        }

        CapacityMediaText.Text =
            $"Medio: {DiscCapacityInfo.FormatCdTime(info.MediaSectors)} · {DiscCapacityInfo.FormatDataMiB(info.MediaSectors)} equivalentes";

        if (!info.AudioEstimateComplete)
        {
            CapacityProjectText.Text =
                $"Proyecto mínimo conocido: {DiscCapacityInfo.FormatCdTime(info.RequiredSectors)} eq. · CD Audio medido {DiscCapacityInfo.FormatCdTime(info.AudioSectors)} · " +
                $"datos+audio {DiscCapacityInfo.FormatDataMiB(info.DataSectors)} ({_burnCompressedAudio.Count} audio) · {info.UnknownAudioCount} pista(s) pendientes";

            if (info.DefinitelyExceeds)
            {
                long excess = -info.RemainingSectors;
                CapacityRemainingText.Text =
                    $"EXCESO mínimo: {DiscCapacityInfo.FormatCdTime(excess)} · {DiscCapacityInfo.FormatDataMiB(excess)} equivalentes";
                CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                CapacityStatusText.Text = "Incluso sin contar las pistas pendientes, el proyecto ya supera la capacidad del CD.";
                CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
                BurnButton.IsEnabled = false;
            }
            else
            {
                string names = string.IsNullOrWhiteSpace(info.UnknownAudioNames) ? "" : $" ({info.UnknownAudioNames})";
                CapacityRemainingText.Text = $"Falta para llenar: pendiente de {info.UnknownAudioCount} pista(s){names}.";
                CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
                CapacityStatusText.Text = "Puedes continuar. Al pulsar Grabar, el audio se convierte primero y se vuelve a comprobar la capacidad exacta antes de escribir el CD.";
                CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
                CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCD34D"));
                CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
                BurnButton.IsEnabled = !_busy;
            }
            return;
        }

        CapacityProjectText.Text =
            $"Proyecto: {DiscCapacityInfo.FormatCdTime(info.RequiredSectors)} eq. · CD Audio {DiscCapacityInfo.FormatCdTime(info.AudioSectors)} ({_burnAudio.Count} pista(s)) · " +
            $"datos+audio {DiscCapacityInfo.FormatDataMiB(info.DataSectors)} ({_burnCompressedAudio.Count} audio) · uso {info.UsagePercent:0.0}%";

        if (info.Fits)
        {
            double freePercent = info.MediaSectors > 0
                ? info.RemainingSectors * 100.0 / info.MediaSectors
                : 0.0;
            CapacityRemainingText.Text =
                $"Falta para llenar: {DiscCapacityInfo.FormatCdTime(info.RemainingSectors)} · {DiscCapacityInfo.FormatDataMiB(info.RemainingSectors)} equivalentes · {freePercent:0.0}% libre";
            CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
            CapacityStatusText.Text = _burnCompressedAudio.Count > 0
                ? "El proyecto cabe: las pistas CD Audio van en la primera sesión y el Audio / Datos en la segunda sesión."
                : "El proyecto cabe en el disco con el margen multisesión y de seguridad incluido.";
            CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
            CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
            CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D"));
        }
        else
        {
            long excess = -info.RemainingSectors;
            CapacityRemainingText.Text =
                $"EXCESO: {DiscCapacityInfo.FormatCdTime(excess)} · {DiscCapacityInfo.FormatDataMiB(excess)} equivalentes";
            CapacityRemainingText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
            CapacityStatusText.Text = _burnAudio.Count > 1
                ? "El proyecto supera la capacidad. Puedes usar 'Ajustar automáticamente a Datos' para mover pistas desde el final a la sesión de datos."
                : "El proyecto supera la capacidad. Reduce los datos/audio o usa un disco de mayor capacidad.";
            CapacityBar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
            CapacityCard.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
            CapacityStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
        }

        BurnButton.IsEnabled = !_busy && info.Fits;
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_burnAudio.Count == 0)
        {
            Info("Agrega al menos una pista para crear un CD Extra.");
            return;
        }
        if (!TryGetBurnSpeed(out int? writeSpeed)) return;

        if (IsSimulationMode)
        {
            await RunCdExtraSimulationAsync(writeSpeed);
            return;
        }

        if (!TryValidateBurnProject(out string tools, out CdrtoolsDevice? cdrDevice, out string dataFolder)) return;
        if (!await EnsureFfmpegForAudioBurnAsync(_burnAudio.ToList())) return;

        string albumArtist = CdTextEnabled ? ExtraArtistText.Text.Trim() : "";
        string albumTitle = CdTextEnabled ? ExtraAlbumText.Text.Trim() : "";

        if (_lastCapacity is { IsKnown: true, Fits: false })
        {
            Info("El proyecto excede la capacidad del CD. Puedes mover pistas a Audio / Datos, usar 'Ajustar automáticamente a Datos' o reducir otros datos; la barra se actualizará sola.");
            return;
        }

        if (ShouldConfirmBeforeBurn)
        {
            var confirm = MessageBox.Show(
                this,
                $"Se grabarán {_burnAudio.Count} pista(s) como CD Audio y {_burnCompressedAudio.Count} archivo(s) de audio dentro de la sesión de datos.\n\n" +
                $"Grabadora cdrtools: {cdrDevice}\nVelocidad: {(!writeSpeed.HasValue ? "Automática" : $"{writeSpeed.Value}x manual")}\n" +
                $"{(string.IsNullOrWhiteSpace(albumArtist) && string.IsNullOrWhiteSpace(albumTitle) ? "CD-TEXT: sin etiquetas" : $"CD-TEXT: {albumArtist} · {albumTitle}")}\n\nEl disco se cerrará al finalizar. ¿Continuar?",
                "Grabar CD Extra con cdrtools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var audioFiles = _burnAudio.ToList();
        string volume = VolumeLabelText.Text;

        await WithBusyAsync(async token =>
        {
            var progress = new Progress<(double progress, string status)>(p =>
            {
                SetStatus(p.status, p.progress * 100);
                Log(p.status);
            });
            var toolLog = new Progress<string>(LogCdrtoolsLine);
            var capacityProgress = new Progress<DiscCapacityInfo>(info =>
            {
                _lastCapacity = info;
                UpdateCapacityUi(info);
            });

            await _burner.BurnAsync(
                tools,
                cdrDevice!.DeviceSpec,
                audioFiles,
                dataFolder,
                volume,
                FfmpegPathText.Text.Trim(),
                albumArtist,
                albumTitle,
                writeSpeed,
                progress,
                toolLog,
                capacityProgress,
                token);

            MessageBox.Show(
                this,
                _burnCompressedAudio.Count > 0
                    ? "El CD Extra híbrido se grabó correctamente con cdrtools."
                    : "El CD Extra se grabó correctamente con cdrtools.",
                "dvdrtool",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Grabación de CD Extra");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogText.Clear();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Cancelación solicitada…", MainProgress.Value);
        Log("Se solicitó cancelar la operación actual.");
    }

    private async Task WithBusyAsync(Func<CancellationToken, Task> action, string operationName)
    {
        if (_busy) return;
        CancelAutomaticCapacityRefresh();
        _busy = true;
        BurnButton.IsEnabled = false;
        BurnAudioOnlyButton.IsEnabled = false;
        BurnDataOnlyButton.IsEnabled = false;
        if (AutoFitMp3Button is not null) AutoFitMp3Button.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operación cancelada", 0, error: true);
            Log($"{operationName} cancelada.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {operationName}", 0, error: true);
            LogError(ex);
            string? diagnosticLog = TrySaveDiagnosticLog(operationName, ex);
            string message = diagnosticLog is null
                ? ex.Message
                : ex.Message + $"\n\nSe guardó el registro completo en:\n{diagnosticLog}";
            MessageBox.Show(this, message, operationName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _busy = false;
            BurnButton.IsEnabled = _lastCapacity?.Fits != false;
            BurnAudioOnlyButton.IsEnabled = true;
            BurnDataOnlyButton.IsEnabled = true;
            UpdateHybridAudioSummary();
            UpdateStandaloneSummaries();
            CancelButton.Visibility = Visibility.Collapsed;
            ScheduleAutomaticCapacityRefresh(250);
            if (MainProgress.Value >= 99) await Task.Delay(600);
            MainProgress.Value = 0;
        }
    }

    private string? TrySaveDiagnosticLog(string operationName, Exception ex)
    {
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

            string safeOperation = string.Concat(operationName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string path = Path.Combine(folder, $"dvdrtool-{safeOperation}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            string content = LogText.Text + Environment.NewLine + Environment.NewLine +
                "=== EXCEPCIÓN ===" + Environment.NewLine + ex;
            File.WriteAllText(path, content);
            Log($"Registro de diagnóstico guardado: {path}");
            return path;
        }
        catch
        {
            return null;
        }
    }

    private void SetStatus(string text, double progress, bool error = false)
    {
        StatusText.Text = text;
        MainProgress.Value = Math.Clamp(progress, 0, 100);
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#DC2626" : "#16A34A"));
    }

    private void LogCdrtoolsLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Log($"cdrtools | {message}");
    }

    private void Log(string message)
    {
        LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogText.ScrollToEnd();
    }

    private void LogError(Exception ex)
    {
        Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null) Log($"  -> {ex.InnerException.Message}");
    }

    private void Info(string text) => MessageBox.Show(this, text, "dvdrtool", MessageBoxButton.OK, MessageBoxImage.Information);
}
