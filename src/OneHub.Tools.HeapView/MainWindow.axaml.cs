using Avalonia.Controls;
using OneHub.Diagnostics.HeapView;

namespace OneHub.Tools.HeapView;

public partial class MainWindow : Window
{
    HeapSnapshot heapSnapshot;

    public MainWindow(string fileName)
    {
        InitializeComponent();

        var heapDump = new GCHeapDump(fileName);
        heapSnapshot = new HeapSnapshot(heapDump);

        heapView.Snapshot = heapSnapshot;
    }
}