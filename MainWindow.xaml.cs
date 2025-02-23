using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Windows;
using System.Text;
using System.Windows.Controls;

public class ModuleUpdate
{
    public string Name { get; set; }
    public string CurrentVersion { get; set; }
    public string NewVersion { get; set; }
    public DateTime? PublishedDate { get; set; }
    public string ReleaseNotes { get; set; }
}

public class PSModule
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string Author { get; set; }
    public DateTime? InstalledDate { get; set; }
    public string Location { get; set; }
    public string ProjectUri { get; set; }
}

namespace PWSHModuleManager
{
    public partial class MainWindow : Window
    {
        private StringBuilder debugLog = new StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
            LoadModuleUpdatesAsync();
            LoadInstalledModulesAsync();
        }

        private void LogDebug(string message)
        {
            Dispatcher.Invoke(() =>
            {
                debugLog.AppendLine($"{DateTime.Now:HH:mm:ss}: {message}");
                debugOutput.Text = debugLog.ToString();
                debugOutput.ScrollToEnd();
                System.Diagnostics.Debug.WriteLine(message);
            });
        }

        private async Task LoadModuleUpdatesAsync()
        {
            try
            {
                // Update UI before starting background work
                txtStatus.Text = "Checking for updates...";
                btnRefresh.IsEnabled = false;
                btnUpdate.IsEnabled = false;
                btnViewNotes.IsEnabled = false;
                // loadingOverlay.Visibility = Visibility.Visible;

                // Run PowerShell operations in background
                var results = await Task.Run(async () =>
                {
                    var iss = InitialSessionState.CreateDefault();
                    iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;
                    using var runspace = RunspaceFactory.CreateRunspace(iss);
                    using var powerShell = PowerShell.Create();

                    await Task.Run(() => LogDebug("Opening runspace..."));
                    runspace.Open();
                    powerShell.Runspace = runspace;

                    // Add error and verbose output handling
                    powerShell.Streams.Error.DataAdded += (sender, e) =>
                    {
                        var error = ((PSDataCollection<ErrorRecord>)sender)[e.Index];
                        Application.Current.Dispatcher.Invoke(() => LogDebug($"Error: {error}"));
                    };

                    powerShell.Streams.Verbose.DataAdded += (sender, e) =>
                    {
                        var verbose = ((PSDataCollection<VerboseRecord>)sender)[e.Index];
                        Application.Current.Dispatcher.Invoke(() => LogDebug($"Verbose: {verbose}"));
                    };

                    powerShell.Streams.Warning.DataAdded += (sender, e) =>
                    {
                        var warning = ((PSDataCollection<WarningRecord>)sender)[e.Index];
                        Application.Current.Dispatcher.Invoke(() => LogDebug($"Warning: {warning}"));
                    };

                    powerShell.Streams.Information.DataAdded += (sender, e) =>
                    {
                        var info = ((PSDataCollection<InformationRecord>)sender)[e.Index];
                        Application.Current.Dispatcher.Invoke(() => LogDebug($"Info: {info}"));
                    };

                    // Your PowerShell script
                    // First, ensure PSResourceGet is available
                    LogDebug("Checking for PSResourceGet...");
                    powerShell.AddScript(@"
                        $VerbosePreference = 'Continue'
                        if (-Not (Get-Module -ListAvailable -Name Microsoft.PowerShell.PSResourceGet)) {
                            Write-Verbose 'PSResourceGet not found - attempting installation...'
                            If (-Not (Get-PackageProvider -Name Nuget -Force)) {
                                Install-PackageProvider -Name Nuget -Force -Verbose
                            }
                            Set-PSRepository -Name 'PSGallery' -InstallationPolicy Trusted -Verbose
                            Install-Module -Name Microsoft.PowerShell.PSResourceGet -Scope CurrentUser -Force -Verbose
                        } else {
                            Write-Verbose 'PSResourceGet module found'
                        }
                        Import-Module -Name Microsoft.PowerShell.PSResourceGet -Verbose
                    ");

                    LogDebug("Executing PSResourceGet setup...");
                    await powerShell.InvokeAsync();
                    LogDebug("Setup complete.");

                    // Clear commands and add main script
                    powerShell.Commands.Clear();
                    powerShell.AddScript(@"
                        $VerbosePreference = 'Continue'
                        Write-Verbose 'Starting module check...'
                        
                        $Installed = Get-InstalledPSResource -ErrorAction Stop | Group-Object Name
                        Write-Verbose ""Found $($Installed.Count) installed modules""
                        
                        foreach ($Module in $Installed) {
                            Write-Verbose ""Checking $($Module.Name)...""
                            if ($Module.Name -like 'Az.*' -and 'Az' -in $Installed.Name) {
                                Write-Verbose 'Skipping Az submodule'
                                Continue
                            }
                            if ($Module.Name -like 'Microsoft.Graph*' -and 'Microsoft.Graph' -in $Installed.Name) {
                                Write-Verbose 'Skipping Graph submodule'
                                Continue
                            }
                            
                            $Check = Find-PSResource -Name $Module.Name
                            if ([version]$Check.Version -gt [version]($Module.Group.Version | Sort-Object | Select-Object -Last 1)) {
                                Write-Verbose ""Update found for $($Module.Name): $($Module.Group.Version[0]) -> $($Check.Version)""
                                [pscustomobject]@{
                                    Name = $Check.Name
                                    NewVersion = $Check.Version
                                    CurrentVersion = $Module.Group.Version[0]
                                    PublishedDate = $Check.PublishedDate
                                    ReleaseNotes = $Check.ReleaseNotes
                                }
                            }
                        }
                        Write-Verbose 'Module check complete'
                    ");

                    LogDebug("Executing main script...");

                    var results = await powerShell.InvokeAsync();
                    powerShell.Dispose();
                    return results;
                });

                // Process results on UI thread
                var moduleList = results.Select(psObject => new ModuleUpdate
                {
                    Name = psObject.Properties["Name"]?.Value?.ToString(),
                    CurrentVersion = psObject.Properties["CurrentVersion"]?.Value?.ToString(),
                    NewVersion = psObject.Properties["NewVersion"]?.Value?.ToString(),
                    PublishedDate = psObject.Properties["PublishedDate"]?.Value as DateTime?,
                    ReleaseNotes = psObject.Properties["ReleaseNotes"]?.Value?.ToString()
                }).ToList();

                modulesGrid.ItemsSource = moduleList;
                txtStatus.Text = $"Last checked: {DateTime.Now:g}";
            }
            catch (Exception ex)
            {
                LogDebug($"Error: {ex}");
                MessageBox.Show($"Error checking for updates: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "Error checking for updates";
            }
            finally
            {
                // Update UI after completion
                btnRefresh.IsEnabled = true;
                btnUpdate.IsEnabled = modulesGrid.Items.Count > 0;
            }
        }

        private async Task LoadInstalledModulesAsync ()
        {
            try
            {
                var results = await Task.Run(async () =>
                {
                    var iss = InitialSessionState.CreateDefault();
                    iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;

                    using (var runspace = RunspaceFactory.CreateRunspace(iss))
                    using (var powerShell = PowerShell.Create())
                    {
                        LogDebug("Opening runspace...");
                        runspace.Open();
                        powerShell.Runspace = runspace;
                        powerShell.AddScript(@"
                       function Get-InstalledModuleList {
                        $Installed = try {
                            Get-InstalledPSResource -ErrorAction Stop
                        }
                        catch [Microsoft.PowerShell.PSResourceGet.UtilClasses.ResourceNotFoundException] {
                            return
                        }
                        foreach ($Module in $Installed){
                            if ($Module.Name -like 'Az.*' -and 'Az' -in $Installed.Name){
                                Continue
                            }
                            if ($Module.Name -like 'Microsoft.Graph*' -and 'Microsoft.Graph' -in $Installed.Name){
                                Continue
                            }
                            [pscustomobject]@{
                                Name = $Module.Name
                                Version = $Module.Version
                                InstalledDate = $Module.InstalledDate
                                Author = $Module.Author
                                Location = $Module.InstalledLocation
                            }
                        }
                    }
                    Get-InstalledModuleList
                        "
                            );
                        var results = await powerShell.InvokeAsync();
                        powerShell.Dispose();
                        return results;
                    }
                });
                    var moduleList = results.Select(psObject =>
                    {
                        return new PSModule
                        {
                            Name = psObject.Properties["Name"]?.Value?.ToString(),
                            Version = psObject.Properties["Version"]?.Value?.ToString(),
                            InstalledDate = psObject.Properties["InstalledDate"]?.Value as DateTime?,
                            Author = psObject.Properties["Author"]?.Value?.ToString(),
                            Location = psObject.Properties["Location"]?.Value.ToString(),
                            // ProjectUri = psObject.Properties["ProjectUri"]?.Value.ToString() ?? string.Empty
                        };
                    }).ToList();
                    Dispatcher.Invoke(() =>
                    {
                        allmodulesGrid.ItemsSource = null;  // Clear the current items
                        if (moduleList.Count > 0)
                        {
                            allmodulesGrid.ItemsSource = moduleList;
                            btnUpdateAll.IsEnabled = modulesGrid.Items.Count > 0;
                        }
                    });
            }
            catch (Exception ex)
            {
                LogDebug($"Error: {ex}");
                MessageBox.Show($"Error loading installed modules {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void InstallModuleUpdates(IEnumerable<string> moduleNames)
        {
            try
            {
                LogDebug($"Updating modules: {string.Join(", ", moduleNames)}");
                btnRefresh.IsEnabled = false;
                btnUpdate.IsEnabled = false;

                // Create initial sessionstate
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;

                using (var runspace = RunspaceFactory.CreateRunspace(iss))
                using (var powerShell = PowerShell.Create())
                {
                    LogDebug("Opening runspace...");
                    runspace.Open();
                    powerShell.Runspace = runspace;

                    // Add error and verbose output handling
                    powerShell.Streams.Error.DataAdded += (sender, e) =>
                    {
                        var error = ((PSDataCollection<ErrorRecord>)sender)[e.Index];
                        LogDebug($"Error: {error}");
                    };

                    powerShell.Streams.Verbose.DataAdded += (sender, e) =>
                    {
                        var verbose = ((PSDataCollection<VerboseRecord>)sender)[e.Index];
                        LogDebug($"Verbose: {verbose}");
                    };

                    powerShell.Streams.Warning.DataAdded += (sender, e) =>
                    {
                        var warning = ((PSDataCollection<WarningRecord>)sender)[e.Index];
                        LogDebug($"Warning: {warning}");
                    };

                    powerShell.Streams.Information.DataAdded += (sender, e) =>
                    {
                        var info = ((PSDataCollection<InformationRecord>)sender)[e.Index];
                        LogDebug($"Info: {info}");
                    };

                    // Create the module list for PowerShell
                    var moduleList = string.Join(",", moduleNames.Select(m => $"'{m}'"));

                    powerShell.AddScript($@"
                $VerbosePreference = ""Continue""
                Set-PSResourceRepository -Name ""PSGallery"" -Trusted
                $modules = @({moduleList})
                foreach($module in $modules) {{
                    Write-Verbose ""Updating $module...""
                    Update-PSResource -Name $module -Verbose
                }}
                ");

                    var UpdateResult = await powerShell.InvokeAsync();
                    LogDebug("Update Complete");
                    powerShell.Dispose();
                    Thread.Sleep(1000);

                    // Refresh the module list after updates
                    LoadModuleUpdatesAsync();
                    LoadInstalledModulesAsync();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error: {ex}");
                MessageBox.Show($"{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnRefresh.IsEnabled = true;
                btnUpdate.IsEnabled = true;
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadModuleUpdatesAsync();
            LoadInstalledModulesAsync();
        }

        private void debugOutput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }

        private void btnViewNotes_Click(object sender, RoutedEventArgs e)
        {
            if (modulesGrid.SelectedItem is ModuleUpdate selectedModule)
            {
                Window1 notesWindow = new Window1();
                notesWindow.Title = $"Release Notes: {selectedModule.Name}";
                notesWindow.txtboxNotes.Text = string.IsNullOrEmpty(selectedModule.ReleaseNotes)
                    ? "No release notes available."
                    : selectedModule.ReleaseNotes;
                notesWindow.txtboxNotes.IsReadOnly = true;
                notesWindow.txtboxNotes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                notesWindow.IsEnabled = true;
                notesWindow.Show();                
            }
        }

        private void modulesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            btnViewNotes.IsEnabled = modulesGrid.SelectedItem != null;
            btnUpdate.IsEnabled = modulesGrid.SelectedItem != null;
            btnUpdateAll.IsEnabled = modulesGrid.Items.Count > 0;
        }

        private void btnUpdate_Click(object sender, RoutedEventArgs e)
        {
            var selectedModules = modulesGrid.SelectedItems.Cast<ModuleUpdate>().Select(m => m.Name).ToList();
            if (selectedModules.Any())
            {
                InstallModuleUpdates(selectedModules);
            }
            else
            {
                MessageBox.Show("Please select at least one module to update.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void menu_OpenLocation(object sender, RoutedEventArgs e)
        {
            if (allmodulesGrid.SelectedItem != null)
            {
                try
                {
                    var selectedModule = allmodulesGrid.SelectedItem as PSModule;
                    LogDebug($"Selected: {selectedModule.Location}");
                    System.Diagnostics.Process.Start("explorer.exe", selectedModule.Location);
                }
                catch (Exception ex)
                {
                    LogDebug($"Error: {ex}");
                }
            }

        }

        private void allmodulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void allModulesTab_Focus(object sender, RoutedEventArgs e)
        {
            btnViewNotes.IsEnabled = false;
            btnUpdate.IsEnabled = false;
            btnUpdateAll.IsEnabled = false;
        }

        private void updatesTab_Focus(object sender, RoutedEventArgs e)
        {
            btnUpdate.IsEnabled = modulesGrid.Items.Count > 0;
            btnUpdateAll.IsEnabled = modulesGrid.Items.Count > 0;
        }

        private void btnUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            var toUpdate = modulesGrid.Items.Cast<ModuleUpdate>().Select(m => m.Name).ToList();
            modulesGrid.SelectAll();
            if (toUpdate.Any())
            {
                InstallModuleUpdates(toUpdate);
            }
        }
    }
}