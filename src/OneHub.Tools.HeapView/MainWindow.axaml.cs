using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using OneHub.Diagnostics.HeapView;
using System.Linq;
using System;
using System.IO;
using MsBox.Avalonia;

namespace OneHub.Tools.HeapView;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }
    
    public async void Open(string fileName)
    {
        try
        {
            HeapSnapshot heapSnapshot;
            switch (Path.GetExtension(fileName))
            {
                case ".hprof":
                    using (var inputStream = File.OpenRead(fileName))
                    {
                        heapSnapshot = HProfConverter.Convert(inputStream);
                    }
                    break;

                case ".mono-heap":
                    using (var inputStream = File.OpenRead(fileName))
                    {
                        heapSnapshot = MonoHeapSnapshotConverter.Convert(inputStream);
                    }
                    break;

                default:
                case ".gcdump":
                    var heapDump = new GCHeapDump(fileName);
                    heapSnapshot = new HeapSnapshot(heapDump);
                    break;
            }
            heapView.Snapshot = heapSnapshot;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);

            var m = MessageBoxManager.GetMessageBoxStandard("Load error", ex.Message);
            await m.ShowAsync();
        }
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files) || e.Data.GetFiles()?.FirstOrDefault() is not IStorageFile)
            e.DragEffects = DragDropEffects.None;
        else
            e.DragEffects = DragDropEffects.Move;
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.OfType<IStorageFile>().ToArray();
            if (files?.FirstOrDefault()?.TryGetLocalPath() is string path)
            {
                Open(path);
            }
        }
    }

    public async void OnOpenClicked(object? sender, EventArgs args)
    {
        var options = new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("GC dump") { Patterns = new[] { "*.gcdump", "*.hprof", "*.mono-heap" } } }
        };
        var result = await StorageProvider.OpenFilePickerAsync(options);
        if (result != null && result.Count == 1 && result[0].TryGetLocalPath() is string path)
        {
            Open(path);
        }
    }
}