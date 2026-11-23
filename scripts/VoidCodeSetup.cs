using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("VoidCode IDE Setup")]
[assembly: AssemblyDescription("VoidCode IDE v1.0.0 Per-User Windows Installer")]
[assembly: AssemblyCompany("VoidCode Contributors")]
[assembly: AssemblyProduct("VoidCode IDE")]
[assembly: AssemblyCopyright("Copyright (c) 2026 VoidCode Contributors")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace VoidCodeSetup
{
    internal static class Program
    {
        private const string MagicFooter = "VCSFX100";

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = false;
            bool launch = true;
            string customDir = null;
            string screenshotPath = null;

            foreach (string rawArg in args)
            {
                string a = rawArg.Trim();
                if (string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/VERYSILENT", StringComparison.OrdinalIgnoreCase))
                {
                    silent = true;
                }
                else if (string.Equals(a, "/NOLAUNCH", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, "--no-launch", StringComparison.OrdinalIgnoreCase))
                {
                    launch = false;
                }
                else if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                {
                    customDir = a.Substring(3).Trim('"');
                }
                else if (a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
                {
                    screenshotPath = a.Substring("--screenshot=".Length).Trim('"');
                }
            }

            string installDir = !string.IsNullOrWhiteSpace(customDir)
                ? customDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VoidCode");

            if (silent)
            {
                try
                {
                    RunInstall(installDir, true, true, true, true, false, null);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("VoidCode Setup failed: " + ex.Message);
                    return 1;
                }
            }

            Application app = new Application();
            Window win = CreateSetupWindow(installDir, launch, screenshotPath);
            app.Run(win);
            return 0;
        }

        private static Window CreateSetupWindow(string defaultDir, bool defaultLaunch, string screenshotPath)
        {
            const string xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='VoidCode IDE Setup'
        Width='600' Height='566'
        WindowStartupLocation='CenterScreen'
        WindowStyle='None'
        AllowsTransparency='True'
        Background='Transparent'
        ResizeMode='NoResize'
        FontFamily='Segoe UI'
        SnapsToDevicePixels='True'
        UseLayoutRounding='True'>
  <Window.Resources>
    <Style x:Key='PrimaryBtn' TargetType='Button'>
      <Setter Property='Background' Value='#38BDF8'/>
      <Setter Property='Foreground' Value='#06101E'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='8' Padding='20,9'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='bd' Property='Background' Value='#7DD3FC'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='bd' Property='Background' Value='#1E293B'/>
                <Setter Property='Foreground' Value='#64748B'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='QuietBtn' TargetType='Button'>
      <Setter Property='Background' Value='#162032'/>
      <Setter Property='Foreground' Value='#CBD5E1'/>
      <Setter Property='BorderBrush' Value='#26344D'/>
      <Setter Property='FontSize' Value='12.5'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='16,8'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='bd' Property='Background' Value='#1E2C44'/>
                <Setter TargetName='bd' Property='BorderBrush' Value='#38BDF8'/>
                <Setter Property='Foreground' Value='#F8FAFC'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter Property='Opacity' Value='0.45'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='CloseBtn' TargetType='Button'>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='Foreground' Value='#94A3B8'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='bd' Background='{TemplateBinding Background}' Width='32' Height='28' CornerRadius='6'>
              <TextBlock Text='✕' FontSize='12' HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{TemplateBinding Foreground}'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='bd' Property='Background' Value='#EF4444'/>
                <Setter Property='Foreground' Value='#FFFFFF'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='ModernCheck' TargetType='CheckBox'>
      <Setter Property='Foreground' Value='#E2E8F0'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Margin' Value='0,0,0,10'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <Grid Background='Transparent'>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='Auto'/>
                <ColumnDefinition Width='*'/>
              </Grid.ColumnDefinitions>
              <Border x:Name='box' Width='18' Height='18' CornerRadius='5' Background='#111827' BorderBrush='#334155' BorderThickness='1.5' VerticalAlignment='Center' Margin='0,0,12,0'>
                <Path x:Name='mark' Data='M 3.5 9 L 7 12.5 L 14 5' Stroke='#06101E' StrokeThickness='2.2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Visibility='Collapsed'/>
              </Border>
              <ContentPresenter Grid.Column='1' VerticalAlignment='Center'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='box' Property='BorderBrush' Value='#38BDF8'/>
              </Trigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='box' Property='Background' Value='#38BDF8'/>
                <Setter TargetName='box' Property='BorderBrush' Value='#38BDF8'/>
                <Setter TargetName='mark' Property='Visibility' Value='Visible'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter Property='Opacity' Value='0.5'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Border Margin='12' CornerRadius='14' Background='#0A0E17' BorderBrush='#233047' BorderThickness='1'>
    <Border.Effect>
      <DropShadowEffect BlurRadius='24' ShadowDepth='6' Direction='270' Color='#000000' Opacity='0.65'/>
    </Border.Effect>
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height='44'/>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='*'/>
        <RowDefinition Height='Auto'/>
      </Grid.RowDefinitions>

      <!-- Custom Title Bar -->
      <Border x:Name='TitleBar' Grid.Row='0' Background='#0E1422' CornerRadius='14,14,0,0' BorderBrush='#1C263B' BorderThickness='0,0,0,1' Padding='16,0,10,0'>
        <Grid>
          <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
            <Border Width='10' Height='10' CornerRadius='5' Background='#38BDF8' Margin='0,0,10,0'/>
            <TextBlock Text='VoidCode IDE Setup' Foreground='#E2E8F0' FontWeight='SemiBold' FontSize='12.5' VerticalAlignment='Center'/>
            <Border Background='#172236' BorderBrush='#283754' BorderThickness='1' CornerRadius='4' Padding='6,1' Margin='10,0,0,0' VerticalAlignment='Center'>
              <TextBlock Text='v1.0.0 · win-x64' Foreground='#7DD3FC' FontSize='10.5' FontFamily='Consolas'/>
            </Border>
          </StackPanel>
          <Button x:Name='BtnCloseTop' Style='{StaticResource CloseBtn}' HorizontalAlignment='Right' VerticalAlignment='Center'/>
        </Grid>
      </Border>

      <!-- Hero Banner -->
      <Border Grid.Row='1' Margin='22,18,22,12' CornerRadius='10' BorderBrush='#22324C' BorderThickness='1' Padding='18,16'>
        <Border.Background>
          <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
            <GradientStop Color='#131D31' Offset='0'/>
            <GradientStop Color='#0D1322' Offset='1'/>
          </LinearGradientBrush>
        </Border.Background>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
          </Grid.ColumnDefinitions>
          <Border Width='46' Height='46' CornerRadius='11' Background='#172942' BorderBrush='#38BDF8' BorderThickness='1' Margin='0,0,16,0' VerticalAlignment='Center'>
            <TextBlock Text='V' Foreground='#38BDF8' FontSize='22' FontWeight='Bold' HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <StackPanel Grid.Column='1' VerticalAlignment='Center'>
            <TextBlock Text='Install VoidCode IDE' Foreground='#F8FAFC' FontSize='18' FontWeight='SemiBold' Margin='0,0,0,3'/>
            <TextBlock Text='Windows-first AI-native IDE powered by VoidAgent Core, Monaco, ConPTY &amp; DAP' Foreground='#94A3B8' FontSize='12.2' TextWrapping='Wrap'/>
          </StackPanel>
        </Grid>
      </Border>

      <!-- Main Body -->
      <StackPanel Grid.Row='2' Margin='22,2,22,0'>
        <TextBlock Text='DESTINATION FOLDER (PER-USER · NO ADMIN REQUIRED)' Foreground='#64748B' FontSize='10.5' FontWeight='Bold' Margin='2,0,0,6'/>
        <Grid Margin='0,0,0,16'>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
          </Grid.ColumnDefinitions>
          <Border Grid.Column='0' Background='#111827' BorderBrush='#24324A' BorderThickness='1' CornerRadius='8' Padding='10,6' Margin='0,0,8,0'>
            <TextBox x:Name='TxtInstallDir' Background='Transparent' Foreground='#F1F5F9' CaretBrush='#38BDF8' BorderThickness='0' FontFamily='Consolas' FontSize='12.5' VerticalContentAlignment='Center'/>
          </Border>
          <Button x:Name='BtnBrowse' Grid.Column='1' Content='Browse...' Style='{StaticResource QuietBtn}'/>
        </Grid>

        <TextBlock Text='INSTALLATION OPTIONS' Foreground='#64748B' FontSize='10.5' FontWeight='Bold' Margin='2,0,0,8'/>
        <Border Background='#0E1422' BorderBrush='#1E2A40' BorderThickness='1' CornerRadius='10' Padding='16,14,16,6'>
          <StackPanel>
            <CheckBox x:Name='ChkDesktop' Style='{StaticResource ModernCheck}' IsChecked='True' Content='Create Desktop shortcut'/>
            <CheckBox x:Name='ChkStartMenu' Style='{StaticResource ModernCheck}' IsChecked='True' Content='Create Start Menu shortcut'/>
            <CheckBox x:Name='ChkPath' Style='{StaticResource ModernCheck}' IsChecked='True' Content='Add &quot;voidcode&quot; CLI command to User PATH (voidcode .)'/>
            <CheckBox x:Name='ChkExplorer' Style='{StaticResource ModernCheck}' IsChecked='True' Content='Register &quot;Open with VoidCode&quot; in Windows Explorer context menu'/>
            <CheckBox x:Name='ChkLaunch' Style='{StaticResource ModernCheck}' IsChecked='True' Content='Launch VoidCode IDE when installation completes'/>
          </StackPanel>
        </Border>

        <!-- Progress Section -->
        <Grid Margin='2,14,2,6'>
          <TextBlock x:Name='LblStatus' Text='Ready to install VoidCode IDE v1.0.0.' Foreground='#94A3B8' FontSize='12' HorizontalAlignment='Left' TextTrimming='CharacterEllipsis' MaxWidth='460'/>
          <TextBlock x:Name='LblPercent' Text='0%' Foreground='#38BDF8' FontSize='12' FontFamily='Consolas' FontWeight='SemiBold' HorizontalAlignment='Right'/>
        </Grid>
        <Border x:Name='ProgressTrack' Height='8' CornerRadius='4' Background='#151F32' ClipToBounds='True' Margin='0,0,0,14'>
          <Border x:Name='ProgressFill' HorizontalAlignment='Left' Width='0' CornerRadius='4'>
            <Border.Background>
              <LinearGradientBrush StartPoint='0,0' EndPoint='1,0'>
                <GradientStop Color='#38BDF8' Offset='0'/>
                <GradientStop Color='#0EA5E9' Offset='1'/>
              </LinearGradientBrush>
            </Border.Background>
          </Border>
        </Border>
      </StackPanel>

      <!-- Footer -->
      <Border Grid.Row='3' Background='#0E1422' CornerRadius='0,0,14,14' BorderBrush='#1C263B' BorderThickness='0,1,0,0' Padding='22,12'>
        <Grid>
          <TextBlock Text='MIT License · Zero Telemetry · Windows DPAPI' Foreground='#475569' FontSize='11.5' VerticalAlignment='Center'/>
          <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
            <Button x:Name='BtnCancel' Content='Cancel' Style='{StaticResource QuietBtn}' Margin='0,0,10,0'/>
            <Button x:Name='BtnInstall' Content='Install VoidCode' Style='{StaticResource PrimaryBtn}'/>
          </StackPanel>
        </Grid>
      </Border>
    </Grid>
  </Border>
</Window>";

            Window win = (Window)XamlReader.Parse(xaml);
            Border titleBar = (Border)win.FindName("TitleBar");
            Button btnCloseTop = (Button)win.FindName("BtnCloseTop");
            TextBox txtDir = (TextBox)win.FindName("TxtInstallDir");
            Button btnBrowse = (Button)win.FindName("BtnBrowse");
            CheckBox chkDesktop = (CheckBox)win.FindName("ChkDesktop");
            CheckBox chkStartMenu = (CheckBox)win.FindName("ChkStartMenu");
            CheckBox chkPath = (CheckBox)win.FindName("ChkPath");
            CheckBox chkExplorer = (CheckBox)win.FindName("ChkExplorer");
            CheckBox chkLaunch = (CheckBox)win.FindName("ChkLaunch");
            TextBlock lblStatus = (TextBlock)win.FindName("LblStatus");
            TextBlock lblPercent = (TextBlock)win.FindName("LblPercent");
            Border progressTrack = (Border)win.FindName("ProgressTrack");
            Border progressFill = (Border)win.FindName("ProgressFill");
            Button btnCancel = (Button)win.FindName("BtnCancel");
            Button btnInstall = (Button)win.FindName("BtnInstall");

            txtDir.Text = defaultDir;
            chkLaunch.IsChecked = defaultLaunch;
            bool isInstalled = false;

            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) win.DragMove();
            };

            btnCloseTop.Click += (s, e) => win.Close();
            btnCancel.Click += (s, e) => win.Close();

            btnBrowse.Click += (s, e) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.SelectedPath = txtDir.Text;
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        txtDir.Text = dlg.SelectedPath;
                    }
                }
            };

            Action<int, string> setProgress = (pct, msg) =>
            {
                int clamped = Math.Max(0, Math.Min(100, pct));
                lblStatus.Text = msg;
                lblPercent.Text = clamped + "%";
                double trackW = progressTrack.ActualWidth > 0 ? progressTrack.ActualWidth : 530;
                progressFill.Width = (trackW * clamped) / 100.0;
            };

            btnInstall.Click += (s, e) =>
            {
                if (isInstalled)
                {
                    win.Close();
                    return;
                }

                string targetDir = (txtDir.Text ?? "").Trim();
                if (string.IsNullOrEmpty(targetDir))
                {
                    MessageBox.Show(win, "Please choose an installation folder.", "VoidCode Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                btnInstall.IsEnabled = false;
                btnBrowse.IsEnabled = false;
                txtDir.IsEnabled = false;
                chkDesktop.IsEnabled = false;
                chkStartMenu.IsEnabled = false;
                chkPath.IsEnabled = false;
                chkExplorer.IsEnabled = false;
                chkLaunch.IsEnabled = false;

                bool desk = chkDesktop.IsChecked == true;
                bool start = chkStartMenu.IsChecked == true;
                bool path = chkPath.IsChecked == true;
                bool exp = chkExplorer.IsChecked == true;
                bool launch = chkLaunch.IsChecked == true;

                Thread worker = new Thread(() =>
                {
                    try
                    {
                        RunInstall(targetDir, desk, start, path, exp, launch, (pct, msg) =>
                        {
                            win.Dispatcher.BeginInvoke((Action)(() => setProgress(pct, msg)));
                        });

                        win.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            isInstalled = true;
                            setProgress(100, "VoidCode IDE v1.0.0 installed successfully!");
                            btnInstall.Content = "Finish";
                            btnInstall.IsEnabled = true;
                            btnCancel.Visibility = Visibility.Collapsed;
                            if (launch) win.Close();
                        }));
                    }
                    catch (Exception ex)
                    {
                        win.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            lblStatus.Text = "Installation failed: " + ex.Message;
                            btnInstall.IsEnabled = true;
                            MessageBox.Show(win, ex.Message, "VoidCode Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }));
                    }
                });
                worker.IsBackground = true;
                worker.Start();
            };

            if (!string.IsNullOrEmpty(screenshotPath))
            {
                win.ContentRendered += (s, e) =>
                {
                    try
                    {
                        setProgress(42, "Installing VoidCode.dll...");
                        win.UpdateLayout();
                        RenderTargetBitmap rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(win);
                        PngBitmapEncoder enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (FileStream fs = new FileStream(screenshotPath, FileMode.Create, FileAccess.Write))
                        {
                            enc.Save(fs);
                        }
                    }
                    catch (Exception) { /* screenshot capture best-effort */ }
                    finally
                    {
                        win.Close();
                    }
                };
            }

            return win;
        }

        internal static void RunInstall(
            string installDir,
            bool desktopShortcut,
            bool startMenuShortcut,
            bool addToPath,
            bool explorerMenu,
            bool launchAfter,
            Action<int, string> onProgress)
        {
            string selfPath = Assembly.GetExecutingAssembly().Location;
            using (FileStream fs = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 24)
                    throw new InvalidOperationException("Installer payload is missing.");

                fs.Seek(fs.Length - 16, SeekOrigin.Begin);
                byte[] footerBuf = new byte[16];
                int read = fs.Read(footerBuf, 0, 16);
                if (read != 16)
                    throw new InvalidOperationException("Could not read installer footer.");

                long zipLength = BitConverter.ToInt64(footerBuf, 0);
                string magic = Encoding.ASCII.GetString(footerBuf, 8, 8);
                if (magic != MagicFooter || zipLength <= 0 || zipLength > fs.Length - 16)
                    throw new InvalidOperationException("Installer payload signature mismatch.");

                long zipOffset = fs.Length - 16 - zipLength;
                Directory.CreateDirectory(installDir);

                using (SubStream zipStream = new SubStream(fs, zipOffset, zipLength))
                using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, archive.Entries.Count);
                    int index = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        index++;
                        string relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(installDir, relPath);

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destPath);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                        int pct = Math.Min(85, (int)((index * 85L) / total));
                        if (onProgress != null && (index % 5 == 0 || index == total))
                        {
                            onProgress(pct, "Installing " + entry.Name + "...");
                        }

                        using (Stream inStream = entry.Open())
                        using (FileStream outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            inStream.CopyTo(outStream);
                        }
                    }
                }
            }

            string exePath = Path.Combine(installDir, "VoidCode.exe");
            string cmdShimPath = Path.Combine(installDir, "voidcode.cmd");
            File.WriteAllText(cmdShimPath, "@echo off\r\nstart \"\" \"%~dp0VoidCode.exe\" %*\r\n", Encoding.ASCII);

            if (onProgress != null) onProgress(88, "Creating shortcuts...");
            if (startMenuShortcut)
            {
                string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
                CreateShortcut(Path.Combine(startMenuDir, "VoidCode.lnk"), exePath, installDir);
            }
            if (desktopShortcut)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopDir, "VoidCode.lnk"), exePath, installDir);
            }

            if (addToPath)
            {
                if (onProgress != null) onProgress(92, "Adding 'voidcode' CLI to User PATH...");
                string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                string[] parts = userPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                bool exists = false;
                foreach (string p in parts)
                {
                    if (string.Equals(p.Trim().TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    string updated = string.IsNullOrEmpty(userPath) ? installDir : (userPath.TrimEnd(';') + ";" + installDir);
                    Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
                }
            }

            if (explorerMenu)
            {
                if (onProgress != null) onProgress(95, "Registering Windows Explorer context menu...");
                RegisterContextMenu(@"Software\Classes\*\shell\VoidCode", exePath, "\"%1\"");
                RegisterContextMenu(@"Software\Classes\Directory\shell\VoidCode", exePath, "\"%1\"");
                RegisterContextMenu(@"Software\Classes\Directory\Background\shell\VoidCode", exePath, "\"%V\"");
            }

            if (onProgress != null) onProgress(98, "Registering in Windows Installed Apps...");
            RegisterUninstaller(installDir, exePath);

            if (onProgress != null) onProgress(100, "VoidCode IDE v1.0.0 installed successfully!");

            if (launchAfter && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = installDir, UseShellExecute = true });
            }
        }

        private static void CreateShortcut(string lnkPath, string targetExe, string workingDir)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "VoidCode — AI-Native Windows IDE" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception) { /* Best-effort shortcut creation */ }
        }

        private static void RegisterContextMenu(string subKeyPath, string exePath, string argToken)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath))
                {
                    if (key == null) return;
                    key.SetValue("", "Open with VoidCode", RegistryValueKind.String);
                    key.SetValue("Icon", "\"" + exePath + "\",0", RegistryValueKind.String);
                }
                using (RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey(subKeyPath + @"\command"))
                {
                    if (cmdKey == null) return;
                    cmdKey.SetValue("", "\"" + exePath + "\" " + argToken, RegistryValueKind.String);
                }
            }
            catch (Exception) { /* Best-effort context menu registration */ }
        }

        private static void RegisterUninstaller(string installDir, string exePath)
        {
            try
            {
                string uninstallScript = Path.Combine(installDir, "uninstall.ps1");
                string uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + uninstallScript + "\"";
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\VoidCode"))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", "VoidCode IDE", RegistryValueKind.String);
                    key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
                    key.SetValue("Publisher", "VoidCode Contributors", RegistryValueKind.String);
                    key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
                    key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                    key.SetValue("UninstallString", uninstallCmd, RegistryValueKind.String);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", 165000, RegistryValueKind.DWord);
                }
            }
            catch (Exception) { /* Best-effort uninstall registration */ }
        }
    }

    internal sealed class SubStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubStream(Stream baseStream, long start, long length)
        {
            _baseStream = baseStream;
            _start = start;
            _length = length;
            _baseStream.Seek(_start, SeekOrigin.Begin);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }

        public override long Position
        {
            get { return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            _baseStream.Seek(_start + _position, SeekOrigin.Begin);
            int read = _baseStream.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset
                : origin == SeekOrigin.Current ? _position + offset
                : _length + offset;
            if (target < 0) target = 0;
            if (target > _length) target = _length;
            _position = target;
            _baseStream.Seek(_start + _position, SeekOrigin.Begin);
            return _position;
        }

        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
