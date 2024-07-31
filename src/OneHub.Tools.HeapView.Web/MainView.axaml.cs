using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using System;
using System.Linq;

namespace OneHub.Tools.HeapView;

public partial class MainView : UserControl
{
    private readonly MainViewModel viewModel = new();

    private IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Invalid owner.");

    public MainView()
    {
        InitializeComponent();

        DataContext = viewModel;

        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }
    
    public async void Open(IStorageFile storageFile)
    {
        try
        {
            await viewModel.LoadDataAsync(storageFile);
            heapView.Snapshot = viewModel.CurrentData;
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
            if (files?.FirstOrDefault() is IStorageFile storageFile)
            {
                Open(storageFile);
            }
        }
    }

    public async void OnOpenClicked(object? sender, RoutedEventArgs args)
    {
        var options = new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("GC dump") { Patterns = new[] { "*.gcdump", "*.hprof", "*.mono-heap" } } }
        };
        var result = await StorageProvider.OpenFilePickerAsync(options);
        if (result != null && result.Count == 1 && result[0] is IStorageFile storageFile)
        {
            Open(storageFile);
        }
    }
}