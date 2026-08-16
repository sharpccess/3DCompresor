using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Compresor3D;

namespace Compresor3D.GUI;

public partial class MainWindow : Window
{
    private ObservableCollection<FileItem> _files = new();
    private bool _isCompressing = false;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
    }

    private void AddFilesBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar archivos",
            Multiselect = true,
            Filter = "Todos los archivos (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                AddFile(file);
            }
            UpdateUI();
        }
    }

    private void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta"
        };

        if (dialog.ShowDialog() == true)
        {
            AddFolder(dialog.FolderName);
            UpdateUI();
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            ((Border)sender).BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0, 122, 204));
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ((Border)sender).BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(63, 63, 70));
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        ((Border)sender).BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(63, 63, 70));

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    AddFile(file);
                }
                else if (Directory.Exists(file))
                {
                    AddFolder(file);
                }
            }
            UpdateUI();
        }
    }

    private void AddFile(string path)
    {
        if (_files.Any(f => f.Path == path)) return;

        var info = new FileInfo(path);
        _files.Add(new FileItem
        {
            Path = path,
            Name = info.Name,
            Size = FormatSize(info.Length),
            SizeBytes = info.Length,
            Status = "Pendiente",
            Ratio = ""
        });
    }

    private void AddFolder(string path)
    {
        var dir = new DirectoryInfo(path);
        foreach (var file in dir.GetFiles("*.*", SearchOption.AllDirectories))
        {
            AddFile(file.FullName);
        }
    }

    private void UpdateUI()
    {
        bool hasFiles = _files.Count > 0;
        DropPlaceholder.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        CompressBtn.IsEnabled = hasFiles && !_isCompressing;
    }

    private async void CompressBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompressing) return;
        _isCompressing = true;
        UpdateUI();

        CompressionProgress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        CompressionProgress.Maximum = _files.Count;
        CompressionProgress.Value = 0;

        int method = MethodSelector.SelectedIndex; // 0=Auto, 1=PAQ1, 2=BWT+Zstd

        for (int i = 0; i < _files.Count; i++)
        {
            var item = _files[i];
            item.Status = "Comprimiendo...";
            ProgressText.Text = $"Comprimiendo {item.Name} ({i + 1}/{_files.Count})";

            try
            {
                await Task.Run(() => CompressFile(item, method));
                item.Status = "✓ Comprimido";
            }
            catch (Exception ex)
            {
                item.Status = $"✗ Error: {ex.Message}";
            }

            CompressionProgress.Value = i + 1;
            FileList.Items.Refresh();
        }

        ProgressText.Text = $"Completado: {_files.Count} archivos procesados";
        _isCompressing = false;
        UpdateUI();
    }

    private void CompressFile(FileItem item, int method)
    {
        string inputPath = item.Path;
        string outputPath = inputPath + ".cubo";

        byte[] data = File.ReadAllBytes(inputPath);
        long originalSize = data.Length;

        if (method == 1) // PAQ1
        {
            byte[] compressed = CompresorPAQ.Comprimir(data);
            File.WriteAllBytes(outputPath, compressed);
            
            var fileInfo = new FileInfo(outputPath);
            double ratio = (double)fileInfo.Length / originalSize * 100;
            item.Ratio = $"{ratio:F1}%";
            item.Status = "✓ PAQ1";
        }
        else // Auto or BWT+Zstd - use standard 3D compression
        {
            // Create cubo and compress
            var cubo = new Cubo3D(1, 1, data.Length, data);
            var resultado = cubo.ComprimirFunciones(out long compressedSize, direccion: 1);
            
            // Save using the engine
            string nombreOriginal = Path.GetFileName(inputPath);
            Compresor3DEngine.GuardarCubo(outputPath, nombreOriginal, 1, 1, data.Length, originalSize, resultado);
            
            var outInfo = new FileInfo(outputPath);
            item.Ratio = $"{(double)outInfo.Length / originalSize * 100:F1}%";
            item.Status = "✓ BWT+Zstd";
        }
    }

    private async void DecompressBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar archivo a descomprimir",
            Filter = "Archivos Cubo (*.cubo)|*.cubo|Todos los archivos (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        string inputFile = dialog.FileName;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = $"Descomprimiendo {Path.GetFileName(inputFile)}...";

        try
        {
            await Task.Run(() => Compresor3DEngine.DescomprimirArchivo(inputFile));
            ProgressText.Text = $"✓ Descomprimido correctamente";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"✗ Error: {ex.Message}";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class FileItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "";
    public string Ratio { get; set; } = "";
}
