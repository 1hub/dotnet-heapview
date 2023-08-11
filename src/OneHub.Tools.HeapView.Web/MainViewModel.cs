using Avalonia.Platform.Storage;
using OneHub.Diagnostics.HeapView;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OneHub.Tools.HeapView
{
    class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public HeapSnapshot? CurrentData { get; private set; }

        public bool Loading { get; private set; }

        public async Task LoadDataAsync(IStorageFile storageFile)
        {
            try
            {
                Loading = true;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Loading)));

                var file = await storageFile.OpenReadAsync();
                var memoryStream = new MemoryStream((int)file.Length);
                await file.CopyToAsync(memoryStream);

                var heapDump = new GCHeapDump(memoryStream, storageFile.Name);
                var oldCurrentData = CurrentData;
                CurrentData = new HeapSnapshot(heapDump);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentData)));
            }
            finally
            {
                Loading = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Loading)));
            }
        }
    }
}
