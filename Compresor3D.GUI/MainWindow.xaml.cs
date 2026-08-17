using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Compresor3D;

namespace Compresor3D.GUI;

public partial class MainWindow : Window
{
    private readonly List<FileItem> _files = new();
    private bool _isProcessing;
    
    public MainWindow()
    {
        InitializeComponent();
    }
    
    // ==================== AÑADIR ARCHIVOS ====================
    
    private void AddFilesBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar archivos",
            Multiselect = true,
            Filter = "Todos los archivos|*.*"
        };
        
        if (dlg.ShowDialog() == true)
        {
            foreach (var path in dlg.FileNames)
                AddFile(path);
            UpdateUI();
        }
    }
    
    private void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta"
        };
        
        if (dlg.ShowDialog() == true)
        {
            string folder = dlg.FolderName;
            string baseName = Path.GetFileName(folder);
            
            foreach (var file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(folder, file);
                AddFile(file, relativePath);
            }
            UpdateUI();
        }
    }
    
    private void AddFile(string path, string? displayName = null)
    {
        if (!File.Exists(path)) return;
        
        var info = new FileInfo(path);
        _files.Add(new FileItem
        {
            FullPath = path,
            Name = displayName ?? info.Name,
            Size = FormatSize(info.Length),
            SizeBytes = info.Length,
            Status = "Pendiente",
            Ratio = ""
        });
    }
    
    // ==================== DRAG & DROP ====================
    
    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Border border)
                border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        }
    }
    
    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"));
    }
    
    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"));
        
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    AddFile(path);
                }
                else if (Directory.Exists(path))
                {
                    string folder = path;
                    foreach (var file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        string relativePath = Path.GetRelativePath(folder, file);
                        AddFile(file, relativePath);
                    }
                }
            }
            UpdateUI();
        }
    }
    
    // ==================== COMPRIMIR ====================
    
    private async void CompressBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || _isProcessing) return;
        
        _isProcessing = true;
        UpdateUI();
        
        // Seleccionar archivo de destino
        var saveDlg = new SaveFileDialog
        {
            Title = "Guardar archivo comprimido",
            Filter = "Archivos CUBO|*.cubo",
            FileName = GetDefaultArchiveName()
        };
        
        if (saveDlg.ShowDialog() != true)
        {
            _isProcessing = false;
            UpdateUI();
            return;
        }
        
        string outputPath = saveDlg.FileName;
        byte metodo = MethodSelector.SelectedIndex switch
        {
            1 => CuboContainer.METHOD_PAQ1,
            2 => CuboContainer.METHOD_PAQ1_2D,
            _ => CuboContainer.METHOD_BWT_ZSTD
        };
        
        // Preparar archivos
        var archivos = new Dictionary<string, byte[]>();
        foreach (var f in _files)
        {
            try
            {
                archivos[f.Name] = await Task.Run(() => File.ReadAllBytes(f.FullPath));
                f.Status = "Listo";
            }
            catch (Exception ex)
            {
                f.Status = $"Error: {ex.Message}";
            }
        }
        FileList.Items.Refresh();
        
        // Comprimir con progreso
        var progress = new Progress<(string archivo, double progreso)>(p =>
        {
            CompressionProgress.Value = p.progreso * 100;
            ProgressText.Text = $"Comprimiendo {p.archivo}... ({p.progreso:P0})";
            
            // Actualizar estado del archivo
            var item = _files.FirstOrDefault(f => f.Name == p.archivo);
            if (item != null)
            {
                item.Status = "Comprimido";
                FileList.Items.Refresh();
            }
        });
        
        try
        {
            await Task.Run(() => CuboContainer.CrearContenedor(outputPath, archivos, metodo, progress));
            
            CompressionProgress.Value = 100;
            ProgressText.Text = $"Completado: {FormatSize(new FileInfo(outputPath).Length)}";
            
            // Mostrar resultados
            foreach (var f in _files)
            {
                f.Status = "✓ Comprimido";
            }
            
            MessageBox.Show($"Archivo creado: {outputPath}\n\nTamaño: {FormatSize(new FileInfo(outputPath).Length)}",
                "Compresión completada", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al comprimir: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            foreach (var f in _files) f.Status = "Error";
        }
        
        _isProcessing = false;
        UpdateUI();
    }
    
    // ==================== DESCOMPRIMIR ====================
    
    private async void DecompressBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        
        // Seleccionar archivo .cubo
        var openDlg = new OpenFileDialog
        {
            Title = "Seleccionar archivo a descomprimir",
            Filter = "Archivos CUBO|*.cubo|Todos los archivos|*.*"
        };
        
        if (openDlg.ShowDialog() != true) return;
        
        string inputPath = openDlg.FileName;
        
        // Verificar que es un .cubo
        if (!CuboContainer.EsCubo(inputPath))
        {
            MessageBox.Show("No es un archivo .cubo válido", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        // Seleccionar carpeta de destino
        var folderDlg = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta de destino"
        };
        
        if (folderDlg.ShowDialog() != true) return;
        
        string outputFolder = folderDlg.FolderName;
        _isProcessing = true;
        UpdateUI();
        
        CompressionProgress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        
        var progress = new Progress<(string archivo, double progreso)>(p =>
        {
            CompressionProgress.Value = p.progreso * 100;
            ProgressText.Text = $"Extrayendo {p.archivo}...";
        });
        
        try
        {
            var archivos = await Task.Run(() => CuboContainer.ExtraerContenedor(inputPath, progress));
            
            // Guardar archivos
            foreach (var kvp in archivos)
            {
                string filePath = Path.Combine(outputFolder, kvp.Key);
                string? dir = Path.GetDirectoryName(filePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(filePath, kvp.Value);
            }
            
            CompressionProgress.Value = 100;
            ProgressText.Text = $"Extraídos {archivos.Count} archivos a {outputFolder}";
            
            MessageBox.Show($"Extraídos {archivos.Count} archivos a:\n{outputFolder}",
                "Descompresión completada", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al descomprimir: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        
        _isProcessing = false;
        UpdateUI();
    }
    
    // ==================== UI HELPERS ====================
    
    private void UpdateUI()
    {
        bool hasFiles = _files.Count > 0;
        
        DropPlaceholder.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        FileList.ItemsSource = _files;
        
        CompressBtn.IsEnabled = hasFiles && !_isProcessing;
        DecompressBtn.IsEnabled = !_isProcessing;
        
        if (!hasFiles)
        {
            CompressionProgress.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            CompressionProgress.Value = 0;
            ProgressText.Text = "";
        }
    }
    
    private string GetDefaultArchiveName()
    {
        if (_files.Count == 1)
            return Path.GetFileNameWithoutExtension(_files[0].Name) + ".cubo";
        return "archivo.cubo";
    }
    
    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int order = 0;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:0.##} {sizes[order]}";
    }
}

public class FileItem
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "";
    public string Ratio { get; set; } = "";
}
